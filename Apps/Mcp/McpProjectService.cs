namespace DevProjex.Mcp;

internal sealed class McpProjectService(
	McpProjectSourceResolver projectSources,
	McpProjectRootJail roots,
	McpServices services,
	bool hidePrivateData)
{
	public async Task<ProjectContextPlan> BuildPlanAsync(
		string? project,
		string? branch,
		IReadOnlyList<string>? paths,
		IReadOnlyList<string>? includePatterns,
		IReadOnlyList<string>? excludePatterns,
		string? profile,
		bool trackedOnly,
		long? maximumFileBytes,
		CancellationToken cancellationToken,
		bool includeOutputMetrics = true)
	{
		var source = await projectSources.ResolveAsync(project, branch, cancellationToken)
			.ConfigureAwait(false);
		var projectRoot = source.Root;
		if (trackedOnly && !IsGitRepository(projectRoot))
		{
			throw new McpToolException(
				McpErrorCodes.InvalidArguments,
				$"{McpErrorCodes.InvalidArguments}: project is not a git repository; omit tracked_only or choose a Git repository returned by list_projects.");
		}
		var profileReference = ResolveProfile(projectRoot, profile);
		var selection = await services.SelectionResolver
			.ResolveAsync(
				projectRoot,
				profileReference,
				new ProjectSelectionSpec(
					GitMode: trackedOnly ? GitFilteringMode.TrackedFilesOnly : null,
					HideSecrets: true,
					HidePrivateData: hidePrivateData),
				cancellationToken)
			.ConfigureAwait(false);
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
		if (plan.HasErrors)
		{
			var diagnostic = plan.Diagnostics.First(static item => item.Severity == ContextDiagnosticSeverity.Error);
			throw new McpToolException(
				McpErrorCodes.ProjectUnavailable,
				$"{McpErrorCodes.ProjectUnavailable}: project preparation failed ({diagnostic.Code}: {diagnostic.Message}). " +
				"Fix the reported project access or Git state and retry.");
		}
		ValidatePlanContainment(roots, projectRoot, plan.IncludedFiles, cancellationToken);
		ProjectContextPlan narrowed;
		if ((paths is null || paths.Count == 0) &&
		    (includePatterns is null || includePatterns.Count == 0) &&
		    (excludePatterns is null || excludePatterns.Count == 0))
		{
			narrowed = plan;
		}
		else
		{
			var globs = McpGlobSet.Create(includePatterns, excludePatterns);
			var requested = ResolveRequestedPaths(projectRoot, paths, cancellationToken);
			var selected = new List<string>();
			foreach (var path in plan.IncludedFiles)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!MatchesRequested(path, requested.Paths, requested.Directories))
					continue;

				var relativePath = ToRelative(projectRoot, path);
				if (globs.Includes(relativePath))
					selected.Add(relativePath);
			}
			if (selected.Count > 0)
			{
				narrowed = await services.Planner
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
		if (!plan.IncludedFiles.Contains(physical, PathComparer.Default))
		{
			throw new McpToolException(
				McpErrorCodes.PathNotFound,
				$"{McpErrorCodes.PathNotFound}: file '{path}' is not in the effective project selection. " +
				"Adjust the profile or patterns and call get_tree first.");
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

		var resolved = new HashSet<string>(PathComparer.Default);
		var directories = new HashSet<string>(PathComparer.Default);
		foreach (var path in paths)
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
			if (PathComparer.Default.Equals(parentPath, ancestorPath))
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
			new HashSet<string>(PathComparer.Default),
			new HashSet<string>(PathComparer.Default));
	}

	internal static string ToRelative(string root, string path) =>
		PathUtility.GetPortableRelativePath(root, path);

	internal static bool IsGitRepository(string root) =>
		Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git"));
}
