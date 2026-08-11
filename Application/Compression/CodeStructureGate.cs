namespace DevProjex.Application.Compression;

/// <summary>
/// A named declaration as seen in one parse: what it is, what it is called, and where it sits.
/// Positions are always in SOURCE coordinates — readings taken from the compressed text are
/// translated back through <see cref="ContentTransformMap"/> before they reach the gate.
/// </summary>
public sealed record CodeDeclaration(string Kind, string Name, int Start, int Length)
{
	public int End => Start + Length;
}

/// <summary>
/// An ERROR or MISSING node, in source coordinates. <see cref="Start"/> is negative when the node
/// lives inside a replacement and therefore has no source counterpart at all.
///
/// COLLECTION CONTRACT — do not replace the full tree walk with the parser's own flag.
/// A defect is any node where <c>IsError</c> or <c>IsMissing</c> holds, OR that is a named node of
/// zero width. The obvious shortcut, tree-sitter's <c>HasError</c>, was measured lying in BOTH
/// directions against the grammars this project ships:
///  * 243 of 993 DevProjex C# files report HasError=true with no ERROR or MISSING node anywhere in
///    the tree — the grammar does not understand C# 12 collection expressions and inserts
///    zero-width identifiers that the binding does not report as missing;
///  * Python "def f():" with an empty suite reports HasError=false while the tree does contain a
///    defect node, so the flag hides a real one.
/// Trusting the flag therefore both refuses a quarter of a modern C# codebase for nothing and lets
/// a genuinely broken splice through. The walk is the cheap half of the gate; the second parse is
/// the expensive half, and it runs on text that is already several times shorter.
/// </summary>
public sealed record CodeParseDefect(string Kind, int Start)
{
	public bool IsUnmappable => Start < 0;
}

/// <summary>Why the gate refused, so the refusal can be explained rather than just counted.</summary>
public enum CodeStructureGateVerdict
{
	Accepted,
	RejectedNewDefects,
	RejectedDeclarationsLost,
	RejectedDeclarationsAdded,
	RejectedEditCrossesDeclaration,
	RejectedEditOutsideAnExecutableBody
}

/// <summary>
/// Decides whether a compressed rendering may replace the original.
///
/// The rule is that compression may fail, but may not silently remove meaning. Two independent
/// comparisons enforce it. Defects are compared as a multiset rather than by total count or set:
/// a file where one defect disappears and another appears has an unchanged count and a real
/// regression, while two identical defects at one position must not collapse into one.
///
/// Declarations are compared against the declarations that were NOT excised. A method whose body
/// is removed keeps its own declaration but loses any local function declared inside it, so
/// comparing the raw sets would reject every such file forever — and it would do so silently,
/// because a rejection looks exactly like a conservative success.
/// </summary>
public static class CodeStructureGate
{
	public static CodeStructureGateVerdict Evaluate(
		IReadOnlyList<CodeDeclaration> originalDeclarations,
		IReadOnlyList<CodeDeclaration> compressedDeclarations,
		IReadOnlyList<CodeParseDefect> originalDefects,
		IReadOnlyList<CodeParseDefect> compressedDefects,
		IReadOnlyList<CodeCompressionEdit> edits,
		IReadOnlySet<string> executableOwnerKinds)
	{
		var orderedEdits = edits.Count <= 1
			? edits
			: edits.OrderBy(static edit => edit.SourceStart).ToArray();

		// An edit that only partially overlaps a declaration means the query captured something
		// that is not a leaf body. Splicing it would cut a declaration in half.
		foreach (var declaration in originalDeclarations)
		{
			for (var editIndex = FindFirstEditEndingAfter(orderedEdits, declaration.Start);
			     editIndex < orderedEdits.Count && orderedEdits[editIndex].SourceStart < declaration.End;
			     editIndex++)
			{
				var edit = orderedEdits[editIndex];
				var containedInEdit = declaration.Start >= edit.SourceStart && declaration.End <= edit.SourceEnd;
				var containsEdit = edit.SourceStart >= declaration.Start && edit.SourceEnd <= declaration.End;
				if (!containedInEdit && !containsEdit)
					return CodeStructureGateVerdict.RejectedEditCrossesDeclaration;
			}
		}

		// Without this, containment alone is self-justifying: an edit that swallows a whole
		// interface body makes every method inside it "excised", so the declaration comparison
		// expects them gone and the gate accepts wholesale deletion. Requiring the innermost
		// declaration around an edit to be one that legitimately OWNS an executable body is what
		// keeps "leaf bodies only" a property of the gate rather than a property of the query.
		var bodyEdits = orderedEdits
			.Where(static edit => (edit.Kinds & CodeTransformKinds.Bodies) != 0)
			.ToArray();
		var owners = ResolveInnermostOwners(originalDeclarations, bodyEdits);
		for (var editIndex = 0; editIndex < bodyEdits.Length; editIndex++)
		{
			// No owner at all means the edit is not inside any declaration - a whole-file splice
			// passes every other check, because "everything was excised" is trivially consistent.
			var owner = owners[editIndex];
			if (owner is null || !executableOwnerKinds.Contains(owner.Kind))
				return CodeStructureGateVerdict.RejectedEditOutsideAnExecutableBody;
		}

		var known = originalDefects
			.GroupBy(static defect => (defect.Kind, defect.Start))
			.ToDictionary(static group => group.Key, static group => group.Count());
		foreach (var defect in compressedDefects)
		{
			// Unmappable means the defect sits inside text the splice invented, so it cannot
			// have existed before by construction.
			var key = (defect.Kind, defect.Start);
			if (defect.IsUnmappable || !known.TryGetValue(key, out var remaining) || remaining == 0)
				return CodeStructureGateVerdict.RejectedNewDefects;
			known[key] = remaining - 1;
		}

		var expected = originalDeclarations
			.Where(declaration => !IsExcisedSorted(declaration, orderedEdits))
			.Select(static declaration => (declaration.Kind, declaration.Name, declaration.Start))
			.ToList();
		var actual = compressedDeclarations
			.Select(static declaration => (declaration.Kind, declaration.Name, declaration.Start))
			.ToList();

		if (actual.Count < expected.Count)
			return CodeStructureGateVerdict.RejectedDeclarationsLost;
		if (actual.Count > expected.Count)
			return CodeStructureGateVerdict.RejectedDeclarationsAdded;

		var sameOrder = true;
		for (var index = 0; index < expected.Count; index++)
		{
			if (expected[index] == actual[index])
				continue;

			sameOrder = false;
			break;
		}

		if (sameOrder)
			return CodeStructureGateVerdict.Accepted;

		// Query captures at the same source range have no stable traversal order after a splice.
		// Fall back to a multiset only on the uncommon reordered path so missing, invented and
		// duplicate declarations remain observable without taxing ordinary files.
		var remainingDeclarations = expected
			.GroupBy(static declaration => declaration)
			.ToDictionary(static group => group.Key, static group => group.Count());
		foreach (var declaration in actual)
		{
			if (!remainingDeclarations.TryGetValue(declaration, out var count) || count == 0)
			{
				return CodeStructureGateVerdict.RejectedDeclarationsAdded;
			}

			remainingDeclarations[declaration] = count - 1;
		}

		if (remainingDeclarations.Values.Any(static count => count != 0))
			return CodeStructureGateVerdict.RejectedDeclarationsLost;

		return CodeStructureGateVerdict.Accepted;
	}

	/// <summary>
	/// Resolves owners in ranges instead of scanning every declaration for every edit. The work is
	/// proportional to actual declaration nesting around edits, not to all unrelated declarations.
	/// </summary>
	private static CodeDeclaration?[] ResolveInnermostOwners(
		IReadOnlyList<CodeDeclaration> declarations,
		IReadOnlyList<CodeCompressionEdit> orderedEdits)
	{
		var owners = new CodeDeclaration?[orderedEdits.Count];
		foreach (var declaration in declarations)
		{
			for (var editIndex = FindFirstEditStartingAtOrAfter(orderedEdits, declaration.Start);
			     editIndex < orderedEdits.Count && orderedEdits[editIndex].SourceStart < declaration.End;
			     editIndex++)
			{
				var edit = orderedEdits[editIndex];
				if (edit.SourceEnd > declaration.End ||
				    declaration.Start == edit.SourceStart && declaration.End == edit.SourceEnd)
				{
					continue;
				}

				if (owners[editIndex] is null || declaration.Length < owners[editIndex]!.Length)
					owners[editIndex] = declaration;
			}
		}

		return owners;
	}

	/// <summary>
	/// A declaration is excised when it lies wholly inside a removed range. A declaration that
	/// merely CONTAINS a removed range — every method whose body was cut — is not excised.
	/// </summary>
	public static bool IsExcised(CodeDeclaration declaration, IReadOnlyList<CodeCompressionEdit> edits)
	{
		var orderedEdits = edits.Count <= 1
			? edits
			: edits.OrderBy(static edit => edit.SourceStart).ToArray();
		return IsExcisedSorted(declaration, orderedEdits);
	}

	private static bool IsExcisedSorted(
		CodeDeclaration declaration,
		IReadOnlyList<CodeCompressionEdit> orderedEdits)
	{
		var candidate = FindLastEditStartingAtOrBefore(orderedEdits, declaration.Start);
		return candidate >= 0 && declaration.End <= orderedEdits[candidate].SourceEnd;
	}

	private static int FindFirstEditEndingAfter(
		IReadOnlyList<CodeCompressionEdit> orderedEdits,
		int position)
	{
		var lower = 0;
		var upper = orderedEdits.Count;
		while (lower < upper)
		{
			var middle = lower + ((upper - lower) / 2);
			if (orderedEdits[middle].SourceEnd <= position)
				lower = middle + 1;
			else
				upper = middle;
		}

		return lower;
	}

	private static int FindFirstEditStartingAtOrAfter(
		IReadOnlyList<CodeCompressionEdit> orderedEdits,
		int position)
	{
		var lower = 0;
		var upper = orderedEdits.Count;
		while (lower < upper)
		{
			var middle = lower + ((upper - lower) / 2);
			if (orderedEdits[middle].SourceStart < position)
				lower = middle + 1;
			else
				upper = middle;
		}

		return lower;
	}

	private static int FindLastEditStartingAtOrBefore(
		IReadOnlyList<CodeCompressionEdit> orderedEdits,
		int position)
	{
		var lower = 0;
		var upper = orderedEdits.Count;
		while (lower < upper)
		{
			var middle = lower + ((upper - lower) / 2);
			if (orderedEdits[middle].SourceStart <= position)
				lower = middle + 1;
			else
				upper = middle;
		}

		return lower - 1;
	}
}
