namespace DevProjex.Mcp;

internal sealed class McpProjectService(
	McpProjectSourceResolver projectSources,
	McpProjectRootJail roots,
	McpServices services,
	bool hidePrivateData,
	GitFilteringMode? serverGitMode,
	IReadOnlyCollection<ProjectExclusion>? serverExclusions = null)
{
	internal const int MaximumRequestedPaths = 256;
	internal const int MaximumRequestedPathLength = 4096;

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
		// per-call set outranks both — the human enabled that delegation at startup.
		IReadOnlyCollection<ProjectExclusion>? baselineExclusions =
			exclusions ?? (string.IsNullOrEmpty(profile) ? serverExclusions : null);
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
		var plan = await (includeOutputMetrics
				? services.Planner.BuildAsync(request, cancellationToken)
				: services.Planner.BuildStructureAsync(request, cancellationToken))
			.ConfigureAwait(false);
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
		ValidatePlanContainment(roots, projectRoot, plan.IncludedFiles, cancellationToken);
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
					$"({diagnostic.Code}: {diagnostic.Message}). Verify the repository and refs, then retry.");
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

		return await ProjectFileSizeFilter
			.ApplyAsync(services.Planner, narrowed, maximumFileBytes, cancellationToken)
			.ConfigureAwait(false);
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
				directories.Add(directory);
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

	public IFileContentAnalyzer CreatePreparedAnalyzer(PreparedSecretRedactionOutput prepared) =>
		services.OutputPreparer.CreatePreparedAnalyzer(prepared);

	public string ResolveFile(ProjectContextPlan plan, string path)
	{
		var physical = roots.ResolveExistingPath(plan.SourceRoot, path);
		if (Directory.Exists(physical))
		{
			throw new McpToolException(
				McpErrorCodes.PathNotFound,
				$"{McpErrorCodes.PathNotFound}: '{path}' is a directory; provide a file path returned by get_tree or search_project.");
		}
		if (!plan.IncludedFiles.Contains(physical, StringComparer.Ordinal))
		{
			throw new McpToolException(
				McpErrorCodes.PathNotFound,
				$"{McpErrorCodes.PathNotFound}: file '{path}' is not in the effective project selection. " +
				"Repeat the selection arguments of the call that listed it, or retrieve it with pack_context and read_pack.");
		}
		return physical;
	}

	public bool HasLocalProfile(string projectRoot) =>
		services.ProfileStore.TryLoadProfile(projectRoot, out _);

	public TreeExportService TreeExportService => services.TreeExportService;
	public ProjectContextDocumentService DocumentService => services.DocumentService;

	private ProjectProfileReference ResolveProfile(string projectRoot, string? profile)
	{
		if (string.IsNullOrEmpty(profile) ||
		    (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(profile)) ||
		    profile.Equals("standard", StringComparison.Ordinal))
			return ProjectProfileReference.Standard;
		if (profile.Equals("local", StringComparison.Ordinal))
			return ProjectProfileReference.Local;

		var path = roots.ResolveExistingPath(projectRoot, profile);
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

		var distinctTokens = NormalizeRequestedPathTokens(projectRoot, paths);
		var resolved = new HashSet<string>(StringComparer.Ordinal);
		var directories = new HashSet<string>(StringComparer.Ordinal);
		foreach (var path in distinctTokens)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var fullPath = roots.ResolveExistingPath(projectRoot, path);
			if (!resolved.Add(fullPath))
				continue;
			if (Directory.Exists(fullPath))
				directories.Add(fullPath);
		}

		return new RequestedPathSelection(resolved, directories);
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
			try
			{
				var fullPath = Path.IsPathFullyQualified(path)
					? Path.GetFullPath(path)
					: Path.GetFullPath(path, projectRoot);
				var normalizedPath = Path.TrimEndingDirectorySeparator(PathUtility.Normalize(fullPath));
				if (seen.Add(normalizedPath))
					normalized.Add(normalizedPath);
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

		return normalized;
	}

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
		IReadOnlySet<string> Directories)
	{
		public static RequestedPathSelection Empty { get; } = new(
			new HashSet<string>(StringComparer.Ordinal),
			new HashSet<string>(StringComparer.Ordinal));
	}

	internal static string ToRelative(string root, string path) =>
		PathUtility.GetPortableRelativePath(root, path);

	internal static bool IsGitRepository(string root) =>
		GitRepositoryBoundaryProbe.ExistsAtOrAbove(root);
}
