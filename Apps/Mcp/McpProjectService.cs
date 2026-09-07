using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DevProjex.Application.Diagnostics;

namespace DevProjex.Mcp;

internal sealed class McpProjectService(
	McpProjectSourceResolver projectSources,
	McpProjectRootJail roots,
	McpServices services,
	bool hidePrivateData,
	GitFilteringMode? serverGitMode,
	IReadOnlyCollection<ProjectExclusion>? serverExclusions = null,
	bool agentExclusions = false,
	Func<string, CancellationToken, ValueTask>? inventoryBuilt = null) : IDisposable
{
	internal const int MaximumRequestedPaths = 256;
	internal const int MaximumRequestedPathLength = 4096;
	private const int MaximumCachedInventories = 8;
	private const int MaximumCachedProjections = 16;
	private const int MaximumRootMonitors = 8;
	private readonly ConcurrentDictionary<McpInventoryCacheKey, CachedInventoryEntry> inventoryCache = [];
	private readonly ConcurrentDictionary<McpProjectionCacheKey, CachedProjectionPlan> projectionCache = [];
	private readonly Dictionary<string, RootChangeMonitor> rootMonitors = new(PathComparer.Default);
	private readonly object rootMonitorSync = new();
	private long cacheGeneration;
	private int disposed;

	/// <summary>The Git baseline every call starts from when it names no profile.</summary>
	public GitFilteringMode ServerGitMode => serverGitMode ?? McpServerBaseline.DefaultGitMode;

	/// <summary>The exclusion baseline every call starts from when it names no profile.</summary>
	public IReadOnlyCollection<ProjectExclusion> ServerExclusions =>
		serverExclusions ?? McpServerBaseline.DefaultExclusions;

	public async Task<ProjectContextPlan> BuildPlanAsync(
		string? project,
		string? branch,
		IReadOnlyList<string>? paths,
		IReadOnlyList<string>? includePatterns,
		IReadOnlyList<string>? excludePatterns,
		string? profile,
		bool trackedOnly,
		string? gitScope,
		long? maximumFileBytes,
		CancellationToken cancellationToken,
		bool includeOutputMetrics = true,
		IReadOnlyList<ProjectExclusion>? exclusions = null)
	{
		using var selectionStage = ContentPipelineDiagnostics.MeasureStage(ContentPipelineStage.Selection);
		var parsedScope = ParseGitScope(gitScope);
		var hasSelectionFilters =
			paths is { Count: > 0 } ||
			includePatterns is { Count: > 0 } ||
			excludePatterns is { Count: > 0 };
		var globs = McpGlobSet.Create(includePatterns, excludePatterns);
		var source = await projectSources.ResolveAsync(project, branch, cancellationToken)
			.ConfigureAwait(false);
		var projectRoot = source.Root;
		var requested = ResolveRequestedPaths(projectRoot, paths, cancellationToken);
		var profileReference = ResolveProfile(projectRoot, profile);
		var baselineGitMode = trackedOnly
			? GitFilteringMode.TrackedFilesOnly
			: string.IsNullOrEmpty(profile)
				? serverGitMode
				: null;
		// The startup exclusion baseline follows the --git-mode precedent: an explicit
		// profile carries its own exclusion state, so the baseline yields to it. A delegated
		// per-call set outranks both — the human enabled that delegation at startup. Without
		// a startup line the server default applies, never the desktop standard set: that set
		// was designed for a person who can see what a checkbox hides.
		IReadOnlyCollection<ProjectExclusion>? baselineExclusions =
			exclusions ?? (string.IsNullOrEmpty(profile) ? ServerExclusions : null);
		var selection = await services.SelectionResolver
			.ResolveAsync(
				projectRoot,
				profileReference,
				new ProjectSelectionSpec(
					GitMode: baselineGitMode,
					Exclusions: baselineExclusions,
					HideSecrets: true,
					HidePrivateData: hidePrivateData),
				cancellationToken)
			.ConfigureAwait(false);
		if (parsedScope is { } narrowingScope)
		{
			selection = GitScopeSelection.WithMode(
				selection,
				GitScopeSelection.ComposeNarrowingUnderlay(
					selection.GitMode!.Value,
					narrowingScope.Mode));
		}
		var marks = ProjectSelectionMarkedSecretsResolver.Resolve(selection);
		if (await services.RedactionSession
			    .EnsurePersistentIdentityReadyAsync(marks, cancellationToken)
			    .ConfigureAwait(false) != PersistentSecretIdentityAvailability.Ready)
		{
			throw new McpToolException(
				McpErrorCodes.InvalidArguments,
				$"{McpErrorCodes.InvalidArguments}: the selected profile's persistent redaction identity is unavailable; use profile 'standard'.");
		}
		if (profileReference.Kind == ProjectProfileSourceKind.Local)
		{
			services.RedactionSession.ReplacePersistentMarks(
				projectRoot,
				new PersistentSecretMarksSnapshot(0, marks));
		}
		else
		{
			services.RedactionSession.ReplaceMarkedSecrets(marks);
		}

		var request = new ProjectContextRequest(projectRoot, selection, source.Identity);
		// Local profiles carry complete checkbox maps in storage outside the watched project root.
		// Rebuild them until that store can supply a coherent revision for the cache key.
		var plan = await BuildBasePlanAsync(
			request,
			includeOutputMetrics,
			allowInventoryReuse:
				parsedScope is null &&
				profileReference.Kind != ProjectProfileSourceKind.Local,
			cancellationToken).ConfigureAwait(false);
		if ((trackedOnly || parsedScope is not null) && !plan.GitReadiness.HasRepositoryBoundary)
		{
			var constraint = (trackedOnly, parsedScope is not null) switch
			{
				(true, true) => "tracked_only and git_scope",
				(true, false) => "tracked_only",
				_ => "git_scope"
			};
			throw new McpToolException(
				McpErrorCodes.InvalidArguments,
				$"{McpErrorCodes.InvalidArguments}: project is not a git repository; omit " +
				$"{constraint} or choose a Git repository returned by list_projects.");
		}
		if (plan.HasErrors)
		{
			var diagnostic = plan.Diagnostics.First(static item => item.Severity == ContextDiagnosticSeverity.Error);
			throw new McpToolException(
				McpErrorCodes.ProjectUnavailable,
				$"{McpErrorCodes.ProjectUnavailable}: project preparation failed ({diagnostic.Code}: {diagnostic.Message}). " +
				"Fix the reported project access or Git state and retry.");
		}
		ValidateRequestedPathCasing(plan, requested);
		if (maximumFileBytes is not null)
			plan = RefreshEffectiveFileSizes(plan);
		var allowProjectionReuse = parsedScope is null &&
		                           profileReference.Kind != ProjectProfileSourceKind.Local &&
		                           maximumFileBytes is null &&
		                           CanMonitorRepositoryState(projectRoot);
		var projectionKey = new McpProjectionCacheKey(
			RuntimeHelpers.GetHashCode(plan),
			BuildProjectionIdentity(
				requested,
				includePatterns,
				excludePatterns,
				maximumFileBytes));
		if (allowProjectionReuse &&
		    projectionCache.TryGetValue(projectionKey, out var cachedProjection) &&
		    ReferenceEquals(cachedProjection.BasePlan, plan))
		{
			// The immutable projection only contains paths from the validated base plan. Content
			// consumers still open every source through the root-jail handle validator.
			return cachedProjection.Plan;
		}
		var selectionFrontier = BuildSelectionFrontier(
			projectRoot,
			plan.IncludedFiles,
			plan.IncludedFolders,
			requested,
			globs,
			hasSelectionFilters,
			cancellationToken);
		if (parsedScope is { } scope)
		{
			string? resolvedDiffRange = null;
			if (scope.Mode == GitFilteringMode.Diff && source.Identity?.SourceType == ProjectSourceType.GitClone)
			{
				resolvedDiffRange = await projectSources.ResolveRemoteDiffRangeAsync(
					source,
					project!,
					scope.DiffRange!,
					cancellationToken).ConfigureAwait(false);
			}
			plan = await GitScopeFilter
				.ApplyAsync(
					services.Planner,
					plan,
					services.GitScopePathProvider,
					scope.Mode,
					scope.DiffRange,
					selectionFrontier?.ProjectionPaths,
					cancellationToken,
					resolvedDiffRange)
				.ConfigureAwait(false);
			if (plan.HasErrors)
			{
				var diagnostic = plan.Diagnostics.First(static item =>
					item.Severity == ContextDiagnosticSeverity.Error);
				throw new McpToolException(
					McpErrorCodes.ProjectUnavailable,
					$"{McpErrorCodes.ProjectUnavailable}: Git state preparation failed " +
					$"({diagnostic.Code}: {diagnostic.Message}). Verify the repository and refs, then retry." +
					(McpTrustedDiagnosticFormatter.FormatBlocking(diagnostic) is { } trailer
						? Environment.NewLine + trailer
						: string.Empty));
			}
		}
		ProjectContextPlan narrowed;
		if (selectionFrontier is null)
		{
			narrowed = plan;
		}
		else
		{
			var selected = new List<string>();
			if (parsedScope is null)
			{
				foreach (var path in selectionFrontier.ProjectionPaths)
				{
					cancellationToken.ThrowIfCancellationRequested();
					selected.Add(ToRelative(projectRoot, path));
				}
			}
			else
			{
				foreach (var path in plan.IncludedFiles)
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (selectionFrontier.FilePaths.Contains(path))
						selected.Add(ToRelative(projectRoot, path));
				}
			}
			if (selected.Count > 0)
			{
				var gitMode = plan.Selection.GitMode ?? GitFilteringMode.None;
				narrowed = GitScopeSelection.IsMomentary(gitMode)
					? await services.Planner
						.ReprojectSelectionAsync(
							plan,
							selected,
							StringComparer.Ordinal,
							cancellationToken)
						.ConfigureAwait(false)
					: await services.Planner
						.ReprojectSelectionAsync(plan, selected, cancellationToken)
						.ConfigureAwait(false);
			}
			else
			{
				narrowed = await services.Planner
					.ReprojectEmptySelectionAsync(plan, cancellationToken)
					.ConfigureAwait(false);
			}
		}

		var final = await ProjectFileSizeFilter
			.ApplyAsync(services.Planner, narrowed, maximumFileBytes, cancellationToken)
			.ConfigureAwait(false);
		ValidatePlanContainment(roots, projectRoot, final.IncludedFiles, cancellationToken);
		if (allowProjectionReuse)
		{
			var projection = new CachedProjectionPlan(
				plan,
				final,
				Interlocked.Increment(ref cacheGeneration));
			if (projectionCache.TryAdd(projectionKey, projection))
			{
				TrimProjectionCache();
			}
		}
		return final;
	}

	private async Task<ProjectContextPlan> BuildBasePlanAsync(
		ProjectContextRequest request,
		bool includeOutputMetrics,
		bool allowInventoryReuse,
		CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
		if (!allowInventoryReuse || !CanMonitorRepositoryState(request.ProjectPath))
			return await BuildUncachedAsync().ConfigureAwait(false);

		var monitor = GetOrCreateRootMonitor(request.ProjectPath);
		if (monitor is null || !monitor.IsReliable)
			return await BuildUncachedAsync().ConfigureAwait(false);

		for (var attempt = 0; attempt < 2; attempt++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var key = new McpInventoryCacheKey(
				PathUtility.Normalize(request.ProjectPath),
				BuildSelectionIdentity(request),
				includeOutputMetrics);
			var created = new CachedInventoryEntry(
				Interlocked.Increment(ref cacheGeneration),
				new Lazy<Task<CachedInventoryPlan>>(
				async () =>
				{
					var revisionBeforeBuild = monitor.Revision;
					var controlStampsBeforeBuild = CaptureBuildControlStamps(request.ProjectPath);
					var built = await BuildUncachedAsync().ConfigureAwait(false);
					if (inventoryBuilt is not null)
						await inventoryBuilt(request.ProjectPath, cancellationToken).ConfigureAwait(false);
					var builtCoherently = controlStampsBeforeBuild is not null &&
					                      controlStampsBeforeBuild.All(static stamp => stamp.IsCurrent()) &&
					                      ObservedControlFilesAreCurrent(built.ObservedControlFiles) &&
					                      monitor.Revision == revisionBeforeBuild &&
					                      monitor.IsReliable;
					var stamps = CapturePlanStamps(built);
					return new CachedInventoryPlan(
						built,
						revisionBeforeBuild,
						stamps,
						builtCoherently);
				},
				LazyThreadSafetyMode.ExecutionAndPublication));
			var entry = inventoryCache.GetOrAdd(key, created);
			if (ReferenceEquals(entry, created))
				TrimInventoryCache();

			CachedInventoryPlan cached;
			try
			{
				cached = await entry.Value.Value.ConfigureAwait(false);
			}
			catch
			{
				RemoveInventoryEntry(key, entry);
				throw;
			}
			var beforeValidation = monitor.Revision;
			var isCurrent = cached.BuiltCoherently &&
			                cached.Stamps is not null &&
			                cached.Revision == beforeValidation &&
			                cached.Stamps.All(static stamp => stamp.IsCurrent()) &&
			                ObservedControlFilesAreCurrent(cached.Plan.ObservedControlFiles) &&
			                monitor.Revision == beforeValidation &&
			                monitor.IsReliable &&
			                !cached.Plan.HasErrors;
			if (isCurrent)
				return cached.Plan;

			RemoveInventoryEntry(key, entry);
		}

		return await BuildUncachedAsync().ConfigureAwait(false);

		Task<ProjectContextPlan> BuildUncachedAsync() => includeOutputMetrics
			? services.Planner.BuildAsync(request, cancellationToken)
			: services.Planner.BuildStructureAsync(request, cancellationToken);
	}

	private static ProjectContextPlan RefreshEffectiveFileSizes(ProjectContextPlan plan)
	{
		var sizes = new Dictionary<string, long>(plan.IncludedFiles.Count, ProjectTreePathIdentity.CanonicalComparer);
		foreach (var path in plan.IncludedFiles)
		{
			try
			{
				sizes[path] = Math.Max(0, new FileInfo(path).Length);
			}
			catch (Exception exception) when (exception is
			       IOException or UnauthorizedAccessException or System.Security.SecurityException or
			       NotSupportedException or ArgumentException)
			{
				sizes[path] = 0;
			}
		}
		return plan with { EffectiveFileSizes = sizes };
	}

	private static bool CanMonitorRepositoryState(string projectRoot)
	{
		var gitMetadataPath = Path.Combine(projectRoot, ".git");
		if (Directory.Exists(gitMetadataPath))
			return true;
		if (File.Exists(gitMetadataPath))
			return GitRepositoryBoundaryProbe.TryResolveMetadataDirectories(projectRoot, out _, out _);
		return !GitRepositoryBoundaryProbe.ExistsAtOrAbove(projectRoot);
	}

	private RootChangeMonitor? GetOrCreateRootMonitor(string projectRoot)
	{
		var normalizedRoot = PathUtility.Normalize(projectRoot);
		lock (rootMonitorSync)
		{
			if (rootMonitors.TryGetValue(normalizedRoot, out var existing))
				return existing;
			if (rootMonitors.Count >= MaximumRootMonitors)
				return null;
			var created = RootChangeMonitor.TryCreate(normalizedRoot);
			if (created is not null)
				rootMonitors.Add(normalizedRoot, created);
			return created;
		}
	}

	private static IReadOnlyList<CachedPathStamp>? CapturePlanStamps(ProjectContextPlan plan)
	{
		try
		{
			var stamps = new List<CachedPathStamp>(8);
			var paths = new HashSet<string>(PathComparer.Default);
			paths.Add(plan.SourceRoot);
			stamps.Add(CachedPathStamp.Capture(plan.SourceRoot, expectDirectory: true));
			foreach (var directory in plan.IncludedFolders.Order(ProjectTreePathIdentity.CanonicalComparer))
			{
				if (paths.Add(directory))
					stamps.Add(CachedPathStamp.Capture(directory, expectDirectory: true));
			}
			return stamps;
		}
		catch (Exception exception) when (exception is
		       IOException or UnauthorizedAccessException or System.Security.SecurityException or
		       NotSupportedException or ArgumentException)
		{
			return null;
		}
	}

	private static bool ObservedControlFilesAreCurrent(
		IReadOnlyList<ProjectControlFileIdentity> observedControlFiles)
	{
		foreach (var observed in observedControlFiles)
		{
			try
			{
				var file = new FileInfo(observed.Path);
				file.Refresh();
				if (file.Exists != observed.Exists ||
				    file.Exists && (file.Length != observed.Length ||
				                    file.LastWriteTimeUtc.Ticks != observed.LastWriteTimeUtcTicks))
				{
					return false;
				}
			}
			catch (Exception exception) when (exception is
			       IOException or UnauthorizedAccessException or System.Security.SecurityException or
			       NotSupportedException or ArgumentException)
			{
				return false;
			}
		}
		return true;
	}

	private static IReadOnlyList<CachedPathStamp>? CaptureBuildControlStamps(string projectRoot)
	{
		try
		{
			var paths = new HashSet<string>(PathComparer.Default);
			var stamps = new List<CachedPathStamp>();
			Add(projectRoot, expectDirectory: true);
			Add(Path.Combine(projectRoot, ".gitignore"), expectDirectory: false);
			Add(Path.Combine(projectRoot, ".gitmodules"), expectDirectory: false);
			var metadataPath = Path.Combine(projectRoot, ".git");
			Add(metadataPath, expectDirectory: Directory.Exists(metadataPath));
			if (GitRepositoryBoundaryProbe.TryResolveMetadataDirectories(
				    projectRoot,
				    out var gitDirectory,
				    out var commonDirectory))
			{
				Add(Path.Combine(gitDirectory, "index"), expectDirectory: false);
				Add(Path.Combine(gitDirectory, "HEAD"), expectDirectory: false);
				Add(Path.Combine(gitDirectory, "commondir"), expectDirectory: false);
				Add(Path.Combine(gitDirectory, "config.worktree"), expectDirectory: false);
				Add(Path.Combine(commonDirectory, "config"), expectDirectory: false);
				Add(Path.Combine(commonDirectory, "info", "exclude"), expectDirectory: false);
			}
			return stamps;

			void Add(string path, bool expectDirectory)
			{
				if (paths.Add(path))
					stamps.Add(CachedPathStamp.Capture(path, expectDirectory));
			}
		}
		catch (Exception exception) when (exception is
		       IOException or UnauthorizedAccessException or System.Security.SecurityException or
		       NotSupportedException or ArgumentException)
		{
			return null;
		}
	}

	private static string BuildSelectionIdentity(ProjectContextRequest request)
	{
		var value = new StringBuilder();
		Append(value, request.SourceIdentity?.DisplayName);
		Append(value, request.SourceIdentity?.SourceType.ToString());
		Append(value, request.SourceIdentity?.SourceReference);
		Append(value, request.SourceIdentity?.RepositoryUrl);
		Append(value, request.SourceIdentity?.Branch);
		Append(value, request.SourceIdentity?.CommitHash);
		Append(value, request.SourceIdentity?.IsCachedRepository.ToString());
		var selection = request.Selection;
		AppendCollection(value, selection.Roots, ProjectTreePathIdentity.CanonicalComparer);
		AppendCollection(value, selection.Extensions, StringComparer.OrdinalIgnoreCase);
		AppendCollection(value, selection.SelectedPaths, ProjectTreePathIdentity.CanonicalComparer);
		Append(value, selection.GitMode?.ToString());
		AppendCollection(value, selection.Exclusions?.Select(static exclusion => exclusion.ToString()), StringComparer.Ordinal);
		Append(value, selection.HideSecrets?.ToString());
		Append(value, selection.HidePrivateData?.ToString());
		Append(value, selection.CompressCode?.ToString());
		Append(value, selection.StripComments?.ToString());
		Append(value, selection.StripBlankLines?.ToString());
		Append(value, selection.ProfileSource?.Kind.ToString());
		Append(value, selection.ProfileSource?.Path);
		Append(value, selection.GitDiffRange);
		Append(value, selection.ApplicationIntent?.ToString());
		return value.ToString();

		static void Append(StringBuilder builder, string? item)
		{
			builder.Append(item?.Length ?? -1).Append(':').Append(item).Append(';');
		}

		static void AppendCollection(
			StringBuilder builder,
			IEnumerable<string>? items,
			IComparer<string> comparer)
		{
			if (items is null)
			{
				Append(builder, null);
				return;
			}
			var ordered = items.Order(comparer).ToArray();
			Append(builder, ordered.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
			foreach (var item in ordered)
				Append(builder, item);
		}
	}

	private void TrimInventoryCache()
	{
		while (inventoryCache.Count > MaximumCachedInventories)
		{
			var oldest = inventoryCache.MinBy(static pair => pair.Value.Generation);
			if (oldest.Value is null || !RemoveInventoryEntry(oldest.Key, oldest.Value))
				break;
		}
	}

	private bool RemoveInventoryEntry(McpInventoryCacheKey key, CachedInventoryEntry entry)
	{
		if (!inventoryCache.TryRemove(new KeyValuePair<McpInventoryCacheKey, CachedInventoryEntry>(key, entry)))
			return false;
		if (entry.Value.IsValueCreated && entry.Value.Value.IsCompletedSuccessfully)
			RemoveProjectionsForBasePlan(entry.Value.Value.Result.Plan);
		return true;
	}

	private void RemoveProjectionsForBasePlan(ProjectContextPlan plan)
	{
		foreach (var pair in projectionCache)
		{
			if (ReferenceEquals(pair.Value.BasePlan, plan))
				projectionCache.TryRemove(pair);
		}
	}

	private static string BuildProjectionIdentity(
		RequestedPathSelection requested,
		IReadOnlyList<string>? includePatterns,
		IReadOnlyList<string>? excludePatterns,
		long? maximumFileBytes)
	{
		var value = new StringBuilder();
		Append(requested.Paths, ProjectTreePathIdentity.CanonicalComparer);
		Append(requested.Directories, ProjectTreePathIdentity.CanonicalComparer);
		Append(requested.Tokens.Select(static token => token.Value), StringComparer.Ordinal);
		Append(includePatterns, StringComparer.Ordinal);
		Append(excludePatterns, StringComparer.Ordinal);
		value.Append(maximumFileBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-");
		return value.ToString();

		void Append(IEnumerable<string>? items, IComparer<string> comparer)
		{
			if (items is null)
			{
				value.Append("-;");
				return;
			}
			foreach (var item in items.Order(comparer))
				value.Append(item.Length).Append(':').Append(item).Append(';');
			value.Append('|');
		}
	}

	private void TrimProjectionCache()
	{
		while (projectionCache.Count > MaximumCachedProjections)
		{
			var oldest = projectionCache.MinBy(static pair => pair.Value.Generation);
			if (oldest.Value is null || !projectionCache.TryRemove(oldest))
				break;
		}
	}

	private static McpSelectionFrontier? BuildSelectionFrontier(
		string projectRoot,
		IReadOnlyList<string> includedFiles,
		IReadOnlyList<string> includedFolders,
		RequestedPathSelection requested,
		McpGlobSet globs,
		bool hasSelectionFilters,
		CancellationToken cancellationToken)
	{
		if (!hasSelectionFilters)
			return null;

		var selectedFiles = new HashSet<string>(StringComparer.Ordinal);
		foreach (var path in includedFiles)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (MatchesRequested(path, requested.Paths, requested.Directories) &&
			    globs.Includes(ToRelative(projectRoot, path)))
			{
				selectedFiles.Add(path);
			}
		}

		var projectionPaths = new HashSet<string>(selectedFiles, StringComparer.Ordinal);
		if (requested.Directories.Count > 0)
		{
			var directoriesWithIncludedFiles = BuildDirectoryContentIndex(
				projectRoot,
				includedFiles,
				cancellationToken);
			foreach (var path in includedFolders)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (requested.Directories.Contains(path) &&
				    !directoriesWithIncludedFiles.Contains(path) &&
				    globs.IncludesDirectory(ToRelative(projectRoot, path)))
					projectionPaths.Add(path);
			}
		}

		return new McpSelectionFrontier(selectedFiles, projectionPaths);
	}

	private static IReadOnlySet<string> BuildDirectoryContentIndex(
		string projectRoot,
		IReadOnlyList<string> includedFiles,
		CancellationToken cancellationToken)
	{
		var directories = new HashSet<string>(StringComparer.Ordinal);
		foreach (var file in includedFiles)
		{
			cancellationToken.ThrowIfCancellationRequested();
			directories.Add(projectRoot);
			var directory = Path.GetDirectoryName(file);
			while (!string.IsNullOrEmpty(directory) &&
			       !StringComparer.Ordinal.Equals(directory, projectRoot))
			{
				if (!directories.Add(directory))
					break;
				var parent = Path.GetDirectoryName(directory);
				if (StringComparer.Ordinal.Equals(parent, directory))
					break;
				directory = parent;
			}
		}

		return directories;
	}

	private sealed record McpSelectionFrontier(
		IReadOnlySet<string> FilePaths,
		IReadOnlySet<string> ProjectionPaths);

	internal static void ValidatePlanContainment(
		McpProjectRootJail roots,
		string projectRoot,
		IReadOnlyList<string> includedFiles,
		CancellationToken cancellationToken)
	{
		foreach (var file in includedFiles)
		{
			cancellationToken.ThrowIfCancellationRequested();
			_ = roots.ResolveExistingPath(projectRoot, file);
		}
	}

	internal static void ValidatePlanContainment(
		McpRootRegistry roots,
		string projectRoot,
		IReadOnlyList<string> includedFiles,
		CancellationToken cancellationToken) =>
		ValidatePlanContainment(
			new McpProjectRootJail(roots),
			projectRoot,
			includedFiles,
			cancellationToken);

	public McpDetailResolution ResolveDetail(ProjectContextPlan plan, McpDetailLevel detail) =>
		McpDetailPolicy.Resolve(plan.Selection, detail);

	public ProjectContextPlan ApplyDetail(
		ProjectContextPlan plan,
		McpDetailResolution resolution,
		CancellationToken cancellationToken)
	{
		var selection = McpDetailPolicy.Apply(plan.Selection, resolution);
		return services.Planner.ApplyContentTransformationSelectionWithCancellation(
			plan,
			selection.HideSecrets == true,
			selection.CompressCode,
			selection.StripComments,
			selection.StripBlankLines,
			selection.HidePrivateData,
			cancellationToken);
	}

	private static McpGitScope? ParseGitScope(string? value)
	{
		if (value is null)
			return null;
		if (McpUnicodeLength.ExceedsScalarValueCount(value, GitScopeSelection.MaximumTokenLength))
		{
			throw new McpToolException(
				McpErrorCodes.InvalidArguments,
				$"{McpErrorCodes.InvalidArguments}: git_scope must be at most " +
				$"{GitScopeSelection.MaximumTokenLength} characters; use staged, changes, or a shorter diff:<ref>..<ref> range.");
		}
		var matchesPublishedSyntax = value is "staged" or "changes" ||
		                             value.StartsWith(GitScopeSelection.DiffPrefix, StringComparison.Ordinal);
		if (!matchesPublishedSyntax ||
		    !GitScopeSelection.TryParse(value, out var mode, out var diffRange) ||
		    !GitScopeSelection.IsMomentary(mode))
		{
			throw new McpToolException(
				McpErrorCodes.InvalidArguments,
				$"{McpErrorCodes.InvalidArguments}: invalid git_scope '{value}'. " +
				"Valid values: staged, changes, diff:<ref>..<ref>.");
		}
		return new McpGitScope(mode, diffRange);
	}

	public ContentTransformationContext CreateTransformationContext(
		ProjectContextPlan plan,
		McpDetailLevel detail = McpDetailLevel.Full)
	{
		var transformKinds = ResolveDetail(plan, detail).Kinds;
		return ContentTransformationContext.For(
			transformKinds == CodeTransformKinds.None
				? null
				: new CodeCompressionContext(plan.SourceRoot, services.CompressionSession, transformKinds),
			new SecretRedactionContext(
				plan.SourceRoot,
				services.RedactionSession,
				SecretRedactionFeatureSelection.Resolve(
					hideSecrets: true,
					hidePrivateData)))!;
	}

	public string ResolveProtectedDocumentRoot(ProjectContextPlan plan)
	{
		var displayRoot = plan.SourceIdentity is
		{
			SourceType: ProjectSourceType.GitClone,
			SourceReference.Length: > 0
		} identity
			? identity.SourceReference
			: plan.SourceRoot;
		var pathRedaction = OutputRootPathPresentation.CaptureRedactionDecision(
			CreateTransformationContext(plan));
		return OutputRootPathPresentation.ResolvePath(displayRoot, pathRedaction).Text;
	}

	public static string ResolveAddressDocumentRoot(ProjectContextPlan plan) =>
		plan.SourceIdentity is
		{
			SourceType: ProjectSourceType.GitClone,
			SourceReference.Length: > 0
		} identity
			? identity.SourceReference
			: plan.SourceRoot;

	public Task<PreparedSecretRedactionOutput> PrepareAsync(
		ProjectContextPlan plan,
		McpDetailLevel detail,
		CancellationToken cancellationToken) =>
		PrepareAsync(plan, detail, progress: null, cancellationToken);

	public async Task<PreparedSecretRedactionOutput> PrepareAsync(
		ProjectContextPlan plan,
		McpDetailLevel detail,
		IProgress<ProjectCopyExportProgress>? progress,
		CancellationToken cancellationToken) =>
		await services.OutputPreparer
			.PrepareAsync(
				CreateTransformationContext(plan, detail),
				plan.IncludedFiles,
				captureEffectiveFindings: false,
				cancellationToken,
				progress)
			.ConfigureAwait(false);

	public Task<PreparedSecretRedactionOutput> PrepareAsync(
		ProjectContextPlan plan,
		CancellationToken cancellationToken) =>
		PrepareAsync(plan, McpDetailLevel.Full, cancellationToken);

	public async Task<PreparedSecretRedactionOutput> MeasureAsync(
		ProjectContextPlan plan,
		McpDetailLevel detail,
		IProgress<ProjectCopyExportProgress>? progress,
		CancellationToken cancellationToken) =>
		await services.OutputPreparer
			.MeasureAsync(
				CreateTransformationContext(plan, detail),
				plan.IncludedFiles,
				captureEffectiveFindings: false,
				progress,
				cancellationToken)
			.ConfigureAwait(false);

	public IFileContentAnalyzer CreatePreparedAnalyzer(PreparedSecretRedactionOutput prepared) =>
		services.OutputPreparer.CreatePreparedAnalyzer(prepared);

	public string ResolveFile(ProjectContextPlan plan, string path)
	{
		string physical;
		try
		{
			physical = ResolveExistingInputPath(plan.SourceRoot, path);
		}
		catch (McpToolException exception) when (exception.Code == McpErrorCodes.PathNotFound)
		{
			throw ResolveCaseMismatch(plan, path) ?? exception;
		}
		if (Directory.Exists(physical))
		{
			throw new McpToolException(
				McpErrorCodes.PathNotFound,
				$"{McpErrorCodes.PathNotFound}: '{path}' is a directory; provide a file path returned by get_tree or search_project.");
		}
		if (!plan.IncludedFiles.Contains(physical, StringComparer.Ordinal))
		{
			var caseMismatch = ResolveCaseMismatch(plan, path);
			if (caseMismatch is not null)
				throw caseMismatch;

			// Name the filters and who can widen them. A remedy that cannot work on this
			// server — the old "repeat the selection arguments" — sends an agent in circles.
			var remedy = agentExclusions
				? "Pass the exclusions value of the call that listed it, or exclusions: [] to turn every toggle off; Git filtering is set on the server startup line."
				: $"Per-call arguments cannot widen these filters; only the server startup line can ({McpEffectiveFilters.StartupFlags}).";
			throw new McpToolException(
				McpErrorCodes.PathNotFound,
				$"{McpErrorCodes.PathNotFound}: file '{path}' is not in the effective project selection " +
				$"(effective filters: {McpEffectiveFilters.Describe(plan)}). {remedy}");
		}
		return physical;
	}

	public IReadOnlyList<string> ResolveRequestedFiles(
		ProjectContextPlan plan,
		IReadOnlyList<string> paths,
		CancellationToken cancellationToken)
	{
		var requested = ResolveRequestedPaths(plan.SourceRoot, paths, cancellationToken);
		ValidateRequestedPathCasing(plan, requested);
		return paths.Select(path => ResolveFile(plan, path)).ToArray();
	}

	public bool HasLocalProfile(string projectRoot) =>
		services.ProfileStore.TryLoadProfile(projectRoot, out _);

	public TreeExportService TreeExportService => services.TreeExportService;
	public ProjectContextDocumentService DocumentService => services.DocumentService;
	public DependencyFactsEngine DependencyFactsEngine => services.DependencyFactsEngine;

	private ProjectProfileReference ResolveProfile(string projectRoot, string? profile)
	{
		if (string.IsNullOrEmpty(profile) ||
		    (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(profile)) ||
		    profile.Equals("standard", StringComparison.Ordinal))
			return ProjectProfileReference.Standard;
		if (profile.Equals("local", StringComparison.Ordinal))
			return ProjectProfileReference.Local;

		string path;
		try
		{
			path = roots.ResolveExistingPath(projectRoot, profile);
		}
		catch (McpToolException exception) when (exception.Code == McpErrorCodes.PathNotFound)
		{
			throw new McpToolException(
				McpErrorCodes.InvalidArguments,
				$"{McpErrorCodes.InvalidArguments}: unknown profile '{McpTextEscaping.EscapeSingleLine(profile)}'; " +
				"use 'standard', 'local', or a profile JSON path inside the project root.");
		}
		if (Directory.Exists(path))
		{
			throw new McpToolException(
				McpErrorCodes.InvalidArguments,
				$"{McpErrorCodes.InvalidArguments}: profile '{profile}' is a directory; use 'standard', 'local', or a profile JSON file inside the project root.");
		}
		return new ProjectProfileReference(ProjectProfileSourceKind.Portable, path);
	}

	private RequestedPathSelection ResolveRequestedPaths(
		string projectRoot,
		IReadOnlyList<string>? paths,
		CancellationToken cancellationToken)
	{
		if (paths is null || paths.Count == 0)
			return RequestedPathSelection.Empty;

		var seen = new HashSet<string>(StringComparer.Ordinal);
		var resolved = new HashSet<string>(StringComparer.Ordinal);
		var directories = new HashSet<string>(StringComparer.Ordinal);
		var tokens = new List<RequestedPathToken>(paths.Count);
		foreach (var path in paths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!seen.Add(NormalizeRequestedPathToken(projectRoot, path)))
				continue;

			string fullPath;
			try
			{
				fullPath = ResolveExistingInputPath(projectRoot, path);
			}
			catch (McpToolException exception) when (exception.Code == McpErrorCodes.PathNotFound)
			{
				tokens.Add(new RequestedPathToken(path, null, IsDirectory: false, exception));
				continue;
			}

			var isDirectory = Directory.Exists(fullPath);
			tokens.Add(new RequestedPathToken(path, fullPath, isDirectory, ResolutionError: null));
			if (!resolved.Add(fullPath))
				continue;
			if (isDirectory)
				directories.Add(fullPath);
		}

		return new RequestedPathSelection(resolved, directories, tokens);
	}

	private static void ValidateRequestedPathCasing(
		ProjectContextPlan plan,
		RequestedPathSelection requested)
	{
		foreach (var token in requested.Tokens)
		{
			var matchesExactly = token.ResolvedPath is { } resolvedPath &&
				(token.IsDirectory
					? plan.IncludedFolders.Contains(resolvedPath, StringComparer.Ordinal)
					: plan.IncludedFiles.Contains(resolvedPath, StringComparer.Ordinal));
			if (matchesExactly)
				continue;

			var caseMismatch = ResolveCaseMismatch(plan, token.Value);
			if (caseMismatch is not null)
				throw caseMismatch;
			if (token.ResolutionError is not null)
				throw token.ResolutionError;
		}
	}

	internal static IReadOnlyList<string> NormalizeRequestedPathTokens(
		string projectRoot,
		IReadOnlyList<string> paths)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ArgumentNullException.ThrowIfNull(paths);
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var normalized = new List<string>(paths.Count);
		foreach (var path in paths)
		{
			var normalizedPath = NormalizeRequestedPathToken(projectRoot, path);
			if (seen.Add(normalizedPath))
				normalized.Add(normalizedPath);
		}

		return normalized;
	}

	private string ResolveExistingInputPath(string projectRoot, string path)
	{
		try
		{
			return roots.ResolveExistingPath(projectRoot, path);
		}
		catch (McpToolException exception) when (
			exception.Code == McpErrorCodes.PathNotFound &&
			TryUnescapeMarkdownPath(path, out var unescapedPath, out var escapedPunctuation))
		{
			try
			{
				return roots.ResolveExistingPath(projectRoot, unescapedPath);
			}
			catch (McpToolException retryException) when (retryException.Code == McpErrorCodes.PathNotFound)
			{
				throw new McpToolException(
					McpErrorCodes.PathNotFound,
					$"{exception.Message} The path contains markdown escaping from get_tree ('\\{escapedPunctuation}'); " +
					"use the unescaped spelling or get_tree with format=text.");
			}
		}
	}

	private static McpToolException? ResolveCaseMismatch(ProjectContextPlan plan, string requestedPath)
	{
		string requestedRelative;
		try
		{
			var requestedFullPath = Path.IsPathFullyQualified(requestedPath)
				? Path.GetFullPath(requestedPath)
				: Path.GetFullPath(requestedPath, plan.SourceRoot);
			requestedRelative = PathUtility.GetPortableRelativePath(plan.SourceRoot, requestedFullPath);
		}
		catch (Exception exception) when (
			exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return null;
		}

		var matches = plan.IncludedFiles
			.Select(file => PathUtility.GetPortableRelativePath(plan.SourceRoot, file))
			.Where(path => path.Equals(requestedRelative, StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.Ordinal)
			.Take(2)
			.ToArray();
		if (matches.Length != 1 || matches[0].Equals(requestedRelative, StringComparison.Ordinal))
			return null;

		return new McpToolException(
			McpErrorCodes.PathNotFound,
			$"{McpErrorCodes.PathNotFound}: file '{McpTextEscaping.EscapeSingleLine(requestedPath)}' differs only in letter case " +
			$"from the listed path '{McpTextEscaping.EscapeSingleLine(matches[0])}'; paths are case-sensitive on every platform — " +
			"retry with the listed spelling.");
	}

	private static string NormalizeRequestedPathToken(string projectRoot, string path)
	{
		try
		{
			var fullPath = Path.IsPathFullyQualified(path)
				? Path.GetFullPath(path)
				: Path.GetFullPath(path, projectRoot);
			return Path.TrimEndingDirectorySeparator(PathUtility.Normalize(fullPath));
		}
		catch (Exception exception) when (
			exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			throw new McpToolException(
				McpErrorCodes.InvalidArguments,
				$"{McpErrorCodes.InvalidArguments}: 'paths' contains an invalid path. " +
				"Use existing project-relative files or directories returned by get_tree.");
		}
	}

	private static bool TryUnescapeMarkdownPath(
		string path,
		out string unescapedPath,
		out char escapedPunctuation)
	{
		StringBuilder? output = null;
		escapedPunctuation = default;
		for (var index = 0; index < path.Length - 1; index++)
		{
			if (path[index] != '\\' || !IsAsciiPunctuation(path[index + 1]))
				continue;

			escapedPunctuation = path[index + 1];
			output = new StringBuilder(path.Length - 1);
			output.Append(path.AsSpan(0, index));
			for (; index < path.Length; index++)
			{
				if (path[index] == '\\' &&
				    index + 1 < path.Length &&
				    IsAsciiPunctuation(path[index + 1]))
				{
					continue;
				}
				output.Append(path[index]);
			}
			break;
		}

		unescapedPath = output?.ToString() ?? path;
		return output is not null;
	}

	private static bool IsAsciiPunctuation(char value) =>
		value is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~';

	private sealed record McpGitScope(GitFilteringMode Mode, string? DiffRange);

	internal static bool MatchesRequested(
		string file,
		IReadOnlySet<string> requestedPaths,
		IReadOnlySet<string> requestedDirectories)
	{
		if (requestedPaths.Count == 0)
			return true;

		var normalizedFile = PathUtility.Normalize(file);
		if (requestedPaths.Contains(normalizedFile))
			return true;

		var ancestorPath = Path.GetDirectoryName(normalizedFile);
		while (!string.IsNullOrEmpty(ancestorPath))
		{
			if (requestedDirectories.Contains(ancestorPath))
				return true;

			var parentPath = Path.GetDirectoryName(ancestorPath);
			if (StringComparer.Ordinal.Equals(parentPath, ancestorPath))
				break;
			ancestorPath = parentPath;
		}
		return false;
	}

	private sealed record RequestedPathSelection(
		IReadOnlySet<string> Paths,
		IReadOnlySet<string> Directories,
		IReadOnlyList<RequestedPathToken> Tokens)
	{
		public static RequestedPathSelection Empty { get; } = new(
			new HashSet<string>(StringComparer.Ordinal),
			new HashSet<string>(StringComparer.Ordinal),
			[]);
	}

	private sealed record RequestedPathToken(
		string Value,
		string? ResolvedPath,
		bool IsDirectory,
		McpToolException? ResolutionError);

	private readonly record struct McpInventoryCacheKey(
		string ProjectRoot,
		string SelectionIdentity,
		bool IncludeOutputMetrics);

	private readonly record struct McpProjectionCacheKey(
		int BasePlanIdentity,
		string ProjectionIdentity);

	private sealed record CachedProjectionPlan(
		ProjectContextPlan BasePlan,
		ProjectContextPlan Plan,
		long Generation);

	private sealed record CachedInventoryEntry(
		long Generation,
		Lazy<Task<CachedInventoryPlan>> Value);

	private sealed record CachedInventoryPlan(
		ProjectContextPlan Plan,
		long Revision,
		IReadOnlyList<CachedPathStamp>? Stamps,
		bool BuiltCoherently);

	private readonly record struct CachedPathStamp(
		string Path,
		bool ExpectDirectory,
		bool Exists,
		long Length,
		long LastWriteTimeUtcTicks,
		long CreationTimeUtcTicks,
		FileAttributes Attributes)
	{
		public static CachedPathStamp Capture(string path, bool expectDirectory)
		{
			try
			{
				var attributes = File.GetAttributes(path);
				var isDirectory = attributes.HasFlag(FileAttributes.Directory);
				if (isDirectory)
				{
					var directory = new DirectoryInfo(path);
					directory.Refresh();
					return new CachedPathStamp(
						path,
						expectDirectory,
						true,
						0,
						directory.LastWriteTimeUtc.Ticks,
						directory.CreationTimeUtc.Ticks,
						attributes);
				}

				var file = new FileInfo(path);
				file.Refresh();
				return new CachedPathStamp(
					path,
					expectDirectory,
					true,
					file.Length,
					file.LastWriteTimeUtc.Ticks,
					file.CreationTimeUtc.Ticks,
					attributes);
			}
			catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
			{
				return new CachedPathStamp(path, expectDirectory, false, 0, 0, 0, 0);
			}
		}

		public bool IsCurrent()
		{
			try
			{
				var current = Capture(Path, ExpectDirectory);
				return current == this &&
				       (!Exists || current.Attributes.HasFlag(FileAttributes.Directory) == ExpectDirectory);
			}
			catch (Exception exception) when (exception is
			       IOException or UnauthorizedAccessException or System.Security.SecurityException or
			       NotSupportedException or ArgumentException)
			{
				return false;
			}
		}
	}

	private sealed class RootChangeMonitor : IDisposable
	{
		private readonly FileSystemWatcher watcher;
		private long revision;
		private int reliable = 1;

		private RootChangeMonitor(FileSystemWatcher watcher)
		{
			this.watcher = watcher;
			watcher.Changed += OnChanged;
			watcher.Created += OnChanged;
			watcher.Deleted += OnChanged;
			watcher.Renamed += OnChanged;
			watcher.Error += OnError;
			watcher.EnableRaisingEvents = true;
		}

		public long Revision => Volatile.Read(ref revision);
		public bool IsReliable => Volatile.Read(ref reliable) != 0;

		public static RootChangeMonitor? TryCreate(string root)
		{
			try
			{
				return new RootChangeMonitor(new FileSystemWatcher(root)
				{
					IncludeSubdirectories = true,
					NotifyFilter = NotifyFilters.FileName |
					               NotifyFilters.DirectoryName |
					               NotifyFilters.Attributes |
					               NotifyFilters.Size |
					               NotifyFilters.LastWrite |
					               NotifyFilters.Security
				});
			}
			catch (Exception exception) when (exception is
			       IOException or UnauthorizedAccessException or System.Security.SecurityException or
			       PlatformNotSupportedException or ArgumentException)
			{
				return null;
			}
		}

		private void OnChanged(object sender, FileSystemEventArgs eventArgs) =>
			Interlocked.Increment(ref revision);

		private void OnError(object sender, ErrorEventArgs eventArgs)
		{
			Volatile.Write(ref reliable, 0);
			Interlocked.Increment(ref revision);
		}

		public void Dispose() => watcher.Dispose();
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref disposed, 1) != 0)
			return;
		inventoryCache.Clear();
		projectionCache.Clear();
		lock (rootMonitorSync)
		{
			foreach (var monitor in rootMonitors.Values)
				monitor.Dispose();
			rootMonitors.Clear();
		}
	}

	internal static string ToRelative(string root, string path) =>
		PathUtility.GetPortableRelativePath(root, path);

	internal static bool IsGitRepository(string root) =>
		GitRepositoryBoundaryProbe.ExistsAtOrAbove(root);
}
