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
/// comparisons enforce it, and both are deliberately set-based rather than count-based: a file
/// where one defect disappears and another appears has an unchanged count and a real regression.
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
		// An edit that only partially overlaps a declaration means the query captured something
		// that is not a leaf body. Splicing it would cut a declaration in half.
		foreach (var declaration in originalDeclarations)
		{
			foreach (var edit in edits)
			{
				var overlaps = declaration.Start < edit.SourceEnd && edit.SourceStart < declaration.End;
				if (!overlaps)
					continue;
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
		foreach (var edit in edits)
		{
			// No owner at all means the edit is not inside any declaration - a whole-file splice
			// passes every other check, because "everything was excised" is trivially consistent.
			var owner = FindInnermostOwner(originalDeclarations, edit);
			if (owner is null || !executableOwnerKinds.Contains(owner.Kind))
				return CodeStructureGateVerdict.RejectedEditOutsideAnExecutableBody;
		}

		var known = originalDefects
			.Select(static defect => (defect.Kind, defect.Start))
			.ToHashSet();
		foreach (var defect in compressedDefects)
		{
			// Unmappable means the defect sits inside text the splice invented, so it cannot
			// have existed before by construction.
			if (defect.IsUnmappable || !known.Contains((defect.Kind, defect.Start)))
				return CodeStructureGateVerdict.RejectedNewDefects;
		}

		var expected = originalDeclarations
			.Where(declaration => !IsExcised(declaration, edits))
			.Select(static declaration => (declaration.Kind, declaration.Name, declaration.Start))
			.ToList();
		var actual = compressedDeclarations
			.Select(static declaration => (declaration.Kind, declaration.Name, declaration.Start))
			.ToList();

		if (actual.Count < expected.Count)
			return CodeStructureGateVerdict.RejectedDeclarationsLost;
		if (actual.Count > expected.Count)
			return CodeStructureGateVerdict.RejectedDeclarationsAdded;

		for (var index = 0; index < expected.Count; index++)
		{
			if (expected[index] != actual[index])
			{
				return expected.Contains(actual[index])
					? CodeStructureGateVerdict.RejectedDeclarationsLost
					: CodeStructureGateVerdict.RejectedDeclarationsAdded;
			}
		}

		return CodeStructureGateVerdict.Accepted;
	}

	/// <summary>
	/// A declaration is excised when it lies wholly inside a removed range. A declaration that
	/// merely CONTAINS a removed range — every method whose body was cut — is not excised.
	/// </summary>
	/// <summary>
	/// The smallest declaration that strictly contains the edit, or null when the edit is not
	/// inside any declaration at all (top-level code, which no language pack should be producing).
	/// </summary>
	private static CodeDeclaration? FindInnermostOwner(
		IReadOnlyList<CodeDeclaration> declarations,
		CodeCompressionEdit edit)
	{
		CodeDeclaration? owner = null;
		foreach (var declaration in declarations)
		{
			if (declaration.Start > edit.SourceStart || declaration.End < edit.SourceEnd)
				continue;
			if (declaration.Start == edit.SourceStart && declaration.End == edit.SourceEnd)
				continue;
			if (owner is null || declaration.Length < owner.Length)
				owner = declaration;
		}

		return owner;
	}

	public static bool IsExcised(CodeDeclaration declaration, IReadOnlyList<CodeCompressionEdit> edits)
	{
		foreach (var edit in edits)
		{
			if (declaration.Start >= edit.SourceStart && declaration.End <= edit.SourceEnd)
				return true;
		}

		return false;
	}
}
