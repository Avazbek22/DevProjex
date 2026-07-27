namespace DevProjex.Application.Context;

public enum ContextDiagnosticSeverity
{
	Information,
	Warning,
	Error
}

public sealed record ContextDiagnostic(
	string Code,
	ContextDiagnosticSeverity Severity,
	string Message,
	string? Path = null);

public sealed record ProjectContextGitReadiness(
	GitFilteringMode Mode,
	int LoadedTrackedIndexCount,
	bool IsReady,
	int UnavailableTrackedIndexCount = 0);

public sealed record ProjectContextPlan(
	string SourceRoot,
	ProjectSelectionSpec Selection,
	IReadOnlyList<string> AvailableRoots,
	IReadOnlyList<string> SelectedRoots,
	IReadOnlyList<string> AvailableExtensions,
	IReadOnlyList<string> SelectedExtensions,
	TreeNodeDescriptor EffectiveTree,
	TreeNodeDescriptor ProjectedTree,
	IReadOnlySet<string> SelectedFullPaths,
	IReadOnlyList<string> IncludedFiles,
	IReadOnlyList<string> IncludedFolders,
	ProjectAnalysisReport Analysis,
	IReadOnlyList<ContextDiagnostic> Diagnostics,
	ProjectContextGitReadiness GitReadiness,
	string Fingerprint,
	long IncludedBytes = 0,
	IReadOnlyDictionary<string, long>? EffectiveFileSizes = null)
{
	public bool HasErrors => Diagnostics.Any(static diagnostic =>
		diagnostic.Severity == ContextDiagnosticSeverity.Error);
}

public sealed record ProjectContextRequest(
	string ProjectPath,
	ProjectSelectionSpec Selection);

public sealed class ProjectContextValidationException(string code, string message)
	: ArgumentException(message)
{
	public string Code { get; } = code;
}
