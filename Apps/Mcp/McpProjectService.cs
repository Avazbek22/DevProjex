namespace DevProjex.Mcp;

internal sealed class McpProjectService(McpRootRegistry roots, McpServices services)
{
	public async Task<ProjectContextPlan> BuildPlanAsync(
		string? project,
		IReadOnlyList<string>? paths,
		IReadOnlyList<string>? includePatterns,
		IReadOnlyList<string>? excludePatterns,
		string? profile,
		bool trackedOnly,
		CancellationToken cancellationToken)
	{
		var projectRoot = roots.ResolveProject(project);
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
					HidePrivateData: true),
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

		var plan = await services.Planner
			.BuildAsync(new ProjectContextRequest(projectRoot, selection), cancellationToken)
			.ConfigureAwait(false);
		if (plan.HasErrors)
		{
			var diagnostic = plan.Diagnostics.First(static item => item.Severity == ContextDiagnosticSeverity.Error);
			throw new McpToolException(
				McpErrorCodes.ProjectUnavailable,
				$"{McpErrorCodes.ProjectUnavailable}: project preparation failed ({diagnostic.Code}: {diagnostic.Message}). " +
				"Fix the reported project access or Git state and retry.");
		}
		foreach (var file in plan.IncludedFiles)
			_ = roots.ResolveExistingPath(projectRoot, file);
		if ((paths is null || paths.Count == 0) &&
		    (includePatterns is null || includePatterns.Count == 0) &&
		    (excludePatterns is null || excludePatterns.Count == 0))
		{
			return plan;
		}

		var globs = McpGlobSet.Create(includePatterns, excludePatterns);
		var requested = ResolveRequestedPaths(projectRoot, paths);
		var selected = plan.IncludedFiles
			.Where(path => MatchesRequested(path, requested))
			.Where(path => globs.Includes(ToRelative(projectRoot, path)))
			.Select(path => ToRelative(projectRoot, path))
			.ToArray();
		if (selected.Length > 0)
		{
			return await services.Planner
				.ReprojectSelectionAsync(plan, selected, cancellationToken)
				.ConfigureAwait(false);
		}

		var empty = await services.Planner
			.ReprojectSelectionAsync(plan, [".devprojex-mcp-empty-selection"], cancellationToken)
			.ConfigureAwait(false);
		return empty with
		{
			Diagnostics = empty.Diagnostics
				.Where(static diagnostic => diagnostic.Code != "DPX-SELECTION-PATH-MISSING")
				.ToArray()
		};
	}

	public McpDetailResolution ResolveDetail(ProjectContextPlan plan, McpDetailLevel detail) =>
		McpDetailPolicy.Resolve(plan.Selection, detail);

	public ProjectContextPlan ApplyDetail(ProjectContextPlan plan, McpDetailResolution resolution) =>
		plan with { Selection = McpDetailPolicy.Apply(plan.Selection, resolution) };

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
				SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData))!;
	}

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
		if (string.IsNullOrWhiteSpace(profile) || profile.Equals("standard", StringComparison.Ordinal))
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

	private IReadOnlyList<string>? ResolveRequestedPaths(string projectRoot, IReadOnlyList<string>? paths)
	{
		if (paths is null || paths.Count == 0)
			return null;
		return paths
			.Select(path => roots.ResolveExistingPath(projectRoot, path))
			.Distinct(PathComparer.Default)
			.ToArray();
	}

	private static bool MatchesRequested(string file, IReadOnlyList<string>? requested)
	{
		if (requested is null)
			return true;
		foreach (var path in requested)
		{
			if (PathComparer.Default.Equals(file, path))
				return true;
			if (Directory.Exists(path) && PathUtility.IsPathInside(file, path))
				return true;
		}
		return false;
	}

	internal static string ToRelative(string root, string path) =>
		Path.GetRelativePath(root, path).Replace('\\', '/');

	internal static bool IsGitRepository(string root) =>
		Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git"));
}
