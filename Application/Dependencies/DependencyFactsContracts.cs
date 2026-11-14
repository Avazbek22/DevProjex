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
	bool CanCache = true,
	DependencySourceObservation ContentObservation = default);

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
	/// <summary>
	/// Control files read from outside the manifest and the directories searched for them, stamped
	/// before they were read, so a cached snapshot can notice an edit or a newly created file.
	/// </summary>
	public IReadOnlyList<DependencyControlPathStamp> ObservedControlPaths { get; init; } = [];

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
	bool AllowJavaScript = false,
	IReadOnlyList<string>? TypeScriptModuleSuffixes = null,
	IReadOnlyList<string>? TypeScriptRootDirectories = null)
{
	public DependencyConfigurationState ConfigurationState { get; init; } = DependencyConfigurationState.Valid;
	public string? ConfigurationDiagnostic { get; init; }
	public bool HasTypeScriptCustomConditions { get; init; }
	public bool DisableTransitiveProjectReferences { get; init; }
	public IReadOnlySet<string> RubyExternalPackages { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	public IReadOnlyList<string> RustTargetRoots { get; init; } = [];
	public IReadOnlyList<string> CIncludeDirectories { get; init; } = [];
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

public readonly record struct DependencyControlPathStamp(
	string Path,
	bool IsDirectory,
	long Length,
	long LastWriteTimeUtcTicks,
	long CreationTimeUtcTicks)
{
	public static DependencyControlPathStamp? TryCapture(string path, bool isDirectory)
	{
		try
		{
			FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
			if (!info.Exists)
				return null;
			return new DependencyControlPathStamp(
				path,
				isDirectory,
				info is FileInfo file ? file.Length : 0,
				info.LastWriteTimeUtc.Ticks,
				info.CreationTimeUtc.Ticks);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
			System.Security.SecurityException or ArgumentException or NotSupportedException)
		{
			return null;
		}
	}

	public bool IsCurrent() => TryCapture(Path, IsDirectory) == this;
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

	FileFacts Extract(
		PreparedDependencySource source,
		DependencyFactsLimits limits,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return Extract(source, limits);
	}

	/// <summary>
	/// The language and owning scope <see cref="PrepareAsync"/> would give <paramref name="fullPath"/>,
	/// decided from the path and the configuration alone, without reading the file.
	/// </summary>
	(LanguageId LanguageId, string ScopeId) DescribePath(string fullPath, DependencyResolverConfiguration configuration) =>
		throw new NotSupportedException();

	int ParseCount { get; }
	int CompiledQuerySetCount { get; }
}

public interface IDependencyNavigationExtractor
{
	IReadOnlyList<NavigationDeclaration> ExtractNavigation(
		string relativePath,
		string source,
		string contentFingerprint,
		CancellationToken cancellationToken);
}

public interface IDependencyConfigurationProvider
{
	Task<DependencyResolverConfiguration> ReadAsync(
		string sourceRoot,
		IReadOnlyList<string> manifestFiles,
		CancellationToken cancellationToken);

	/// <param name="readableProjectFiles">
	/// Absolute paths of the files the caller may read. A control file outside the manifest is
	/// read only when it is one of them; null applies no such bound.
	/// </param>
	Task<DependencyResolverConfiguration> ReadAsync(
		string sourceRoot,
		IReadOnlyList<string> manifestFiles,
		IReadOnlySet<string>? readableProjectFiles,
		CancellationToken cancellationToken) =>
		ReadAsync(sourceRoot, manifestFiles, cancellationToken);
}

public sealed record DependencyFactsLimits(
	int MaximumCharactersPerFile = 2 * 1024 * 1024,
	int MaximumFactsPerFile = 50_000,
	int MaximumRawCapturesPerFile = 1_000_000,
	int MaximumEdgesPerFile = 20_000,
	int MaximumWorkPerIndex = 5_000_000,
	int MaximumCachedFiles = 8_192,
	int MaximumCachedIndexes = 16,
	long MaximumFileCacheBytes = 64L * 1024 * 1024,
	long MaximumIndexCacheBytes = 128L * 1024 * 1024,
	long MaximumAccumulatedFactBytes = 128L * 1024 * 1024);
