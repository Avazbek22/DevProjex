namespace DevProjex.Application.Dependencies;

public sealed record PreparedDependencySource(
	string FullPath,
	string RelativePath,
	string ScopeId,
	LanguageId LanguageId,
	string ContentFingerprint,
	string ExtractorIdentity,
	string Source,
	DependencyFileStatus PreparedStatus = DependencyFileStatus.Supported,
	string? PreparedStatusReason = null,
	bool CanCache = true);

public sealed class DependencyManifestContentIdentities
{
	public DependencyManifestContentIdentities(IReadOnlyDictionary<string, string> byFullPath)
	{
		ArgumentNullException.ThrowIfNull(byFullPath);
		var normalized = new Dictionary<string, string>(byFullPath.Count, PathComparer.Default);
		foreach (var pair in byFullPath)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
			ArgumentNullException.ThrowIfNull(pair.Value);
			normalized[Path.GetFullPath(pair.Key)] = pair.Value;
		}
		ByFullPath = normalized;
	}

	public IReadOnlyDictionary<string, string> ByFullPath { get; }
}

public sealed record DependencyResolverConfiguration(
	string Fingerprint,
	IReadOnlyList<DependencyScopeDescriptor> Scopes,
	IReadOnlyDictionary<string, PackageMapDescriptor> PackageMaps,
	IReadOnlySet<string> DotNetExternalSymbols,
	IReadOnlyDictionary<string, IReadOnlySet<string>> PythonStandardLibraryModules,
	IReadOnlySet<string> NodeBuiltInModules)
{
	public IReadOnlyList<DependencyConfigurationDiagnostic> ConfigurationDiagnostics { get; init; } = [];
	public bool CanCache { get; init; } = true;
	public IReadOnlyList<string> AbsentControlFiles { get; init; } = [];

	public DependencyScopeDescriptor? FindScope(string scopeId) =>
		Scopes.FirstOrDefault(scope => string.Equals(scope.ScopeId, scopeId, StringComparison.Ordinal));
}

public sealed record DependencyScopeDescriptor(
	string ScopeId,
	string Root,
	LanguageId LanguageId,
	IReadOnlyList<string> ProjectReferences,
	string? ModuleResolution,
	bool LegacyTypeScriptConfiguration,
	IReadOnlyDictionary<string, IReadOnlyList<string>> TypeScriptPaths,
	string? PackageName,
	IReadOnlySet<string> PythonExternalPackages,
	IReadOnlyList<string> PythonRoots,
	bool HasConfiguration,
	string? PythonVersion = null,
	bool AllowJavaScript = false)
{
	public DependencyConfigurationState ConfigurationState { get; init; } = DependencyConfigurationState.Valid;
	public string? ConfigurationDiagnostic { get; init; }
}

public sealed record PackageMapDescriptor(
	string Directory,
	string? PackageName,
	IReadOnlyDictionary<string, PackageTargetDescriptor> Imports,
	IReadOnlyDictionary<string, PackageTargetDescriptor> Exports,
	string? ModuleType,
	IReadOnlySet<string> ExternalPackages)
{
	public DependencyConfigurationState ConfigurationState { get; init; } = DependencyConfigurationState.Valid;
	public string? ConfigurationDiagnostic { get; init; }
}

public enum DependencyConfigurationState
{
	Valid,
	Missing,
	Corrupt,
	UnsupportedSemantics
}

public sealed record DependencyConfigurationDiagnostic(
	string Path,
	DependencyConfigurationState State,
	string Reason,
	IReadOnlyList<string> ScopeIds);

public enum PackageTargetKind
{
	Path,
	Blocked,
	Conditions,
	Unsupported
}

public sealed record PackageTargetDescriptor(
	PackageTargetKind Kind,
	string? Path,
	IReadOnlyList<PackageConditionDescriptor> Conditions,
	string? UnsupportedReason);

public sealed record PackageConditionDescriptor(string Name, PackageTargetDescriptor Target);

public interface IDependencyFactExtractor : IDisposable
{
	ValueTask<PreparedDependencySource> PrepareAsync(
		string sourceRoot,
		string fullPath,
		DependencyResolverConfiguration configuration,
		DependencyFactsLimits limits,
		CancellationToken cancellationToken,
		string? contentIdentity = null);

	FileFacts Extract(PreparedDependencySource source, DependencyFactsLimits limits);

	int ParseCount { get; }
	int CompiledQuerySetCount { get; }
}

public interface IDependencyConfigurationProvider
{
	Task<DependencyResolverConfiguration> ReadAsync(
		string sourceRoot,
		IReadOnlyList<string> manifestFiles,
		CancellationToken cancellationToken);
}

public sealed record DependencyFactsLimits(
	int MaximumCharactersPerFile = 2 * 1024 * 1024,
	int MaximumFactsPerFile = 50_000,
	int MaximumEdgesPerFile = 20_000,
	int MaximumWorkPerIndex = 5_000_000,
	int MaximumCachedFiles = 8_192,
	int MaximumCachedIndexes = 16,
	long MaximumFileCacheBytes = 64L * 1024 * 1024,
	long MaximumIndexCacheBytes = 128L * 1024 * 1024);
