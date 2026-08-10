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
	int UnavailableTrackedIndexCount = 0)
{
	public const string UnavailableDiagnosticCode = "DPX-GIT-TRACKED-INDEX-UNAVAILABLE";
	public const string PartialDiagnosticCode = "DPX-GIT-TRACKED-INDEX-PARTIAL";

	public static ProjectContextGitReadiness Evaluate(
		GitFilteringMode mode,
		int discoveredTrackedIndexCount,
		int unavailableTrackedIndexCount)
	{
		var unavailableCount = Math.Clamp(
			unavailableTrackedIndexCount,
			0,
			Math.Max(0, discoveredTrackedIndexCount));
		var loadedCount = Math.Max(0, discoveredTrackedIndexCount - unavailableCount);
		return new ProjectContextGitReadiness(
			mode,
			loadedCount,
			mode != GitFilteringMode.TrackedFilesOnly || loadedCount > 0,
			unavailableCount);
	}

	public static ProjectContextGitReadiness Evaluate(
		GitFilteringMode mode,
		ProjectTreeInventorySnapshot? inventory)
	{
		var indexes = inventory?.DiscoveredGitTrackedPathIndexes;
		return Evaluate(
			mode,
			indexes?.Count ?? 0,
			indexes?.Count(static index => !index.IsAvailable) ?? 0);
	}

	public ContextDiagnostic? CreateDiagnostic(string sourceRoot)
	{
		if (Mode != GitFilteringMode.TrackedFilesOnly)
			return null;

		if (!IsReady)
		{
			return new ContextDiagnostic(
				UnavailableDiagnosticCode,
				ContextDiagnosticSeverity.Error,
				"Tracked Git files mode was requested, but no readable Git index is available.",
				sourceRoot);
		}

		return UnavailableTrackedIndexCount > 0
			? new ContextDiagnostic(
				PartialDiagnosticCode,
				ContextDiagnosticSeverity.Warning,
				"Some nested Git indexes could not be read; those repository scopes were excluded.",
				sourceRoot)
			: null;
	}
}

public sealed record ProjectSourceIdentity(
	string DisplayName,
	ProjectSourceType SourceType,
	string SourceReference,
	string? RepositoryUrl = null,
	string? Branch = null,
	string? CommitHash = null,
	bool IsCachedRepository = false);

public sealed record SecretRedactionSummary(int MatchedCount, int RedactedCount);

public sealed record CodeCompressionSummary(
	int CompressedFiles,
	int UnchangedFiles,
	long SourceCharacters,
	long TransformedCharacters,
	int BodyTransformedFiles = 0,
	int CommentTransformedFiles = 0);

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
	IReadOnlyDictionary<string, long>? EffectiveFileSizes = null,
	ProjectSourceIdentity? SourceIdentity = null,
	SecretRedactionSummary? Redaction = null,
	CodeCompressionSummary? Compression = null)
{
	public bool HasErrors => Diagnostics.Any(static diagnostic =>
		diagnostic.Severity == ContextDiagnosticSeverity.Error);
}

public sealed record ProjectContextRequest(
	string ProjectPath,
	ProjectSelectionSpec Selection,
	ProjectSourceIdentity? SourceIdentity = null);

public sealed class ProjectContextValidationException(string code, string message)
	: ArgumentException(message)
{
	public string Code { get; } = code;
}
