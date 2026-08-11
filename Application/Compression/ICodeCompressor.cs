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
	/// True when the language pack can apply at least one requested edit family. The default keeps
	/// existing test and third-party implementations source-compatible; data-driven compressors
	/// should override this to preserve unsupported-language fast paths per mode.
	/// </summary>
	bool IsSupported(string relativePath, CodeTransformKinds kinds) => IsSupported(relativePath);

	/// <summary>
	/// Returns the edit families that can actually affect this file. Implementations that cannot
	/// prove a narrower capability set conservatively keep every requested kind, preventing plans
	/// from different modes from sharing a cache entry. Callers use this only after
	/// <see cref="IsSupported(string, CodeTransformKinds)"/> succeeds.
	/// </summary>
	CodeTransformKinds GetEffectiveTransformKinds(string relativePath, CodeTransformKinds kinds) =>
		kinds;

	/// <summary>
	/// Loads whatever the selection needs and nothing else. Grammars are expensive to materialize
	/// and load, so nothing is touched until a language actually appears in the selection.
	/// </summary>
	ICodeCompressionScope CreateScope(string projectRoot);

	/// <summary>
	/// Creates one parse operation for the requested edit families. Implementations that only
	/// support the original body-compression contract keep working for that mode and fail loudly
	/// rather than silently applying body edits to a comments-only request.
	/// </summary>
	ICodeCompressionScope CreateScope(string projectRoot, CodeTransformKinds kinds) =>
		kinds == CodeTransformKinds.Bodies
			? CreateScope(projectRoot)
			: throw new NotSupportedException(
				$"The compressor does not support transformation mode '{kinds}'.");
}

/// <summary>Optional native-runtime facts used by bounded orchestration and developer diagnostics.</summary>
public interface ICodeCompressionRuntimeDiagnosticsProvider
{
	int AnalysisWorkerCapacity { get; }

	CodeCompressionRuntimeDiagnosticSnapshot CaptureRuntimeDiagnostics();
}

public readonly record struct CodeCompressionRuntimeDiagnosticSnapshot(
	int CompiledQuerySets,
	int MaterializedWorkers,
	int AvailableWorkers,
	int LeasedWorkers,
	int GlobalWorkerCapacity,
	int GlobalActiveWorkers,
	int GlobalPeakActiveWorkers,
	int GlobalRetainedWorkers,
	int GlobalRetainedWorkerCapacity)
{
	public static CodeCompressionRuntimeDiagnosticSnapshot Empty { get; } = new();
}

/// <summary>
/// A single output operation. Implementations must support concurrent analysis: metrics and export
/// pipelines process independent files in parallel while the scope keeps one coherent operation.
/// </summary>
public interface ICodeCompressionScope : IDisposable
{
	/// <summary>
	/// Analyses one file and returns a plan. Never throws for ordinary refusals: an unsupported
	/// language, a failed parse, a rejected gate or a result that did not shrink all come back as
	/// an unchanged plan carrying the reason.
	/// </summary>
	CodeCompressionAnalysis Analyze(
		string fullPath,
		string relativePath,
		string content,
		CancellationToken cancellationToken);
}

/// <summary>
/// A validated plan and, on a cache miss, the already materialized text used by the reverse-parse
/// gate. Reusing that text avoids applying every new plan twice.
/// </summary>
public sealed record CodeCompressionAnalysis(
	CodeCompressionPlan Plan,
	CodeCompressionResult? ValidatedResult)
{
	public CodeCompressionResult GetResult(string source) =>
		ValidatedResult ?? Plan.Apply(source);
}
