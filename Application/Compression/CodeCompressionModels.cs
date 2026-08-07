namespace DevProjex.Application.Compression;

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
	///    an expression body like "=> a" is smaller than any placeholder worth printing;
	///  * a file whose total would not shrink comes back as <see cref="CodeCompressionOutcome.UnchangedNoBenefit"/>.
	/// </summary>
	public static CodeCompressionPlan Create(
		string relativePath,
		string languageId,
		IReadOnlyList<CodeCompressionEdit> edits,
		int sourceLength,
		string transformIdentity)
	{
		var ordered = edits
			.Where(static edit => edit.Replacement.Length < edit.SourceLength)
			.OrderBy(static edit => edit.SourceStart)
			.ToArray();
		var transformedLength = sourceLength;
		var previousEnd = 0;
		foreach (var edit in ordered)
		{
			if (edit.SourceStart < 0 || edit.SourceLength < 0 || edit.SourceEnd > sourceLength)
				throw new ArgumentOutOfRangeException(nameof(edits), $"Edit [{edit.SourceStart}, {edit.SourceEnd}) leaves the file of {sourceLength} characters.");
			if (edit.SourceStart < previousEnd)
				throw new ArgumentException($"Edit [{edit.SourceStart}, {edit.SourceEnd}) overlaps the previous one ending at {previousEnd}.", nameof(edits));
			previousEnd = edit.SourceEnd;
			transformedLength += edit.Replacement.Length - edit.SourceLength;
		}

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
	{
		ArgumentNullException.ThrowIfNull(source);
		if (source.Length != SourceLength)
			throw new ArgumentException($"The plan was built for {SourceLength} characters but the text has {source.Length}.", nameof(source));
		return !HasEdits
			? new CodeCompressionResult(source, ContentTransformMap.Identity)
			: ApplyCore(source.AsSpan());
	}

	public CodeCompressionResult Apply(ReadOnlySpan<char> source)
	{
		if (source.Length != SourceLength)
			throw new ArgumentException($"The plan was built for {SourceLength} characters but the text has {source.Length}.", nameof(source));
		if (!HasEdits)
			return new CodeCompressionResult(source.ToString(), ContentTransformMap.Identity);
		return ApplyCore(source);
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
}

public sealed record CodeCompressionResult(string Text, ContentTransformMap Map);

internal sealed record CodeCompressionExecution(
	CodeCompressionPlan Plan,
	CodeCompressionResult Output);
