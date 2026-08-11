using DevProjex.Application.Diagnostics;

namespace DevProjex.Application.Compression;

[Flags]
public enum CodeTransformKinds
{
	None = 0,
	Bodies = 1,
	Comments = 2
}

public static class CodeTransformIdentity
{
	public static CodeTransformKinds Resolve(bool compressBodies, bool stripComments) =>
		(compressBodies ? CodeTransformKinds.Bodies : CodeTransformKinds.None) |
		(stripComments ? CodeTransformKinds.Comments : CodeTransformKinds.None);

	public static string Create(string engineIdentity, CodeTransformKinds kinds) =>
		kinds switch
		{
			CodeTransformKinds.Bodies => engineIdentity + "+bodies",
			CodeTransformKinds.Comments => engineIdentity + "+comments",
			CodeTransformKinds.Bodies | CodeTransformKinds.Comments =>
				engineIdentity + "+bodies+comments",
			_ => throw new ArgumentOutOfRangeException(nameof(kinds), kinds, null)
		};
}

/// <summary>
/// Why a file looks the way it does in the output. Every value except
/// <see cref="Compressed"/> means the original bytes were kept, and each is surfaced to the user
/// as a reason: "left full" without a cause reads as a random refusal.
/// </summary>
public enum CodeCompressionOutcome
{
	Compressed,
	UnchangedUnsupportedLanguage,
	UnchangedTooLarge,
	UnchangedParseFailed,
	UnchangedGateRejected,
	UnchangedNoBenefit
}

/// <summary>
/// One replaced range of the original text. Edits describe surgery on the source, not a rendered
/// document: the signature around a body stays the author's own bytes, so generics, constraints,
/// ref/out/in, nullable annotations, attributes and lifetimes survive without being reconstructed.
/// </summary>
public sealed record CodeCompressionEdit(int SourceStart, int SourceLength, string Replacement)
{
	public CodeTransformKinds Kinds { get; init; } = CodeTransformKinds.Bodies;

	public int SourceEnd => SourceStart + SourceLength;
}

/// <summary>
/// The result of analysing one file: what to cut, and what it costs. A plan is data — applying it
/// is a pure function of the plan and the original text, so preview, clipboard, context exports
/// and the CLI all produce identical bytes from the same plan.
///
/// A plan with no edits is the normal shape for every non-compressed outcome.
/// </summary>
public sealed record CodeCompressionPlan(
	string RelativePath,
	string LanguageId,
	CodeCompressionOutcome Outcome,
	IReadOnlyList<CodeCompressionEdit> Edits,
	int SourceLength,
	int TransformedLength,
	string TransformIdentity)
{
	public bool HasEdits => Edits.Count > 0;

	public CodeTransformKinds AffectedKinds => Edits.Aggregate(
		CodeTransformKinds.None,
		static (kinds, edit) => kinds | edit.Kinds);

	/// <summary>Characters saved. Never negative: a plan that does not shrink is not applied.</summary>
	public int SavedCharacters => SourceLength - TransformedLength;

	public static CodeCompressionPlan Unchanged(
		string relativePath,
		string languageId,
		CodeCompressionOutcome outcome,
		int sourceLength,
		string transformIdentity) =>
		new(relativePath, languageId, outcome, [], sourceLength, sourceLength, transformIdentity);

	/// <summary>Same file, no edits, carrying the reason it was left alone.</summary>
	public CodeCompressionPlan ToUnchanged(CodeCompressionOutcome outcome) =>
		this with { Outcome = outcome, Edits = [], TransformedLength = SourceLength };

	/// <summary>
	/// Builds a plan from edits, rejecting anything that could corrupt the splice. Edits are sorted
	/// and validated here rather than trusted, because an out-of-range or overlapping span produces
	/// silently mangled output instead of an error.
	///
	/// Two size rules are applied here, before any text is produced, so a file can never be made
	/// larger by enabling compression:
	///  * an individual edit is dropped unless its replacement is shorter than what it replaces —
	///    a tiny captured body is not worth replacing with a placeholder;
	///  * a file whose total would not shrink comes back as <see cref="CodeCompressionOutcome.UnchangedNoBenefit"/>.
	/// </summary>
	public static CodeCompressionPlan Create(
		string relativePath,
		string languageId,
		IReadOnlyList<CodeCompressionEdit> edits,
		int sourceLength,
		string transformIdentity) =>
		CreateForAnalysis(
			relativePath,
			languageId,
			edits,
			sourceLength,
			transformIdentity,
			CancellationToken.None);

	/// <summary>
	/// Builds and validates a plan while observing cancellation during large edit collections.
	/// </summary>
	public static CodeCompressionPlan CreateForAnalysis(
		string relativePath,
		string languageId,
		IReadOnlyList<CodeCompressionEdit> edits,
		int sourceLength,
		string transformIdentity,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var beneficial = new List<CodeCompressionEdit>(edits.Count);
		for (var index = 0; index < edits.Count; index++)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, index);
			var edit = edits[index];
			if (edit.Replacement.Length < edit.SourceLength)
				beneficial.Add(edit);
		}
		cancellationToken.ThrowIfCancellationRequested();
		beneficial.Sort(static (left, right) => left.SourceStart.CompareTo(right.SourceStart));
		cancellationToken.ThrowIfCancellationRequested();
		var ordered = beneficial.ToArray();
		var transformedLength = sourceLength;
		var previousEnd = 0;
		for (var index = 0; index < ordered.Length; index++)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, index);
			var edit = ordered[index];
			if (edit.SourceStart < 0 || edit.SourceLength < 0 || edit.SourceEnd > sourceLength)
				throw new ArgumentOutOfRangeException(nameof(edits), $"Edit [{edit.SourceStart}, {edit.SourceEnd}) leaves the file of {sourceLength} characters.");
			if (edit.SourceStart < previousEnd)
				throw new ArgumentException($"Edit [{edit.SourceStart}, {edit.SourceEnd}) overlaps the previous one ending at {previousEnd}.", nameof(edits));
			previousEnd = edit.SourceEnd;
			transformedLength += edit.Replacement.Length - edit.SourceLength;
		}
		cancellationToken.ThrowIfCancellationRequested();

		if (ordered.Length == 0 || transformedLength >= sourceLength)
			return Unchanged(relativePath, languageId, CodeCompressionOutcome.UnchangedNoBenefit, sourceLength, transformIdentity);

		return new CodeCompressionPlan(
			relativePath,
			languageId,
			CodeCompressionOutcome.Compressed,
			ordered,
			sourceLength,
			transformedLength,
			transformIdentity);
	}

	/// <summary>
	/// Applies the plan, returning the transformed text and the map that translates offsets between
	/// the two texts in both directions.
	/// </summary>
	public CodeCompressionResult Apply(string source)
		=> ApplyForAnalysis(source, CancellationToken.None);

	/// <summary>
	/// Applies the plan while observing cancellation during large map and splice operations.
	/// </summary>
	public CodeCompressionResult ApplyForAnalysis(
		string source,
		CancellationToken cancellationToken)
	{
		ContentPipelineDiagnostics.RecordPlanApply();
		ArgumentNullException.ThrowIfNull(source);
		cancellationToken.ThrowIfCancellationRequested();
		if (source.Length != SourceLength)
			throw new ArgumentException($"The plan was built for {SourceLength} characters but the text has {source.Length}.", nameof(source));
		return !HasEdits
			? new CodeCompressionResult(source, ContentTransformMap.Identity)
			: ApplyCore(
				source,
				ContentTransformMap.CreateForAnalysis(Edits, SourceLength, cancellationToken),
				cancellationToken);
	}

	internal CodeCompressionResult Apply(string source, ContentTransformMap map)
	{
		ContentPipelineDiagnostics.RecordPlanApply();
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(map);
		if (source.Length != SourceLength)
			throw new ArgumentException($"The plan was built for {SourceLength} characters but the text has {source.Length}.", nameof(source));
		return !HasEdits
			? new CodeCompressionResult(source, ContentTransformMap.Identity)
			: ApplyCore(source, map, CancellationToken.None);
	}

	public CodeCompressionResult Apply(ReadOnlySpan<char> source)
	{
		ContentPipelineDiagnostics.RecordPlanApply();
		if (source.Length != SourceLength)
			throw new ArgumentException($"The plan was built for {SourceLength} characters but the text has {source.Length}.", nameof(source));
		if (!HasEdits)
			return new CodeCompressionResult(source.ToString(), ContentTransformMap.Identity);
		return ApplyCore(source);
	}

	private CodeCompressionResult ApplyCore(
		string source,
		ContentTransformMap map,
		CancellationToken cancellationToken)
	{
		var text = string.Create(
			TransformedLength,
			(Source: source, Edits, CancellationToken: cancellationToken),
			static (destination, state) =>
			{
				var sourceCursor = 0;
				var destinationCursor = 0;
				for (var index = 0; index < state.Edits.Count; index++)
				{
					ThrowIfCancellationRequestedPeriodically(state.CancellationToken, index);
					var edit = state.Edits[index];
					var retained = state.Source.AsSpan(sourceCursor, edit.SourceStart - sourceCursor);
					retained.CopyTo(destination[destinationCursor..]);
					destinationCursor += retained.Length;
					edit.Replacement.AsSpan().CopyTo(destination[destinationCursor..]);
					destinationCursor += edit.Replacement.Length;
					sourceCursor = edit.SourceEnd;
				}

				state.Source.AsSpan(sourceCursor).CopyTo(destination[destinationCursor..]);
			});
		cancellationToken.ThrowIfCancellationRequested();
		return new CodeCompressionResult(text, map);
	}

	private CodeCompressionResult ApplyCore(ReadOnlySpan<char> source)
	{
		var builder = new StringBuilder(TransformedLength);
		var cursor = 0;
		foreach (var edit in Edits)
		{
			builder.Append(source[cursor..edit.SourceStart]);
			builder.Append(edit.Replacement);
			cursor = edit.SourceEnd;
		}

		builder.Append(source[cursor..]);
		return new CodeCompressionResult(builder.ToString(), ContentTransformMap.Create(Edits, SourceLength));
	}

	private static void ThrowIfCancellationRequestedPeriodically(
		CancellationToken cancellationToken,
		int iteration)
	{
		if (cancellationToken.CanBeCanceled && iteration != 0 && (iteration & 1023) == 0)
			cancellationToken.ThrowIfCancellationRequested();
	}
}

public sealed record CodeCompressionResult(string Text, ContentTransformMap Map);

internal sealed record CodeCompressionExecution(
	CodeCompressionPlan Plan,
	CodeCompressionResult? Output);
