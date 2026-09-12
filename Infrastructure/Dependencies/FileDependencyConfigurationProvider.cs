using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DevProjex.Application.Dependencies;
using DevProjex.Application.Services;
using Tomlyn;
using Tomlyn.Model;

namespace DevProjex.Infrastructure.Dependencies;

public sealed partial class FileDependencyConfigurationProvider : IDependencyConfigurationProvider
{
	internal const int MaximumConfigurationBytes = 4 * 1024 * 1024;
	internal const int MaximumTypeScriptExtendsDepth = 8;
	internal const string TypeScriptExtendsShapeReason = "tsconfig extends must be one relative path string";
	internal const string TypeScriptExtendsPackageReason = "tsconfig package extends is not supported";
	internal const string TypeScriptExtendsOutsideRootReason = "tsconfig extends must stay inside the project root";
	internal const string TypeScriptExtendsCycleReason = "tsconfig extends cycle is not supported";
	internal const string TypeScriptExtendsDepthReason = "tsconfig extends exceeds the maximum depth";
	internal const string TypeScriptExtendsUnavailableReason = "extended tsconfig is unavailable";
	internal const string TypeScriptModuleResolutionReason = "tsconfig moduleResolution is not supported";
	internal const string TypeScriptCustomConditionsReason = "tsconfig customConditions are not supported";
	internal const string TypeScriptRootDirectoriesOutsideRootReason = "tsconfig rootDirs must stay inside the project root";
	internal const string ProjectReferenceConditionReason = "project reference condition could not be evaluated safely";
	internal const string CompileItemMembershipReason = "C# Compile item membership is not supported";
	internal const string DisableTransitiveProjectReferencesReason = "DisableTransitiveProjectReferences could not be evaluated safely";
	internal const string InvalidPyProjectReason = "invalid pyproject TOML";
	internal const string AmbiguousConfigurationOwnershipReason = "multiple owning dependency configurations";
	private readonly IDependencyControlFileReader _reader;
	private readonly IDependencyPathMetadata _pathMetadata;

	public FileDependencyConfigurationProvider()
		: this(new BoundedDependencyControlFileReader(), new DependencyPathMetadata())
	{
	}

	public FileDependencyConfigurationProvider(FileContentReadStreamOpener sourceOpener)
		: this(
			new BoundedDependencyControlFileReader(
				sourceOpener ?? throw new ArgumentNullException(nameof(sourceOpener))),
			new DependencyPathMetadata())
	{
	}

	internal FileDependencyConfigurationProvider(IDependencyControlFileReader reader)
		: this(reader, new DependencyPathMetadata())
	{
	}

	internal FileDependencyConfigurationProvider(
		IDependencyControlFileReader reader,
		IDependencyPathMetadata pathMetadata)
	{
		_reader = reader ?? throw new ArgumentNullException(nameof(reader));
		_pathMetadata = pathMetadata ?? throw new ArgumentNullException(nameof(pathMetadata));
	}

	public async Task<DependencyResolverConfiguration> ReadAsync(
		string sourceRoot,
		IReadOnlyList<string> manifestFiles,
		CancellationToken cancellationToken)
	{
		var root = Path.GetFullPath(sourceRoot);
		var manifest = manifestFiles.Select(Path.GetFullPath).ToHashSet(PathComparer);
		var scopes = new List<DependencyScopeDescriptor>();
		var fingerprintParts = new List<string>();
		var csharpProjects = new Dictionary<string, (
			string Scope,
			string[] References,
			bool DisableTransitiveReferences,
			DependencyConfigurationState State,
			string? Reason)>(PathComparer);
		var snapshots = new Dictionary<string, Task<DependencyControlFileSnapshot>>(PathComparer);
		var packageProjections = new Dictionary<string, Task<ConfigurationParseResult<PackageMapDescriptor>>>(PathComparer);
		var typeScriptLayerProjections = new Dictionary<string, Task<ConfigurationParseResult<TypeScriptConfigurationLayer>>>(PathComparer);
		var diagnostics = new List<DependencyConfigurationDiagnostic>();
		var absentControlFiles = new HashSet<string>(PathComparer);
		var fingerprintedControlFiles = new HashSet<string>(PathComparer);
		var transientReadFailure = 0;
		var projectFiles = new List<string>();
		var typeScriptConfigFiles = new List<string>();
		var pythonConfigFiles = new List<string>();
		var packageFiles = new List<string>();
		var javaConfigFiles = new List<string>();
		var rustConfigFiles = new List<string>();
		var rubyConfigFiles = new List<string>();
		var composerConfigFiles = new List<string>();
		var cConfigFiles = new List<string>();
		foreach (var path in manifest.Order(StringComparer.Ordinal))
		{
			if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) projectFiles.Add(path);
			if (IsTypeScriptConfig(path)) typeScriptConfigFiles.Add(path);
			if (IsPythonConfig(path)) pythonConfigFiles.Add(path);
			if (Path.GetFileName(path).Equals("package.json", StringComparison.OrdinalIgnoreCase)) packageFiles.Add(path);
			if (IsJavaConfig(path)) javaConfigFiles.Add(path);
			if (Path.GetFileName(path).Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase)) rustConfigFiles.Add(path);
			if (Path.GetFileName(path).Equals("Gemfile", StringComparison.OrdinalIgnoreCase) ||
			    path.EndsWith(".gemspec", StringComparison.OrdinalIgnoreCase)) rubyConfigFiles.Add(path);
			if (Path.GetFileName(path).Equals("composer.json", StringComparison.OrdinalIgnoreCase)) composerConfigFiles.Add(path);
			if (Path.GetFileName(path).Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase)) cConfigFiles.Add(path);
		}

		Task<DependencyControlFileSnapshot> ReadSnapshotAsync(string path)
		{
			if (snapshots.TryGetValue(path, out var existing)) return existing;
			var created = ReadTrackedSnapshotAsync(path);
			snapshots[path] = created;
			return created;
		}

		async Task<DependencyControlFileSnapshot> ReadTrackedSnapshotAsync(string path)
		{
			var snapshot = await _reader.ReadAsync(path, MaximumConfigurationBytes, cancellationToken).ConfigureAwait(false);
			if (snapshot.State == DependencyConfigurationState.Missing)
				absentControlFiles.Add(Path.GetFullPath(path));
			if (!snapshot.CanCache)
				Interlocked.Exchange(ref transientReadFailure, 1);
			return snapshot;
		}

		Task<ConfigurationParseResult<PackageMapDescriptor>> ReadPackageAsync(string path)
		{
			if (packageProjections.TryGetValue(path, out var existing)) return existing;
			var created = ReadPackageCoreAsync(path);
			packageProjections[path] = created;
			return created;
		}

		async Task<ConfigurationParseResult<PackageMapDescriptor>> ReadPackageCoreAsync(string path)
		{
			var snapshot = await ReadSnapshotAsync(path).ConfigureAwait(false);
			AddFingerprint(path, snapshot);
			return snapshot.State == DependencyConfigurationState.Valid
				? ParsePackageMap(Path.GetDirectoryName(path)!, snapshot.Content)
				: ConfigurationParseResult<PackageMapDescriptor>.Failure(
					UnavailablePackageMap(Path.GetDirectoryName(path)!, snapshot.State, snapshot.Reason),
					snapshot.State,
					snapshot.Reason);
		}

		void AddFingerprint(string path, DependencyControlFileSnapshot snapshot)
		{
			path = Path.GetFullPath(path);
			if (fingerprintedControlFiles.Add(path))
				fingerprintParts.Add(Fingerprint(root, path, snapshot.FingerprintValue));
		}

		async Task<ConfigurationParseResult<TypeScriptConfiguration>> ReadTypeScriptConfigAsync(
			string configPath,
			string scopeDirectory)
		{
			var chain = new HashSet<string>(PathComparer);
			var parsed = await ReadTypeScriptLayerAsync(Path.GetFullPath(configPath), 0, chain).ConfigureAwait(false);
			return parsed.State == DependencyConfigurationState.Valid
				? ConfigurationParseResult<TypeScriptConfiguration>.Valid(
					MaterializeTypeScriptConfiguration(parsed.Value, scopeDirectory, root))
				: ConfigurationParseResult<TypeScriptConfiguration>.Failure(
					TypeScriptConfiguration.Default,
					parsed.State,
					parsed.Reason);
		}

		Task<ConfigurationParseResult<TypeScriptConfigurationLayer>> ReadTypeScriptLayerProjectionAsync(string path)
		{
			if (typeScriptLayerProjections.TryGetValue(path, out var existing)) return existing;
			var created = ReadTypeScriptLayerProjectionCoreAsync(path);
			typeScriptLayerProjections[path] = created;
			return created;
		}

		async Task<ConfigurationParseResult<TypeScriptConfigurationLayer>> ReadTypeScriptLayerProjectionCoreAsync(string path)
		{
			var snapshot = await ReadSnapshotAsync(path).ConfigureAwait(false);
			AddFingerprint(path, snapshot);
			return snapshot.State == DependencyConfigurationState.Valid
				? ParseTypeScriptConfigLayer(path, snapshot.Content)
				: TypeScriptLayerFailure(snapshot.State, snapshot.Reason);
		}

		async Task<ConfigurationParseResult<TypeScriptConfigurationLayer>> ReadTypeScriptLayerAsync(
			string configPath,
			int depth,
			HashSet<string> chain)
		{
			if (!chain.Add(configPath))
				return TypeScriptLayerFailure(DependencyConfigurationState.UnsupportedSemantics, TypeScriptExtendsCycleReason);

			try
			{
				var layer = await ReadTypeScriptLayerProjectionAsync(configPath).ConfigureAwait(false);
				if (layer.State != DependencyConfigurationState.Valid)
				{
					return TypeScriptLayerFailure(
						layer.State,
						layer.State == DependencyConfigurationState.Missing && depth > 0
							? TypeScriptExtendsUnavailableReason
							: layer.Reason);
				}
				if (layer.State != DependencyConfigurationState.Valid || layer.Value.Extends is null)
					return layer;

				if (depth >= MaximumTypeScriptExtendsDepth)
					return TypeScriptLayerFailure(DependencyConfigurationState.UnsupportedSemantics, TypeScriptExtendsDepthReason);

				var extendedPath = ResolveTypeScriptExtendsPath(configPath, layer.Value.Extends);
				if (extendedPath.State != DependencyConfigurationState.Valid)
					return TypeScriptLayerFailure(extendedPath.State, extendedPath.Reason);
				if (!IsWithin(root, extendedPath.Value))
					return TypeScriptLayerFailure(DependencyConfigurationState.UnsupportedSemantics, TypeScriptExtendsOutsideRootReason);
				// The engine's fast manifest snapshot can validate only files in its manifest.
				// If an extended control file is outside that set, bypass that snapshot so this
				// provider observes every later edit instead of reusing configuration by metadata
				// that never included the base file.
				if (!manifest.Contains(extendedPath.Value))
					Interlocked.Exchange(ref transientReadFailure, 1);

				var inherited = await ReadTypeScriptLayerAsync(extendedPath.Value, depth + 1, chain)
					.ConfigureAwait(false);
				return inherited.State == DependencyConfigurationState.Valid
					? ConfigurationParseResult<TypeScriptConfigurationLayer>.Valid(
						MergeTypeScriptLayers(inherited.Value, layer.Value))
					: inherited;
			}
			finally
			{
				chain.Remove(configPath);
			}
		}

		void AddDiagnostic(string path, DependencyConfigurationState state, string? reason, params string[] scopeIds)
		{
			if (state == DependencyConfigurationState.Valid ||
			    state == DependencyConfigurationState.Missing && !manifest.Contains(path))
				return;
			diagnostics.Add(new DependencyConfigurationDiagnostic(
				PortableRelative(root, path),
				state,
				reason ?? "configuration is unavailable",
				scopeIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
		}

		foreach (var project in projectFiles)
		{
			var snapshot = await ReadSnapshotAsync(project).ConfigureAwait(false);
			AddFingerprint(project, snapshot);
			var scope = "csharp:" + PortableRelative(root, project);
			var parsed = snapshot.State == DependencyConfigurationState.Valid
				? ParseProjectReferences(project, snapshot.Content)
				: ConfigurationParseResult<CSharpProjectConfiguration>.Failure(
					CSharpProjectConfiguration.Default, snapshot.State, snapshot.Reason);
			var references = parsed.Value.ProjectReferences;
			foreach (var reference in references.Where(reference => !manifest.Contains(reference)))
			{
				if (IsNetworkPath(reference) || !IsWithin(root, reference) ||
				    !_pathMetadata.TryResolveContainedPath(root, reference, out var physicalReference))
				{
					fingerprintParts.Add(Fingerprint(root, project, "project-reference:out-of-manifest"));
					continue;
				}

				var exists = _pathMetadata.FileExists(physicalReference);
				fingerprintParts.Add(Fingerprint(root, reference, exists ? "present" : "missing"));
				if (!exists)
					absentControlFiles.Add(reference);
			}
			csharpProjects[project] = (
				scope,
				references,
				parsed.Value.DisableTransitiveProjectReferences,
				parsed.State,
				parsed.Reason);
			AddDiagnostic(project, parsed.State, parsed.Reason, scope);
			if (parsed.Value.HasInvalidDisableTransitiveProjectReferences)
			{
				diagnostics.Add(new DependencyConfigurationDiagnostic(
					PortableRelative(root, project),
					DependencyConfigurationState.UnsupportedSemantics,
					DisableTransitiveProjectReferencesReason,
					[scope]));
			}
		}
		foreach (var pair in csharpProjects.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
		{
			var references = pair.Value.References
				.Where(csharpProjects.ContainsKey)
				.Select(path => csharpProjects[path].Scope)
				.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
			scopes.Add(new DependencyScopeDescriptor(
				pair.Value.Scope,
				Path.GetDirectoryName(pair.Key)!,
				LanguageId.CSharp,
				references,
				null,
				false,
				new Dictionary<string, IReadOnlyList<string>>(),
				null,
				new HashSet<string>(),
				[],
				true)
			{
				ConfigurationState = pair.Value.State,
				ConfigurationDiagnostic = pair.Value.Reason,
				DisableTransitiveProjectReferences = pair.Value.DisableTransitiveReferences
			});
		}

		foreach (var configPath in typeScriptConfigFiles)
		{
			var directory = Path.GetDirectoryName(configPath)!;
			var parsed = await ReadTypeScriptConfigAsync(configPath, directory).ConfigureAwait(false);
			var package = FindNearestManifestFile(directory, root, "package.json", manifest);
			var packageName = package is null ? null : (await ReadPackageAsync(package).ConfigureAwait(false)).Value.PackageName;
			var scopeId = "typescript:" + PortableRelative(root, configPath);
			AddDiagnostic(configPath, parsed.State, parsed.Reason, scopeId);
			scopes.Add(new DependencyScopeDescriptor(
				scopeId,
				directory,
				LanguageId.TypeScript,
				[],
				parsed.Value.ModuleResolution,
				parsed.Value.Legacy,
				parsed.Value.Paths,
				packageName,
				new HashSet<string>(),
				[],
				true,
				AllowJavaScript: parsed.Value.AllowJavaScript,
				TypeScriptModuleSuffixes: parsed.Value.ModuleSuffixes,
				TypeScriptRootDirectories: parsed.Value.RootDirectories)
			{
				ConfigurationState = parsed.State,
				ConfigurationDiagnostic = parsed.Reason,
				HasTypeScriptCustomConditions = parsed.Value.HasCustomConditions
			});
			if (parsed.Value.HasIgnoredRootDirectories)
			{
				diagnostics.Add(new DependencyConfigurationDiagnostic(
					PortableRelative(root, configPath),
					DependencyConfigurationState.UnsupportedSemantics,
					TypeScriptRootDirectoriesOutsideRootReason,
					[scopeId]));
			}
		}

		foreach (var configPath in pythonConfigFiles)
		{
			var snapshot = await ReadSnapshotAsync(configPath).ConfigureAwait(false);
			AddFingerprint(configPath, snapshot);
			var directory = Path.GetDirectoryName(configPath)!;
			var scopeId = "python:" + PortableRelative(root, configPath);
			var python = snapshot.State == DependencyConfigurationState.Valid
				? ParsePythonConfiguration(configPath, snapshot.Content)
				: ConfigurationParseResult<PythonConfiguration>.Failure(
					PythonConfiguration.Default, snapshot.State, snapshot.Reason);
			AddDiagnostic(configPath, python.State, python.Reason, scopeId);
			scopes.Add(new DependencyScopeDescriptor(
				scopeId,
				directory,
				LanguageId.Python,
				[], null, false,
				new Dictionary<string, IReadOnlyList<string>>(), null,
				python.Value.Dependencies,
				new[] { directory, Path.Combine(directory, "src") },
				true,
				python.Value.Version)
			{
				ConfigurationState = python.State,
				ConfigurationDiagnostic = python.Reason
			});
		}

		var javaProjects = new List<(string Path, string ScopeId, string ProjectKey,
			IReadOnlyList<string> ReferenceKeys, DependencyConfigurationState State, string? Reason)>();
		foreach (var configPath in javaConfigFiles)
		{
			var snapshot = await ReadSnapshotAsync(configPath).ConfigureAwait(false);
			AddFingerprint(configPath, snapshot);
			var parsed = snapshot.State == DependencyConfigurationState.Valid
				? ParseJavaProject(root, configPath, snapshot.Content)
				: ConfigurationParseResult<JavaProjectConfiguration>.Failure(
					JavaProjectConfiguration.Empty, snapshot.State, snapshot.Reason);
			var scopeId = "java:" + PortableRelative(root, configPath);
			javaProjects.Add((configPath, scopeId, parsed.Value.ProjectKey,
				parsed.Value.ProjectReferences, parsed.State, parsed.Reason));
			AddDiagnostic(configPath, parsed.State, parsed.Reason, scopeId);
		}
		var javaScopeByKey = javaProjects
			.Where(static project => project.ProjectKey.Length > 0)
			.GroupBy(static project => project.ProjectKey, StringComparer.Ordinal)
			.Where(static group => group.Count() == 1)
			.ToDictionary(static group => group.Key, static group => group.Single().ScopeId, StringComparer.Ordinal);
		foreach (var project in javaProjects.OrderBy(static project => project.Path, StringComparer.Ordinal))
		{
			var references = project.ReferenceKeys
				.Where(javaScopeByKey.ContainsKey)
				.Select(key => javaScopeByKey[key])
				.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
			scopes.Add(new DependencyScopeDescriptor(
				project.ScopeId,
				Path.GetDirectoryName(project.Path)!,
				LanguageId.Java,
				references,
				null,
				false,
				new Dictionary<string, IReadOnlyList<string>>(),
				null,
				new HashSet<string>(),
				[],
				true)
			{
				ConfigurationState = project.State,
				ConfigurationDiagnostic = project.Reason
			});
		}
		var kotlinScopeByKey = javaProjects
			.Where(static project => project.ProjectKey.Length > 0)
			.GroupBy(static project => project.ProjectKey, StringComparer.Ordinal)
			.Where(static group => group.Count() == 1)
			.ToDictionary(
				static group => group.Key,
				group => "kotlin:" + PortableRelative(root, group.Single().Path),
				StringComparer.Ordinal);
		foreach (var project in javaProjects.OrderBy(static project => project.Path, StringComparer.Ordinal))
		{
			var references = project.ReferenceKeys
				.Where(kotlinScopeByKey.ContainsKey)
				.Select(key => kotlinScopeByKey[key])
				.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
			scopes.Add(new DependencyScopeDescriptor(
				"kotlin:" + PortableRelative(root, project.Path),
				Path.GetDirectoryName(project.Path)!,
				LanguageId.Kotlin,
				references,
				null,
				false,
				new Dictionary<string, IReadOnlyList<string>>(),
				null,
				new HashSet<string>(),
				[],
				true)
			{
				ConfigurationState = project.State,
				ConfigurationDiagnostic = project.Reason
			});
		}

		var rustProjects = new List<(string Path, string ScopeId, RustProjectConfiguration Configuration,
			DependencyConfigurationState State, string? Reason)>();
		foreach (var configPath in rustConfigFiles)
		{
			var snapshot = await ReadSnapshotAsync(configPath).ConfigureAwait(false);
			AddFingerprint(configPath, snapshot);
			var parsed = snapshot.State == DependencyConfigurationState.Valid
				? ParseRustProject(configPath, snapshot.Content)
				: ConfigurationParseResult<RustProjectConfiguration>.Failure(
					RustProjectConfiguration.Empty, snapshot.State, snapshot.Reason);
			var scopeId = "rust:" + PortableRelative(root, configPath);
			rustProjects.Add((configPath, scopeId, parsed.Value, parsed.State, parsed.Reason));
			AddDiagnostic(configPath, parsed.State, parsed.Reason, scopeId);
		}
		var rustScopeByDirectory = rustProjects
			.GroupBy(static project => Path.GetDirectoryName(project.Path)!, PathComparer)
			.Where(static group => group.Count() == 1)
			.ToDictionary(static group => group.Key, static group => group.Single().ScopeId, PathComparer);
		foreach (var project in rustProjects.OrderBy(static project => project.Path, StringComparer.Ordinal))
		{
			var references = project.Configuration.ProjectDirectories
				.Where(rustScopeByDirectory.ContainsKey)
				.Select(directory => rustScopeByDirectory[directory])
				.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
			scopes.Add(new DependencyScopeDescriptor(
				project.ScopeId,
				Path.GetDirectoryName(project.Path)!,
				LanguageId.Rust,
				references,
				null,
				false,
				new Dictionary<string, IReadOnlyList<string>>(),
				project.Configuration.PackageName,
				new HashSet<string>(),
				[],
				true)
			{
				ConfigurationState = project.State,
				ConfigurationDiagnostic = project.Reason
			});
		}

		var rubyProjects = new List<(string Directory, string ScopeId, string? PackageName,
			IReadOnlyList<string> ProjectDirectories, DependencyConfigurationState State, string? Reason)>();
		foreach (var group in rubyConfigFiles.GroupBy(static path => Path.GetDirectoryName(path)!, PathComparer)
			.OrderBy(static group => group.Key, StringComparer.Ordinal))
		{
			var state = DependencyConfigurationState.Valid;
			string? reason = null;
			string? packageName = null;
			var projectDirectories = new HashSet<string>(PathComparer);
			foreach (var configPath in group.Order(StringComparer.Ordinal))
			{
				var snapshot = await ReadSnapshotAsync(configPath).ConfigureAwait(false);
				AddFingerprint(configPath, snapshot);
				if (snapshot.State != DependencyConfigurationState.Valid)
				{
					state = snapshot.State;
					reason = snapshot.Reason;
				}
				else
				{
					var parsed = ParseRubyProject(root, configPath, snapshot.Content);
					packageName ??= parsed.PackageName;
					foreach (var directory in parsed.ProjectDirectories) projectDirectories.Add(directory);
				}
				AddDiagnostic(configPath, snapshot.State, snapshot.Reason,
					"ruby:" + PortableRelative(root, group.Key));
			}
			rubyProjects.Add((group.Key, "ruby:" + PortableRelative(root, group.Key), packageName,
				projectDirectories.Order(StringComparer.Ordinal).ToArray(), state, reason));
		}
		var rubyScopeByDirectory = rubyProjects.ToDictionary(
			static project => Path.GetFullPath(project.Directory), static project => project.ScopeId, PathComparer);
		foreach (var project in rubyProjects)
		{
			var references = project.ProjectDirectories.Where(rubyScopeByDirectory.ContainsKey)
				.Select(directory => rubyScopeByDirectory[directory])
				.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
			scopes.Add(new DependencyScopeDescriptor(
				project.ScopeId, project.Directory, LanguageId.Ruby, references, null, false,
				new Dictionary<string, IReadOnlyList<string>>(), project.PackageName,
				new HashSet<string>(), [], true)
			{
				ConfigurationState = project.State,
				ConfigurationDiagnostic = project.Reason
			});
		}

		var composerProjects = new List<(string Path, string ScopeId, ComposerProjectConfiguration Configuration,
			DependencyConfigurationState State, string? Reason)>();
		foreach (var configPath in composerConfigFiles)
		{
			var snapshot = await ReadSnapshotAsync(configPath).ConfigureAwait(false);
			AddFingerprint(configPath, snapshot);
			var parsed = snapshot.State == DependencyConfigurationState.Valid
				? ParseComposerProject(snapshot.Content)
				: ConfigurationParseResult<ComposerProjectConfiguration>.Failure(
					ComposerProjectConfiguration.Empty, snapshot.State, snapshot.Reason);
			var scopeId = "php:" + PortableRelative(root, configPath);
			composerProjects.Add((configPath, scopeId, parsed.Value, parsed.State, parsed.Reason));
			AddDiagnostic(configPath, parsed.State, parsed.Reason, scopeId);
		}
		var composerScopeByName = composerProjects.Where(static project => project.Configuration.PackageName is not null)
			.GroupBy(static project => project.Configuration.PackageName!, StringComparer.Ordinal)
			.Where(static group => group.Count() == 1)
			.ToDictionary(static group => group.Key, static group => group.Single().ScopeId, StringComparer.Ordinal);
		foreach (var project in composerProjects)
		{
			var references = project.Configuration.Dependencies.Where(composerScopeByName.ContainsKey)
				.Select(name => composerScopeByName[name]).Order(StringComparer.Ordinal).ToArray();
			scopes.Add(new DependencyScopeDescriptor(
				project.ScopeId, Path.GetDirectoryName(project.Path)!, LanguageId.Php, references, null, false,
				project.Configuration.AutoloadPaths, project.Configuration.PackageName,
				new HashSet<string>(), [], true)
			{
				ConfigurationState = project.State,
				ConfigurationDiagnostic = project.Reason
			});
		}

		foreach (var configPath in cConfigFiles)
		{
			var snapshot = await ReadSnapshotAsync(configPath).ConfigureAwait(false);
			AddFingerprint(configPath, snapshot);
			var directory = Path.GetDirectoryName(configPath)!;
			var scopeId = "c:" + PortableRelative(root, configPath);
			var includeDirectories = snapshot.State == DependencyConfigurationState.Valid
				? ParseCIncludeDirectories(root, directory, snapshot.Content)
				: [];
			AddDiagnostic(configPath, snapshot.State, snapshot.Reason, scopeId);
			scopes.Add(new DependencyScopeDescriptor(
				scopeId, directory, LanguageId.C, [], null, false,
				new Dictionary<string, IReadOnlyList<string>>(), null,
				new HashSet<string>(), [], true)
			{
				ConfigurationState = snapshot.State,
				ConfigurationDiagnostic = snapshot.Reason,
				CIncludeDirectories = includeDirectories
			});
			scopes.Add(new DependencyScopeDescriptor(
				"cpp:" + PortableRelative(root, configPath), directory, LanguageId.Cpp, [], null, false,
				new Dictionary<string, IReadOnlyList<string>>(), null,
				new HashSet<string>(), [], true)
			{
				ConfigurationState = snapshot.State,
				ConfigurationDiagnostic = snapshot.Reason,
				CIncludeDirectories = includeDirectories
			});
		}

		MarkAmbiguousScopeOwnership(scopes, diagnostics, root);

		AddFallbackScope(scopes, root, LanguageId.CSharp);
		AddFallbackScope(scopes, root, LanguageId.TypeScript);
		AddFallbackScope(scopes, root, LanguageId.Python);
		AddFallbackScope(scopes, root, LanguageId.C);
		AddFallbackScope(scopes, root, LanguageId.Cpp);
		var packageMaps = new Dictionary<string, PackageMapDescriptor>(StringComparer.Ordinal);
		foreach (var packagePath in packageFiles)
		{
			var parsed = await ReadPackageAsync(packagePath).ConfigureAwait(false);
			packageMaps[PortableRelative(root, parsed.Value.Directory)] = parsed.Value;
			AddDiagnostic(packagePath, parsed.State, parsed.Reason,
				scopes.Where(scope => IsWithin(parsed.Value.Directory, scope.Root)).Select(static scope => scope.ScopeId).ToArray());
		}

		return new DependencyResolverConfiguration(
			Hash(fingerprintParts.Order(StringComparer.Ordinal)),
			scopes.OrderBy(static scope => scope.ScopeId, StringComparer.Ordinal).ToArray(),
			packageMaps,
			DotNetCatalog.Value,
			PythonCatalogs.Value,
			NodeCatalog.Value)
		{
			ConfigurationDiagnostics = diagnostics
				.GroupBy(static item => (item.Path, item.State, item.Reason))
				.Select(static group => group.First() with
				{
					ScopeIds = group.SelectMany(static item => item.ScopeIds)
						.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
				})
				.OrderBy(static item => item.Path, StringComparer.Ordinal)
				.ToArray(),
			CanCache = Volatile.Read(ref transientReadFailure) == 0,
			AbsentControlFiles = absentControlFiles.Order(StringComparer.Ordinal).ToArray()
		};
	}

	private static IReadOnlySet<string> ParseNodeDependencies(JsonElement root)
	{
		var result = new HashSet<string>(StringComparer.Ordinal);
		foreach (var property in new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" })
		{
			if (!root.TryGetProperty(property, out var dependencies) ||
			    dependencies.ValueKind != JsonValueKind.Object)
				continue;
			foreach (var dependency in dependencies.EnumerateObject())
				result.Add(dependency.Name);
		}
		return result;
	}

	private static ConfigurationParseResult<CSharpProjectConfiguration> ParseProjectReferences(string projectPath, string content)
	{
		try
		{
			var directory = Path.GetDirectoryName(projectPath)!;
			var document = XDocument.Parse(content);
			var compileItems = document.Descendants()
				.Where(static element => element.Name.LocalName == "Compile")
				.Where(static element => element.Attributes().Any(attribute =>
					attribute.Name.LocalName is "Include" or "Remove" or "Link"))
				.ToArray();
			var disableValues = document.Descendants()
				.Where(static element => element.Name.LocalName == "DisableTransitiveProjectReferences")
				.Select(static element => element.Value.Trim())
				.ToArray();
			var disableTransitive = disableValues.LastOrDefault() switch
			{
				var candidate when IsLiteralTrue(candidate) => true,
				_ => false
			};
			var invalidDisableTransitive = disableValues.Any(static value =>
				!IsLiteralTrue(value) && !IsLiteralFalse(value));
			var elements = document.Descendants()
				.Where(static element => element.Name.LocalName == "ProjectReference")
				.Where(static element => !IsLiteralFalse(element.Attribute("ReferenceOutputAssembly")?.Value))
				.ToArray();
			var hasUnknownCondition = elements.Any(static element =>
			{
				var condition = element.Attribute("Condition")?.Value;
				return !string.IsNullOrWhiteSpace(condition) &&
				       !IsLiteralTrue(condition) &&
				       !IsLiteralFalse(condition);
			});
			var references = elements
				.Where(static element => IsEnabledProjectReference(element))
				.Select(element => element.Attribute("Include")?.Value)
				.Where(static value => !string.IsNullOrWhiteSpace(value))
				.Select(value => Path.GetFullPath(Path.Combine(directory, NormalizeMsBuildInclude(value!))))
				.Distinct(PathComparer).Order(StringComparer.Ordinal).ToArray();
			var projectConfiguration = new CSharpProjectConfiguration(
				references,
				disableTransitive,
				invalidDisableTransitive);
			if (compileItems.Length > 0)
				return ConfigurationParseResult<CSharpProjectConfiguration>.Failure(
					projectConfiguration,
					DependencyConfigurationState.UnsupportedSemantics,
					CompileItemMembershipReason);
			return hasUnknownCondition
				? ConfigurationParseResult<CSharpProjectConfiguration>.Failure(
					projectConfiguration,
					DependencyConfigurationState.UnsupportedSemantics,
					ProjectReferenceConditionReason)
				: ConfigurationParseResult<CSharpProjectConfiguration>.Valid(projectConfiguration);
		}
		catch (System.Xml.XmlException)
		{
			return ConfigurationParseResult<CSharpProjectConfiguration>.Failure(
				CSharpProjectConfiguration.Default,
				DependencyConfigurationState.Corrupt,
				"invalid project XML");
		}
	}

	private static string NormalizeMsBuildInclude(string value) =>
		value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

	private static ConfigurationParseResult<TypeScriptConfigurationLayer> ParseTypeScriptConfigLayer(
		string configPath,
		string content)
	{
		try
		{
			using var document = JsonDocument.Parse(content, new JsonDocumentOptions
			{
				AllowTrailingCommas = true,
				CommentHandling = JsonCommentHandling.Skip
			});
			if (document.RootElement.ValueKind != JsonValueKind.Object)
				return TypeScriptLayerFailure(
					DependencyConfigurationState.UnsupportedSemantics,
					"tsconfig root must be an object");

			var extends = ParseTypeScriptExtends(document.RootElement);
			if (extends.State != DependencyConfigurationState.Valid)
				return TypeScriptLayerFailure(extends.State, extends.Reason);

			if (!document.RootElement.TryGetProperty("compilerOptions", out var options))
			{
				return ConfigurationParseResult<TypeScriptConfigurationLayer>.Valid(
					TypeScriptConfigurationLayer.Empty with { Extends = extends.Value });
			}
			if (options.ValueKind != JsonValueKind.Object)
				return TypeScriptLayerFailure(
					DependencyConfigurationState.Corrupt,
					"tsconfig compilerOptions must be an object");
			if (options.TryGetProperty("moduleResolution", out var mode) && mode.ValueKind != JsonValueKind.String)
				return TypeScriptLayerFailure(
					DependencyConfigurationState.UnsupportedSemantics,
					"tsconfig compilerOptions.moduleResolution must be a string");
			if (options.TryGetProperty("module", out var moduleElement) &&
			    moduleElement.ValueKind != JsonValueKind.String)
				return TypeScriptLayerFailure(
					DependencyConfigurationState.UnsupportedSemantics,
					"tsconfig compilerOptions.module must be a string");
			var hasCustomConditions = options.TryGetProperty("customConditions", out var customConditions);
			if (hasCustomConditions &&
			    (customConditions.ValueKind != JsonValueKind.Array ||
			     customConditions.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String)))
				return TypeScriptLayerFailure(
					DependencyConfigurationState.UnsupportedSemantics,
					TypeScriptCustomConditionsReason);
			var usesCustomConditions = hasCustomConditions && customConditions.GetArrayLength() > 0;

			var hasModuleResolution = options.TryGetProperty("moduleResolution", out mode);
			var moduleResolution = hasModuleResolution ? mode.GetString()?.ToLowerInvariant() : null;
			if (hasModuleResolution && !IsSupportedTypeScriptModuleResolution(moduleResolution))
				return TypeScriptLayerFailure(
					DependencyConfigurationState.UnsupportedSemantics,
					TypeScriptModuleResolutionReason);
			var hasModule = options.TryGetProperty("module", out moduleElement);
			var module = hasModule ? moduleElement.GetString()?.ToLowerInvariant() : null;
			var hasBaseUrl = options.TryGetProperty("baseUrl", out var baseUrlElement);
			var baseUrl = hasBaseUrl && baseUrlElement.ValueKind == JsonValueKind.String
				? baseUrlElement.GetString()
				: null;
			var hasAllowJavaScript = options.TryGetProperty("allowJs", out var allowJs);
			var allowJavaScript = hasAllowJavaScript && allowJs.ValueKind is JsonValueKind.True;
			var hasModuleSuffixes = options.TryGetProperty("moduleSuffixes", out var moduleSuffixesElement);
			if (hasModuleSuffixes &&
			    (moduleSuffixesElement.ValueKind != JsonValueKind.Array ||
			     moduleSuffixesElement.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String)))
			{
				return TypeScriptLayerFailure(
					DependencyConfigurationState.UnsupportedSemantics,
					"tsconfig compilerOptions.moduleSuffixes must be an array of strings");
			}
			var moduleSuffixes = hasModuleSuffixes
				? moduleSuffixesElement.EnumerateArray().Select(static item => item.GetString()!).ToArray()
				: null;
			var hasRootDirectories = options.TryGetProperty("rootDirs", out var rootDirectoriesElement);
			if (hasRootDirectories &&
			    (rootDirectoriesElement.ValueKind != JsonValueKind.Array ||
			     rootDirectoriesElement.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String)))
			{
				return TypeScriptLayerFailure(
					DependencyConfigurationState.UnsupportedSemantics,
					"tsconfig compilerOptions.rootDirs must be an array of strings");
			}
			var rootDirectories = hasRootDirectories
				? rootDirectoriesElement.EnumerateArray().Select(static item => item.GetString()!).ToArray()
				: null;
			var paths = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
			var hasPaths = options.TryGetProperty("paths", out var mappings);
			if (hasPaths && mappings.ValueKind != JsonValueKind.Object)
				return TypeScriptLayerFailure(
					DependencyConfigurationState.UnsupportedSemantics,
					"tsconfig compilerOptions.paths must be an object");
			if (mappings.ValueKind == JsonValueKind.Object)
			{
				foreach (var mapping in mappings.EnumerateObject())
				{
					if (mapping.Value.ValueKind != JsonValueKind.Array ||
					    mapping.Value.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String))
						return TypeScriptLayerFailure(
							DependencyConfigurationState.UnsupportedSemantics,
							"tsconfig path mapping must be an array of strings");
					paths[mapping.Name] = mapping.Value.EnumerateArray()
						.Select(static item => item.GetString()!).ToArray();
				}
			}
			return ConfigurationParseResult<TypeScriptConfigurationLayer>.Valid(
				new TypeScriptConfigurationLayer(
					extends.Value,
					new OptionalConfigurationValue<string>(hasModuleResolution, moduleResolution),
					new OptionalConfigurationValue<string>(hasModule, module),
					new OptionalConfigurationValue<TypeScriptBaseUrl>(
						hasBaseUrl,
						hasBaseUrl ? new TypeScriptBaseUrl(Path.GetDirectoryName(configPath)!, baseUrl) : null),
					new OptionalConfigurationValue<TypeScriptPathMappings>(
						hasPaths,
						hasPaths ? new TypeScriptPathMappings(Path.GetDirectoryName(configPath)!, paths) : null),
					new OptionalConfigurationValue<bool>(hasAllowJavaScript, allowJavaScript),
					new OptionalConfigurationValue<IReadOnlyList<string>>(hasModuleSuffixes, moduleSuffixes),
					new OptionalConfigurationValue<TypeScriptRootDirectories>(
						hasRootDirectories,
						hasRootDirectories
							? new TypeScriptRootDirectories(Path.GetDirectoryName(configPath)!, rootDirectories!)
							: null),
					new OptionalConfigurationValue<bool>(hasCustomConditions, usesCustomConditions)));
		}
		catch (JsonException)
		{
			return TypeScriptLayerFailure(
				DependencyConfigurationState.Corrupt,
				"invalid tsconfig JSON");
		}
	}

	private static bool IsSupportedTypeScriptModuleResolution(string? value) =>
		value is "node10" or "node" or "classic" or "node16" or "nodenext" or "bundler";

	private static bool IsEnabledProjectReference(XElement element)
	{
		if (IsLiteralFalse(element.Attribute("ReferenceOutputAssembly")?.Value))
			return false;
		var condition = element.Attribute("Condition")?.Value;
		return string.IsNullOrWhiteSpace(condition) || IsLiteralTrue(condition);
	}

	private static bool IsLiteralFalse(string? value) =>
		NormalizeMsBuildBoolean(value).Equals("false", StringComparison.OrdinalIgnoreCase);

	private static bool IsLiteralTrue(string? value) =>
		NormalizeMsBuildBoolean(value).Equals("true", StringComparison.OrdinalIgnoreCase);

	private static string NormalizeMsBuildBoolean(string? value)
	{
		var normalized = value?.Trim() ?? string.Empty;
		return normalized.Length >= 2 &&
		       (normalized[0] == '\'' && normalized[^1] == '\'' ||
		        normalized[0] == '"' && normalized[^1] == '"')
			? normalized[1..^1].Trim()
			: normalized;
	}

	private static ConfigurationParseResult<string?> ParseTypeScriptExtends(JsonElement root)
	{
		if (!root.TryGetProperty("extends", out var extends))
			return ConfigurationParseResult<string?>.Valid(null);
		if (extends.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(extends.GetString()))
			return ConfigurationParseResult<string?>.Failure(
				null,
				DependencyConfigurationState.UnsupportedSemantics,
				TypeScriptExtendsShapeReason);

		var value = extends.GetString()!;
		if (!IsExplicitRelativeTypeScriptExtends(value))
			return ConfigurationParseResult<string?>.Failure(
				null,
				DependencyConfigurationState.UnsupportedSemantics,
				TypeScriptExtendsPackageReason);
		return ConfigurationParseResult<string?>.Valid(value);
	}

	private static bool IsExplicitRelativeTypeScriptExtends(string value) =>
		value.StartsWith("./", StringComparison.Ordinal) ||
		value.StartsWith("../", StringComparison.Ordinal) ||
		value.StartsWith(".\\", StringComparison.Ordinal) ||
		value.StartsWith("..\\", StringComparison.Ordinal);

	private static ConfigurationParseResult<string> ResolveTypeScriptExtendsPath(
		string configPath,
		string relativePath)
	{
		try
		{
			var normalized = relativePath
				.Replace('/', Path.DirectorySeparatorChar)
				.Replace('\\', Path.DirectorySeparatorChar);
			return ConfigurationParseResult<string>.Valid(
				Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configPath)!, normalized)));
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return ConfigurationParseResult<string>.Failure(
				string.Empty,
				DependencyConfigurationState.UnsupportedSemantics,
				TypeScriptExtendsShapeReason);
		}
	}

	private static TypeScriptConfigurationLayer MergeTypeScriptLayers(
		TypeScriptConfigurationLayer inherited,
		TypeScriptConfigurationLayer child) =>
		new(
			Extends: null,
			child.ModuleResolution.IsSpecified ? child.ModuleResolution : inherited.ModuleResolution,
			child.Module.IsSpecified ? child.Module : inherited.Module,
			child.BaseUrl.IsSpecified ? child.BaseUrl : inherited.BaseUrl,
			child.Paths.IsSpecified ? child.Paths : inherited.Paths,
			child.AllowJavaScript.IsSpecified ? child.AllowJavaScript : inherited.AllowJavaScript,
			child.ModuleSuffixes.IsSpecified ? child.ModuleSuffixes : inherited.ModuleSuffixes,
			child.RootDirectories.IsSpecified ? child.RootDirectories : inherited.RootDirectories,
			child.CustomConditions.IsSpecified ? child.CustomConditions : inherited.CustomConditions);

	private TypeScriptConfiguration MaterializeTypeScriptConfiguration(
		TypeScriptConfigurationLayer layer,
		string scopeDirectory,
		string projectRoot)
	{
		var moduleResolution = layer.ModuleResolution.IsSpecified
			? layer.ModuleResolution.Value ?? "bundler"
			: layer.Module.Value is "node16" or "nodenext"
				? layer.Module.Value
				: "bundler";
		var mappings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
		if (layer.Paths.Value is { } paths)
		{
			var mappingRoot = layer.BaseUrl.Value is { } baseUrl
				? ResolveTypeScriptOptionDirectory(baseUrl.DeclaringDirectory, baseUrl.Value)
				: paths.DeclaringDirectory;
			foreach (var mapping in paths.Values)
			{
				mappings[mapping.Key] = mapping.Value
					.Select(target => RebaseTypeScriptPath(scopeDirectory, mappingRoot, target))
					.ToArray();
			}
		}
		var legacy = moduleResolution.Equals("node10", StringComparison.OrdinalIgnoreCase) ||
		             moduleResolution.Equals("node", StringComparison.OrdinalIgnoreCase) ||
		             layer.BaseUrl.IsSpecified;
		var rootDirectories = new List<string>();
		var hasIgnoredRootDirectories = false;
		if (layer.RootDirectories.Value is { } configuredRoots)
		{
			foreach (var value in configuredRoots.Values)
			{
				string candidate;
				try
				{
					candidate = ResolveTypeScriptOptionDirectory(
						configuredRoots.DeclaringDirectory,
						NormalizeTypeScriptOptionPath(value));
				}
				catch (Exception exception) when (
					exception is ArgumentException or NotSupportedException or PathTooLongException)
				{
					hasIgnoredRootDirectories = true;
					continue;
				}
				if (IsNetworkPath(candidate) || !IsWithin(projectRoot, candidate) ||
				    !_pathMetadata.TryResolveContainedPath(projectRoot, candidate, out _))
				{
					hasIgnoredRootDirectories = true;
					continue;
				}
				if (!rootDirectories.Contains(candidate, PathComparer))
					rootDirectories.Add(candidate);
			}
		}
		return new TypeScriptConfiguration(
			moduleResolution,
			legacy,
			mappings,
			layer.AllowJavaScript.IsSpecified && layer.AllowJavaScript.Value,
			layer.ModuleSuffixes.IsSpecified
				? layer.ModuleSuffixes.Value ?? []
				: [""],
			layer.CustomConditions.IsSpecified && layer.CustomConditions.Value,
			rootDirectories,
			hasIgnoredRootDirectories);
	}

	private static string NormalizeTypeScriptOptionPath(string value) => value
		.Replace('/', Path.DirectorySeparatorChar)
		.Replace('\\', Path.DirectorySeparatorChar);

	private static string ResolveTypeScriptOptionDirectory(string declaringDirectory, string? relative) =>
		string.IsNullOrEmpty(relative)
			? declaringDirectory
			: Path.GetFullPath(Path.Combine(declaringDirectory, relative));

	private static string RebaseTypeScriptPath(string scopeDirectory, string mappingRoot, string target) =>
		Path.GetRelativePath(scopeDirectory, Path.GetFullPath(Path.Combine(mappingRoot, target)));

	private static ConfigurationParseResult<TypeScriptConfigurationLayer> TypeScriptLayerFailure(
		DependencyConfigurationState state,
		string? reason) =>
		ConfigurationParseResult<TypeScriptConfigurationLayer>.Failure(
			TypeScriptConfigurationLayer.Empty,
			state,
			reason);

	private static ConfigurationParseResult<PackageMapDescriptor> ParsePackageMap(string directory, string content)
	{
		try
		{
			using var document = JsonDocument.Parse(content);
			if (document.RootElement.ValueKind != JsonValueKind.Object)
				return ConfigurationParseResult<PackageMapDescriptor>.Failure(
					UnavailablePackageMap(
						directory,
						DependencyConfigurationState.UnsupportedSemantics,
						"package.json root must be an object"),
					DependencyConfigurationState.UnsupportedSemantics,
					"package.json root must be an object");
			return ConfigurationParseResult<PackageMapDescriptor>.Valid(new PackageMapDescriptor(
				directory,
				document.RootElement.TryGetProperty("name", out var name) ? name.GetString() : null,
				FlattenMap(document.RootElement, "imports"),
				FlattenMap(document.RootElement, "exports"),
				document.RootElement.TryGetProperty("type", out var type) ? type.GetString() : null,
				ParseNodeDependencies(document.RootElement)));
		}
		catch (Exception exception) when (exception is JsonException or InvalidOperationException)
		{
			return ConfigurationParseResult<PackageMapDescriptor>.Failure(
				UnavailablePackageMap(directory, DependencyConfigurationState.Corrupt, "invalid package.json JSON"),
				DependencyConfigurationState.Corrupt,
				"invalid package.json JSON");
		}
	}

	private static PackageMapDescriptor EmptyPackageMap(string directory) => new(
		directory,
		null,
		new Dictionary<string, PackageTargetDescriptor>(),
		new Dictionary<string, PackageTargetDescriptor>(),
		null,
		new HashSet<string>());

	private static PackageMapDescriptor UnavailablePackageMap(
		string directory,
		DependencyConfigurationState state,
		string? reason) => EmptyPackageMap(directory) with
		{
			ConfigurationState = state,
			ConfigurationDiagnostic = reason ?? "package.json configuration is unavailable"
		};

	private static IReadOnlyDictionary<string, PackageTargetDescriptor> FlattenMap(JsonElement root, string property)
	{
		var result = new Dictionary<string, PackageTargetDescriptor>(StringComparer.Ordinal);
		if (!root.TryGetProperty(property, out var map)) return result;
		if (map.ValueKind is JsonValueKind.String or JsonValueKind.Null)
		{
			result["."] = ParsePackageTarget(map);
			return result;
		}
		if (map.ValueKind != JsonValueKind.Object)
		{
			result["."] = UnsupportedPackageTarget(property == "exports"
				? "package exports must be a string, null, or object"
				: "package imports must be a string, null, or object");
			return result;
		}
		if (property == "exports" && !map.EnumerateObject().Any(static item => item.Name.StartsWith(".", StringComparison.Ordinal)))
		{
			result["."] = ParsePackageTarget(map);
			return result;
		}
		foreach (var item in map.EnumerateObject()) result[item.Name] = ParsePackageTarget(item.Value);
		return result;
	}

	private static PackageTargetDescriptor ParsePackageTarget(JsonElement value)
	{
		if (value.ValueKind == JsonValueKind.String)
			return new PackageTargetDescriptor(PackageTargetKind.Path, value.GetString(), [], null);
		if (value.ValueKind == JsonValueKind.Null)
			return new PackageTargetDescriptor(PackageTargetKind.Blocked, null, [], null);
		if (value.ValueKind != JsonValueKind.Object)
			return UnsupportedPackageTarget("package target kind is not supported");
		return new PackageTargetDescriptor(
			PackageTargetKind.Conditions,
			null,
			value.EnumerateObject()
				.Select(static condition => new PackageConditionDescriptor(
					condition.Name,
					ParsePackageTarget(condition.Value)))
				.ToArray(),
			null);
	}

	private static PackageTargetDescriptor UnsupportedPackageTarget(string reason) =>
		new(PackageTargetKind.Unsupported, null, [], reason);

	private static ConfigurationParseResult<PythonConfiguration> ParsePythonConfiguration(
		string path,
		string content)
	{
		if (!Path.GetFileName(path).Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase))
		{
			return ConfigurationParseResult<PythonConfiguration>.Valid(new PythonConfiguration(
				ParseSetupDependencies(content),
				ParsePythonVersion(content)));
		}

		try
		{
			var root = TomlSerializer.Deserialize<TomlTable>(content) ??
				throw new InvalidOperationException("TOML root is unavailable.");
			var dependencies = new HashSet<string>(StringComparer.Ordinal);
			string? version = null;
			if (TryGetTable(root, "project", out var project))
			{
				AddTomlRequirements(project, "dependencies", dependencies);
				if (project.TryGetValue("requires-python", out var requiresPython) && requiresPython is string constraint)
					version = ParsePythonVersionConstraint(constraint);
				if (TryGetTable(project, "optional-dependencies", out var optional))
					foreach (var value in optional.Values.OfType<TomlArray>())
						AddTomlRequirements(value, dependencies);
			}
			if (TryGetTable(root, "tool", out var tool) &&
			    TryGetTable(tool, "poetry", out var poetry))
			{
				if (TryGetTable(poetry, "dependencies", out var poetryDependencies))
				{
					foreach (var key in poetryDependencies.Keys)
						AddRequirement(key, dependencies);
					if (poetryDependencies.TryGetValue("python", out var pythonConstraint) && pythonConstraint is string constraint)
						version ??= ParsePythonVersionConstraint(constraint);
				}
				if (TryGetTable(poetry, "group", out var groups))
				{
					foreach (var group in groups.Values.OfType<TomlTable>())
						if (TryGetTable(group, "dependencies", out var groupDependencies))
							foreach (var key in groupDependencies.Keys)
								AddRequirement(key, dependencies);
				}
			}
			return ConfigurationParseResult<PythonConfiguration>.Valid(
				new PythonConfiguration(dependencies, version));
		}
		catch (Exception exception) when (exception is TomlException or InvalidOperationException)
		{
			return ConfigurationParseResult<PythonConfiguration>.Failure(
				PythonConfiguration.Default,
				DependencyConfigurationState.Corrupt,
				InvalidPyProjectReason);
		}
	}

	private static bool TryGetTable(TomlTable table, string key, out TomlTable value)
	{
		if (table.TryGetValue(key, out var candidate) && candidate is TomlTable nested)
		{
			value = nested;
			return true;
		}
		value = null!;
		return false;
	}

	private static void AddTomlRequirements(TomlTable table, string key, ISet<string> result)
	{
		if (table.TryGetValue(key, out var value) && value is TomlArray requirements)
			AddTomlRequirements(requirements, result);
	}

	private static void AddTomlRequirements(TomlArray requirements, ISet<string> result)
	{
		foreach (var requirement in requirements.OfType<string>())
			AddRequirement(requirement, result);
	}

	private static string? ParsePythonVersion(string content)
	{
		var match = PythonVersionRegex.Match(content);
		return match.Success ? ParsePythonVersionConstraint(match.Groups["constraint"].Value) : null;
	}

	private static string? ParsePythonVersionConstraint(string value)
	{
		var constraint = value.Replace(" ", string.Empty, StringComparison.Ordinal);
		if (constraint.Contains(">=3.13", StringComparison.Ordinal) ||
		    constraint.Contains("==3.13", StringComparison.Ordinal) ||
		    constraint.Contains("~=3.13", StringComparison.Ordinal))
			return "3.13";
		if (constraint.Contains("<3.13", StringComparison.Ordinal) ||
		    constraint.Contains("==3.12", StringComparison.Ordinal) ||
		    constraint.Contains("~=3.12", StringComparison.Ordinal))
			return "3.12";
		return null;
	}

	private static IReadOnlySet<string> ParseSetupDependencies(string content)
	{
		var result = new HashSet<string>(StringComparer.Ordinal);
		var section = string.Empty;
		var setupRequirementList = false;
		foreach (var line in content.Split('\n'))
		{
			var value = line.Trim();
			if (value.StartsWith('[') && value.EndsWith(']'))
			{
				section = value.Trim('[', ']').Trim();
				setupRequirementList = false;
				continue;
			}

			var inOptions = section.Equals("options", StringComparison.OrdinalIgnoreCase);
			var inExtras = section.Equals("options.extras_require", StringComparison.OrdinalIgnoreCase);
			if (inOptions && value.StartsWith("install_requires", StringComparison.OrdinalIgnoreCase) && value.Contains('='))
			{
				setupRequirementList = true;
				AddRequirement(value[(value.IndexOf('=') + 1)..], result);
				continue;
			}
			if (inExtras && value.Contains('='))
			{
				setupRequirementList = true;
				AddRequirement(value[(value.IndexOf('=') + 1)..], result);
				continue;
			}
			if (setupRequirementList && (line.Length == 0 || char.IsWhiteSpace(line[0])))
				AddRequirement(value, result);
			else if (value.Length > 0)
				setupRequirementList = false;
		}
		return result;
	}

	private static void AddRequirement(string value, ISet<string> result)
	{
		var candidate = value.Trim().Trim('"', '\'', ',', '[', ']');
		if (candidate.Length == 0 || candidate.Contains("://", StringComparison.Ordinal) && !candidate.Contains('@') ||
		    candidate.Contains("::", StringComparison.Ordinal)) return;
		var match = RequirementNameRegex.Match(candidate);
		if (match.Success && !match.Groups["name"].Value.Equals("python", StringComparison.OrdinalIgnoreCase))
			result.Add(match.Groups["name"].Value.Replace('-', '_'));
	}

	private static readonly Regex RequirementNameRegex = new(
		"^(?<name>[A-Za-z0-9][A-Za-z0-9_.-]*)",
		RegexOptions.CultureInvariant);
	private static readonly Regex PythonVersionRegex = new(
		"^(?:requires-python|requires_python|python_requires)\\s*=\\s*[\\\"']?(?<constraint>[^\\\"'\\r\\n]+)",
		RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline);

	private static IReadOnlySet<string> LoadCatalog(string fileName)
	{
		var name = $"DevProjex.Infrastructure.Dependencies.PlatformSymbols.{fileName}";
		using var stream = typeof(FileDependencyConfigurationProvider).Assembly.GetManifestResourceStream(name) ??
			throw new InvalidOperationException($"Dependency platform catalog '{name}' is missing.");
		return JsonSerializer.Deserialize<string[]>(stream)?.ToFrozenSet(StringComparer.Ordinal) ??
			throw new InvalidOperationException($"Dependency platform catalog '{name}' is empty.");
	}
	private static IReadOnlyDictionary<string, IReadOnlySet<string>> LoadCatalogMap(string fileName)
	{
		var name = $"DevProjex.Infrastructure.Dependencies.PlatformSymbols.{fileName}";
		using var stream = typeof(FileDependencyConfigurationProvider).Assembly.GetManifestResourceStream(name) ??
			throw new InvalidOperationException($"Dependency platform catalog '{name}' is missing.");
		var catalog = JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream) ??
			throw new InvalidOperationException($"Dependency platform catalog '{name}' is empty.");
		return catalog.ToFrozenDictionary(
			static pair => pair.Key,
			static pair => (IReadOnlySet<string>)pair.Value.ToFrozenSet(StringComparer.Ordinal),
			StringComparer.Ordinal);
	}

	private static ConfigurationParseResult<JavaProjectConfiguration> ParseJavaProject(
		string root,
		string path,
		string content)
	{
		if (Path.GetFileName(path).Equals("pom.xml", StringComparison.OrdinalIgnoreCase))
		{
			try
			{
				var document = XDocument.Parse(content, LoadOptions.None);
				var project = document.Root;
				if (project is null || project.Name.LocalName != "project")
					return ConfigurationParseResult<JavaProjectConfiguration>.Failure(
						JavaProjectConfiguration.Empty,
						DependencyConfigurationState.Corrupt,
						"Maven project configuration is invalid");
				string? DirectValue(XElement element, string name) => element.Elements()
					.FirstOrDefault(candidate => candidate.Name.LocalName == name)?.Value.Trim();
				var artifact = DirectValue(project, "artifactId");
				var group = DirectValue(project, "groupId");
				if (group is null && project.Elements()
				    .FirstOrDefault(static element => element.Name.LocalName == "parent") is { } parent)
					group = DirectValue(parent, "groupId");
				if (string.IsNullOrWhiteSpace(artifact) || string.IsNullOrWhiteSpace(group))
					return ConfigurationParseResult<JavaProjectConfiguration>.Failure(
						JavaProjectConfiguration.Empty,
						DependencyConfigurationState.UnsupportedSemantics,
						"Maven project coordinates are unavailable");
				var mavenReferences = project.Elements()
					.Where(static element => element.Name.LocalName == "dependencies")
					.SelectMany(static element => element.Elements()
						.Where(static candidate => candidate.Name.LocalName == "dependency"))
					.Select(element =>
					{
						var dependencyGroup = DirectValue(element, "groupId");
						var dependencyArtifact = DirectValue(element, "artifactId");
						return string.IsNullOrWhiteSpace(dependencyGroup) || string.IsNullOrWhiteSpace(dependencyArtifact)
							? null
							: $"{dependencyGroup}:{dependencyArtifact}";
					})
					.Where(static value => value is not null).Cast<string>()
					.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
				return ConfigurationParseResult<JavaProjectConfiguration>.Valid(
					new JavaProjectConfiguration($"{group}:{artifact}", mavenReferences));
			}
			catch (Exception exception) when (exception is System.Xml.XmlException or InvalidOperationException)
			{
				return ConfigurationParseResult<JavaProjectConfiguration>.Failure(
					JavaProjectConfiguration.Empty,
					DependencyConfigurationState.Corrupt,
					"Maven project configuration is invalid");
			}
		}

		var directory = Path.GetDirectoryName(path)!;
		var relative = PortableRelative(root, directory);
		var projectKey = relative == "." ? ":" : ":" + relative.Replace('/', ':');
		var gradleReferences = Regex.Matches(
			content,
			"""\bproject\s*\(\s*(?:path\s*=\s*)?['"](?<path>:[^'"]+)['"]\s*\)""",
			RegexOptions.CultureInvariant)
			.Select(static match => match.Groups["path"].Value)
			.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
		return ConfigurationParseResult<JavaProjectConfiguration>.Valid(
			new JavaProjectConfiguration(projectKey, gradleReferences));
	}

	private static ConfigurationParseResult<RustProjectConfiguration> ParseRustProject(
		string path,
		string content)
	{
		try
		{
			var model = TomlSerializer.Deserialize<TomlTable>(content) ??
				throw new InvalidDataException("Cargo configuration is empty.");
			string? packageName = null;
			if (TryGetTable(model, "package", out var package) &&
			    package.TryGetValue("name", out var nameValue) && nameValue is string name)
				packageName = name.Replace('-', '_');
			var directories = new HashSet<string>(PathComparer);
			foreach (var section in new[] { "dependencies", "dev-dependencies", "build-dependencies" })
			{
				if (!TryGetTable(model, section, out var dependencies)) continue;
				foreach (var dependency in dependencies.Values.OfType<TomlTable>())
				{
					if (!dependency.TryGetValue("path", out var pathValue) || pathValue is not string relativePath)
						continue;
					if (Path.IsPathFullyQualified(relativePath))
						return ConfigurationParseResult<RustProjectConfiguration>.Failure(
							RustProjectConfiguration.Empty,
							DependencyConfigurationState.UnsupportedSemantics,
							"Cargo dependency path must be relative");
					directories.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, relativePath)));
				}
			}
			return ConfigurationParseResult<RustProjectConfiguration>.Valid(
				new RustProjectConfiguration(packageName, directories.Order(StringComparer.Ordinal).ToArray()));
		}
		catch (Exception exception) when (exception is InvalidDataException or TomlException or InvalidOperationException)
		{
			return ConfigurationParseResult<RustProjectConfiguration>.Failure(
				RustProjectConfiguration.Empty,
				DependencyConfigurationState.Corrupt,
				"Cargo project configuration is invalid");
		}
	}

	private static RubyProjectConfiguration ParseRubyProject(string root, string path, string content)
	{
		var directory = Path.GetDirectoryName(path)!;
		string? packageName = null;
		if (path.EndsWith(".gemspec", StringComparison.OrdinalIgnoreCase))
		{
			var name = Regex.Match(content,
				"""\b(?:name|spec\.name)\s*=\s*['\"](?<name>[^'\"]+)['\"]""",
				RegexOptions.CultureInvariant);
			if (name.Success) packageName = name.Groups["name"].Value;
		}
		var directories = new HashSet<string>(PathComparer);
		if (Path.GetFileName(path).Equals("Gemfile", StringComparison.OrdinalIgnoreCase))
		{
			foreach (Match match in Regex.Matches(content,
				"""\bgem\s*\(?\s*['\"][^'\"]+['\"]\s*,[^\r\n]*?\bpath\s*:\s*['\"](?<path>[^'\"]+)['\"]""",
				RegexOptions.CultureInvariant))
			{
				var relative = match.Groups["path"].Value;
				if (Path.IsPathFullyQualified(relative)) continue;
				var candidate = Path.GetFullPath(Path.Combine(directory, relative));
				if (IsWithin(root, candidate)) directories.Add(candidate);
			}
		}
		return new RubyProjectConfiguration(packageName, directories.Order(StringComparer.Ordinal).ToArray());
	}

	private static ConfigurationParseResult<ComposerProjectConfiguration> ParseComposerProject(string content)
	{
		try
		{
			using var document = JsonDocument.Parse(content);
			var root = document.RootElement;
			var name = root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
				? nameElement.GetString()
				: null;
			var dependencies = root.TryGetProperty("require", out var require) && require.ValueKind == JsonValueKind.Object
				? require.EnumerateObject().Select(static property => property.Name)
					.Where(static dependency => !dependency.StartsWith("php", StringComparison.OrdinalIgnoreCase) &&
						!dependency.StartsWith("ext-", StringComparison.OrdinalIgnoreCase))
					.Order(StringComparer.Ordinal).ToArray()
				: [];
			var paths = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
			if (root.TryGetProperty("autoload", out var autoload) && autoload.ValueKind == JsonValueKind.Object &&
			    autoload.TryGetProperty("psr-4", out var psr4) && psr4.ValueKind == JsonValueKind.Object)
			{
				foreach (var mapping in psr4.EnumerateObject())
				{
					var values = mapping.Value.ValueKind switch
					{
						JsonValueKind.String => new[] { mapping.Value.GetString()! },
						JsonValueKind.Array => mapping.Value.EnumerateArray()
							.Where(static item => item.ValueKind == JsonValueKind.String)
							.Select(static item => item.GetString()!).ToArray(),
						_ => []
					};
					paths[mapping.Name] = values;
				}
			}
			return ConfigurationParseResult<ComposerProjectConfiguration>.Valid(
				new ComposerProjectConfiguration(name, dependencies, paths));
		}
		catch (JsonException)
		{
			return ConfigurationParseResult<ComposerProjectConfiguration>.Failure(
				ComposerProjectConfiguration.Empty, DependencyConfigurationState.Corrupt,
				"Composer project configuration is invalid");
		}
	}
	private static readonly Lazy<IReadOnlySet<string>> DotNetCatalog = new(
		() => LoadCatalog("dotnet-net10.0.json"),
		LazyThreadSafetyMode.ExecutionAndPublication);
	private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlySet<string>>> PythonCatalogs = new(
		() => LoadCatalogMap("python-3.12-3.13.json"),
		LazyThreadSafetyMode.ExecutionAndPublication);
	private static readonly Lazy<IReadOnlySet<string>> NodeCatalog = new(
		() => LoadCatalog("node-24.json"),
		LazyThreadSafetyMode.ExecutionAndPublication);

	private static bool IsTypeScriptConfig(string path) => Path.GetFileName(path) is "tsconfig.json" or "jsconfig.json";
	private static bool IsPythonConfig(string path) => Path.GetFileName(path) is "pyproject.toml" or "setup.cfg";
	private static bool IsJavaConfig(string path) => Path.GetFileName(path) is "pom.xml" or "build.gradle" or "build.gradle.kts";
	private static void MarkAmbiguousScopeOwnership(
		IList<DependencyScopeDescriptor> scopes,
		ICollection<DependencyConfigurationDiagnostic> diagnostics,
		string root)
	{
		var ambiguous = scopes
			.Where(static scope => scope.HasConfiguration)
			.GroupBy(scope => (scope.LanguageId, Root: Path.GetFullPath(scope.Root)))
			.Where(static group => group.Count() > 1 && group.All(scope =>
				scope.ConfigurationState == DependencyConfigurationState.Valid))
			.ToArray();
		foreach (var group in ambiguous)
		{
			var scopeIds = group.Select(static scope => scope.ScopeId)
				.Order(StringComparer.Ordinal).ToArray();
			foreach (var scope in group)
			{
				var index = scopes.IndexOf(scope);
				scopes[index] = scope with
				{
					ConfigurationState = DependencyConfigurationState.UnsupportedSemantics,
					ConfigurationDiagnostic = AmbiguousConfigurationOwnershipReason
				};
				diagnostics.Add(new DependencyConfigurationDiagnostic(
					ScopeConfigurationPath(scope.ScopeId),
					DependencyConfigurationState.UnsupportedSemantics,
					AmbiguousConfigurationOwnershipReason,
					scopeIds));
			}
		}
	}

	private static string ScopeConfigurationPath(string scopeId)
	{
		var separator = scopeId.IndexOf(':');
		return separator < 0 ? scopeId : scopeId[(separator + 1)..];
	}

	private static string? FindNearestManifestFile(string directory, string root, string name, IReadOnlySet<string> manifest)
	{
		while (IsWithin(root, directory))
		{
			var candidate = Path.Combine(directory, name);
			if (manifest.Contains(candidate)) return candidate;
			if (Path.GetFullPath(directory) == Path.GetFullPath(root)) break;
			directory = Path.GetDirectoryName(directory)!;
		}
		return null;
	}
	private static void AddFallbackScope(ICollection<DependencyScopeDescriptor> scopes, string root, LanguageId language)
	{
		if (scopes.Any(scope => scope.LanguageId == language &&
		    (language != LanguageId.Python || PathComparer.Equals(
			    Path.GetFullPath(scope.Root), Path.GetFullPath(root))))) return;
		scopes.Add(new DependencyScopeDescriptor(
			$"root:{language.ToString().ToLowerInvariant()}", root, language, [],
			language == LanguageId.TypeScript ? "bundler" : null, false,
			new Dictionary<string, IReadOnlyList<string>>(), null, new HashSet<string>(),
			language == LanguageId.Python ? new[] { root, Path.Combine(root, "src") } : [],
			false)
		{
			ConfigurationState = DependencyConfigurationState.Missing,
			ConfigurationDiagnostic = "no owning configuration file in the manifest"
		});
	}
	private static string Fingerprint(string root, string path, string content) => $"{PortableRelative(root, path)}\0{Hash([content])}";
	private static string Hash(IEnumerable<string> values) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', values))));
	private static string PortableRelative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
	private static bool IsWithin(string root, string path)
	{
		var relative = Path.GetRelativePath(root, path);
		return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
	}
	private static bool IsNetworkPath(string path) =>
		path.StartsWith("\\\\", StringComparison.Ordinal) ||
		path.StartsWith("//", StringComparison.Ordinal);
	private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
	private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
	private sealed record ConfigurationParseResult<T>(
		T Value,
		DependencyConfigurationState State,
		string? Reason)
	{
		public static ConfigurationParseResult<T> Valid(T value) =>
			new(value, DependencyConfigurationState.Valid, null);

		public static ConfigurationParseResult<T> Failure(
			T value,
			DependencyConfigurationState state,
			string? reason) => new(value, state, reason ?? "configuration is unavailable");
	}
	private sealed record CSharpProjectConfiguration(
		string[] ProjectReferences,
		bool DisableTransitiveProjectReferences,
		bool HasInvalidDisableTransitiveProjectReferences)
	{
		public static CSharpProjectConfiguration Default { get; } = new([], false, false);
	}
	private sealed record PythonConfiguration(IReadOnlySet<string> Dependencies, string? Version)
	{
		public static PythonConfiguration Default { get; } = new(new HashSet<string>(), null);
	}

	private static IReadOnlyList<string> ParseCIncludeDirectories(string root, string directory, string content)
	{
		var results = new HashSet<string>(PathComparer);
		foreach (Match command in CIncludeDirectoriesRegex().Matches(content))
		{
			var body = command.Groups["body"].Value;
			foreach (Match tokenMatch in CMakeTokenRegex().Matches(body))
			{
				var token = tokenMatch.Groups["quoted"].Success
					? tokenMatch.Groups["quoted"].Value
					: tokenMatch.Groups["plain"].Value;
				if (token.Length == 0 || token is "PUBLIC" or "PRIVATE" or "INTERFACE" || token.Contains('$')) continue;
				var candidate = Path.GetFullPath(Path.Combine(directory, token.Replace('/', Path.DirectorySeparatorChar)));
				if (IsWithin(root, candidate)) results.Add(candidate);
			}
		}
		return results.Order(StringComparer.Ordinal).ToArray();
	}

	[GeneratedRegex(@"target_include_directories\s*\([^\s\)]+(?<body>[^\)]*)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex CIncludeDirectoriesRegex();
	[GeneratedRegex("\\\"(?<quoted>[^\\\"]+)\\\"|(?<plain>[^\\s]+)", RegexOptions.CultureInvariant)]
	private static partial Regex CMakeTokenRegex();
	private sealed record JavaProjectConfiguration(string ProjectKey, IReadOnlyList<string> ProjectReferences)
	{
		public static JavaProjectConfiguration Empty { get; } = new(string.Empty, []);
	}
	private sealed record RustProjectConfiguration(string? PackageName, IReadOnlyList<string> ProjectDirectories)
	{
		public static RustProjectConfiguration Empty { get; } = new(null, []);
	}
	private sealed record RubyProjectConfiguration(string? PackageName, IReadOnlyList<string> ProjectDirectories);
	private sealed record ComposerProjectConfiguration(
		string? PackageName,
		IReadOnlyList<string> Dependencies,
		IReadOnlyDictionary<string, IReadOnlyList<string>> AutoloadPaths)
	{
		public static ComposerProjectConfiguration Empty { get; } = new(
			null, [], new Dictionary<string, IReadOnlyList<string>>());
	}
	private sealed record TypeScriptConfiguration(
		string ModuleResolution,
		bool Legacy,
		IReadOnlyDictionary<string, IReadOnlyList<string>> Paths,
		bool AllowJavaScript,
		IReadOnlyList<string> ModuleSuffixes,
		bool HasCustomConditions,
		IReadOnlyList<string> RootDirectories,
		bool HasIgnoredRootDirectories)
	{
		public static readonly TypeScriptConfiguration Default = new(
			"bundler",
			false,
			new Dictionary<string, IReadOnlyList<string>>(),
			false,
			[""],
			false,
			[],
			false);
	}

	private readonly record struct OptionalConfigurationValue<T>(bool IsSpecified, T? Value);

	private sealed record TypeScriptBaseUrl(string DeclaringDirectory, string? Value);

	private sealed record TypeScriptPathMappings(
		string DeclaringDirectory,
		IReadOnlyDictionary<string, IReadOnlyList<string>> Values);

	private sealed record TypeScriptRootDirectories(
		string DeclaringDirectory,
		IReadOnlyList<string> Values);

	private sealed record TypeScriptConfigurationLayer(
		string? Extends,
		OptionalConfigurationValue<string> ModuleResolution,
		OptionalConfigurationValue<string> Module,
		OptionalConfigurationValue<TypeScriptBaseUrl> BaseUrl,
		OptionalConfigurationValue<TypeScriptPathMappings> Paths,
		OptionalConfigurationValue<bool> AllowJavaScript,
		OptionalConfigurationValue<IReadOnlyList<string>> ModuleSuffixes,
		OptionalConfigurationValue<TypeScriptRootDirectories> RootDirectories,
		OptionalConfigurationValue<bool> CustomConditions)
	{
		public static readonly TypeScriptConfigurationLayer Empty = new(
			null,
			default,
			default,
			default,
			default,
			default,
			default,
			default,
			default);
	}
}

internal interface IDependencyPathMetadata
{
	bool FileExists(string path);
	bool TryResolveContainedPath(string root, string path, out string resolvedPath);
}

internal sealed class DependencyPathMetadata : IDependencyPathMetadata
{
	public bool FileExists(string path) => File.Exists(path);

	public bool TryResolveContainedPath(string root, string path, out string resolvedPath)
	{
		root = Path.GetFullPath(root);
		path = Path.GetFullPath(path);
		resolvedPath = path;
		var relative = Path.GetRelativePath(root, path);
		if (!IsContainedRelative(relative))
			return false;

		var current = root;
		var segments = relative.Split(
			[Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
			StringSplitOptions.RemoveEmptyEntries);
		for (var index = 0; index < segments.Length; index++)
		{
			var candidate = Path.Combine(current, segments[index]);
			FileAttributes attributes;
			try
			{
				attributes = File.GetAttributes(candidate);
			}
			catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
			{
				resolvedPath = Path.Combine(current, Path.Combine(segments[index..]));
				return true;
			}
			catch (Exception exception) when (
				exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
			{
				return false;
			}

			if (!attributes.HasFlag(FileAttributes.ReparsePoint))
			{
				current = candidate;
				continue;
			}

			FileSystemInfo info = attributes.HasFlag(FileAttributes.Directory)
				? new DirectoryInfo(candidate)
				: new FileInfo(candidate);
			var linkTarget = info.LinkTarget;
			if (string.IsNullOrWhiteSpace(linkTarget))
				return false;
			var target = Path.GetFullPath(
				Path.IsPathFullyQualified(linkTarget)
					? linkTarget
					: Path.Combine(Path.GetDirectoryName(candidate)!, linkTarget));
			if (IsNetworkPath(target) || !IsContainedRelative(Path.GetRelativePath(root, target)))
				return false;
			current = target;
		}

		resolvedPath = current;
		return true;
	}

	private static bool IsContainedRelative(string relative) =>
		relative != ".." &&
		!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
		!Path.IsPathRooted(relative);

	private static bool IsNetworkPath(string path) =>
		path.StartsWith("\\\\", StringComparison.Ordinal) ||
		path.StartsWith("//", StringComparison.Ordinal);
}

internal interface IDependencyControlFileReader
{
	ValueTask<DependencyControlFileSnapshot> ReadAsync(
		string path,
		int maximumBytes,
		CancellationToken cancellationToken);
}

internal sealed record DependencyControlFileSnapshot(
	DependencyConfigurationState State,
	string Content,
	string? Reason,
	string FingerprintValue,
	bool CanCache = true);

internal sealed class BoundedDependencyControlFileReader(FileContentReadStreamOpener? sourceOpener = null) : IDependencyControlFileReader
{
	private static ReadOnlySpan<byte> Utf8Preamble => [0xEF, 0xBB, 0xBF];
	private static readonly UTF8Encoding StrictUtf8 = new(false, true);

	public async ValueTask<DependencyControlFileSnapshot> ReadAsync(
		string path,
		int maximumBytes,
		CancellationToken cancellationToken)
	{
		try
		{
			await using var stream = sourceOpener is null
				? new FileStream(
					path,
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read | FileShare.Delete,
					64 * 1024,
					FileOptions.Asynchronous | FileOptions.SequentialScan)
				: sourceOpener(path, 64 * 1024, FileShare.Read | FileShare.Delete, asynchronous: true);
			var length = stream.Length;
			var lastWrite = File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks;
			if (length > maximumBytes || length > int.MaxValue)
			{
				return Failure(
					DependencyConfigurationState.UnsupportedSemantics,
					$"configuration exceeds the {maximumBytes} byte limit",
					length,
					lastWrite);
			}

			var bytes = new byte[checked((int)length)];
			var written = 0;
			while (written < bytes.Length)
			{
				var read = await stream.ReadAsync(bytes.AsMemory(written), cancellationToken).ConfigureAwait(false);
				if (read == 0) break;
				written += read;
			}
			var currentLength = stream.Length;
			var currentLastWrite = File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks;
			if (written != bytes.Length || currentLength != length || currentLastWrite != lastWrite)
			{
				return Failure(
					DependencyConfigurationState.Corrupt,
					"configuration changed while it was being read",
					currentLength,
					currentLastWrite,
					canCache: false);
			}
			var offset = bytes.AsSpan().StartsWith(Utf8Preamble) ? Utf8Preamble.Length : 0;
			var content = StrictUtf8.GetString(bytes.AsSpan(offset));
			return new DependencyControlFileSnapshot(
				DependencyConfigurationState.Valid,
				content,
				null,
				$"valid:{length}:{lastWrite}:{Hash(bytes)}");
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (FileNotFoundException)
		{
			return Failure(DependencyConfigurationState.Missing, "configuration file is unavailable", 0, 0);
		}
		catch (DirectoryNotFoundException)
		{
			return Failure(DependencyConfigurationState.Missing, "configuration file is unavailable", 0, 0);
		}
		catch (DecoderFallbackException)
		{
			return Failure(DependencyConfigurationState.Corrupt, "configuration is not valid UTF-8", 0, 0);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return Failure(DependencyConfigurationState.Corrupt, "configuration file could not be read", 0, 0, canCache: false);
		}
	}

	private static DependencyControlFileSnapshot Failure(
		DependencyConfigurationState state,
		string reason,
		long length,
		long lastWrite,
		bool canCache = true) => new(state, string.Empty, reason, $"{state}:{length}:{lastWrite}:{reason}", canCache);

	private static string Hash(ReadOnlySpan<byte> bytes) =>
		Convert.ToHexStringLower(SHA256.HashData(bytes));

	private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
