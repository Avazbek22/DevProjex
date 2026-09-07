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
	string? PreparedStatusReason = null);

public sealed record DependencyResolverConfiguration(
	string Fingerprint,
	IReadOnlyList<DependencyScopeDescriptor> Scopes,
	IReadOnlyDictionary<string, PackageMapDescriptor> PackageMaps,
	IReadOnlySet<string> DotNetExternalSymbols,
	IReadOnlyDictionary<string, IReadOnlySet<string>> PythonStandardLibraryModules,
	IReadOnlySet<string> NodeBuiltInModules)
{
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
	string? PythonVersion = null);

public sealed record PackageMapDescriptor(
	string Directory,
	string? PackageName,
	IReadOnlyDictionary<string, string?> Imports,
	IReadOnlyDictionary<string, string?> Exports,
	string? ModuleType,
	IReadOnlySet<string> ExternalPackages);

public interface IDependencyFactExtractor : IDisposable
{
	ValueTask<PreparedDependencySource> PrepareAsync(
		string sourceRoot,
		string fullPath,
		DependencyResolverConfiguration configuration,
		CancellationToken cancellationToken);

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
