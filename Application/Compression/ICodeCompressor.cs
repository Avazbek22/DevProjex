namespace DevProjex.Application.Compression;

/// <summary>
/// Produces compression plans for source files. Mirrors the boundary <see cref="Secrets.ISecretDetector"/>
/// establishes for secret detection: the Application layer owns the plan model, the splice and the
/// offset map, while the parsing engine and the per-language queries stay in Infrastructure.
///
/// Implementations must not read files. Classification and source IO remain owned by
/// <see cref="Kernel.Abstractions.IFileContentAnalyzer"/>, exactly as for detection.
/// </summary>
public interface ICodeCompressor
{
	/// <summary>
	/// Identity of the engine and its query set. It goes into cache keys, so a grammar or query
	/// change must change this string or stale plans will be served against new rules.
	/// </summary>
	string TransformIdentity { get; }

	/// <summary>
	/// True when a file with this extension has a language pack. Cheap enough to call per file
	/// before any content is read, so unsupported files never pay for parsing.
	/// </summary>
	bool IsSupported(string relativePath);

	/// <summary>
	/// Loads whatever the selection needs and nothing else. Grammars are expensive to materialize
	/// and load, so nothing is touched until a language actually appears in the selection.
	/// </summary>
	ICodeCompressionScope CreateScope(string projectRoot);
}

/// <summary>
/// A single output operation. Scopes are not thread-safe by themselves: parsers are pooled per
/// language inside, and callers must follow the same discipline the redaction scope uses — parallel
/// analysis, serial accumulation.
/// </summary>
public interface ICodeCompressionScope : IDisposable
{
	/// <summary>
	/// Analyses one file and returns a plan. Never throws for ordinary refusals: an unsupported
	/// language, a failed parse, a rejected gate or a result that did not shrink all come back as
	/// an unchanged plan carrying the reason.
	/// </summary>
	CodeCompressionPlan Plan(
		string fullPath,
		string relativePath,
		ReadOnlySpan<char> content,
		CancellationToken cancellationToken);
}
