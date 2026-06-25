using System.Diagnostics;

namespace DevProjex.Application.Services;

public sealed class ProjectAnalysisService(
	ScanOptionsUseCase scanOptions,
	BuildTreeUseCase buildTree,
	IgnoreOptionsService ignoreOptions,
	IgnoreRulesService ignoreRules,
	TreeExportService treeExport,
	IFileContentAnalyzer fileContentAnalyzer,
	Func<DateTimeOffset>? utcNowProvider = null)
{
	private readonly Func<DateTimeOffset> _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);

	public async Task<ProjectAnalysisReport> AnalyzeAsync(
		ProjectAnalysisRequest request,
		CancellationToken cancellationToken = default)
	{
		var loadedProject = Load(request, cancellationToken);
		return await BuildReportFromTreeAsync(loadedProject, cancellationToken).ConfigureAwait(false);
	}

	public LoadedProjectAnalysisRequest Load(
		ProjectAnalysisRequest request,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(request.RootPath))
			throw new ArgumentException("Root path is required.", nameof(request));

		var rootPath = PathUtility.Normalize(request.RootPath);
		if (!Directory.Exists(rootPath))
			throw new DirectoryNotFoundException($"Project path was not found: {request.RootPath}");

		var loadingStopwatch = Stopwatch.StartNew();
		var selectedRootFolders = NormalizeRootFolders(request.SelectedRootFolders);
		var selectedIgnoreOptions = ResolveSelectedIgnoreOptions(
			rootPath,
			selectedRootFolders,
			useAllRootFoldersForDefaults: request.SelectedRootFolders is null,
			request.SelectedIgnoreOptions,
			cancellationToken);
		var rules = ignoreRules.Build(rootPath, selectedIgnoreOptions, selectedRootFolders);
		var scan = scanOptions.Execute(new ScanOptionsRequest(rootPath, rules), cancellationToken);

		var allowedRootFolders = request.SelectedRootFolders is null
			? scan.RootFolders.ToArray()
			: selectedRootFolders.ToArray();
		var allowedExtensions = request.SelectedExtensions is null
			? scan.Extensions.ToArray()
			: NormalizeExtensions(request.SelectedExtensions).ToArray();

		rules = ignoreRules.Build(rootPath, selectedIgnoreOptions, allowedRootFolders);
		var treeResult = buildTree.Execute(new BuildTreeRequest(
			rootPath,
			new TreeFilterOptions(
				AllowedExtensions: allowedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase),
				AllowedRootFolders: allowedRootFolders.ToHashSet(PathComparer.Default),
				IgnoreRules: rules)),
			cancellationToken);
		loadingStopwatch.Stop();

		return new LoadedProjectAnalysisRequest(
			RootPath: rootPath,
			Tree: treeResult,
			AvailableRootFolders: scan.RootFolders,
			AvailableExtensions: scan.Extensions,
			SelectedRootFolders: allowedRootFolders,
			SelectedExtensions: allowedExtensions,
			SelectedIgnoreOptions: selectedIgnoreOptions,
			RootAccessDenied: scan.RootAccessDenied || treeResult.RootAccessDenied,
			HadAccessDenied: scan.HadAccessDenied || treeResult.HadAccessDenied,
			KnownLoadingElapsed: loadingStopwatch.Elapsed);
	}

	public async Task<ProjectAnalysisReport> BuildReportFromTreeAsync(
		LoadedProjectAnalysisRequest request,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var analysisStopwatch = Stopwatch.StartNew();
		var treeMetrics = treeExport.CalculateFullTreeMetrics(request.RootPath, request.Tree.Root, TreeTextFormat.Ascii);
		var contentMetrics = await CalculateContentMetricsAsync(request.Tree.OrderedFilePaths, cancellationToken)
			.ConfigureAwait(false);
		analysisStopwatch.Stop();

		var loadingElapsed = request.KnownLoadingElapsed ?? TimeSpan.Zero;
		var analysisElapsed = analysisStopwatch.Elapsed;
		var totalElapsed = loadingElapsed + analysisElapsed;
		var treeSummary = CountTree(request.Tree.Root);

		return new ProjectAnalysisReport(
			SchemaVersion: ProjectAnalysisReport.CurrentSchemaVersion,
			GeneratedUtc: _utcNowProvider(),
			RootPath: request.RootPath,
			Selection: new ProjectAnalysisSelectionReport(
				SelectedRootFolders: NormalizeRootFolders(request.SelectedRootFolders).ToArray(),
				SelectedExtensions: NormalizeExtensions(request.SelectedExtensions).ToArray(),
				SelectedIgnoreOptions: request.SelectedIgnoreOptions.OrderBy(static option => (int)option).ToArray()),
			Inventory: new ProjectAnalysisInventoryReport(
				AvailableRootFolders: request.AvailableRootFolders.OrderBy(static value => value, PathComparer.Default).ToArray(),
				AvailableExtensions: request.AvailableExtensions.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
				Tree: treeSummary),
			Metrics: new ProjectAnalysisOutputMetricsReport(
				Tree: ToReportMetrics(treeMetrics),
				Content: ToReportMetrics(contentMetrics)),
			Timing: new ProjectAnalysisTimingReport(
				LoadingMilliseconds: ToMilliseconds(loadingElapsed),
				AnalysisMilliseconds: ToMilliseconds(analysisElapsed),
				TotalMilliseconds: ToMilliseconds(totalElapsed)),
			Diagnostics: BuildDiagnostics(request));
	}

	public static ProjectAnalysisDiagnosticsReport BuildDiagnostics(LoadedProjectAnalysisRequest request) =>
		new(
			RootAccessDenied: request.RootAccessDenied,
			HadAccessDenied: request.HadAccessDenied,
			Warnings: BuildWarnings(request).ToArray());

	private IReadOnlyCollection<IgnoreOptionId> ResolveSelectedIgnoreOptions(
		string rootPath,
		IReadOnlyCollection<string> selectedRootFolders,
		bool useAllRootFoldersForDefaults,
		IReadOnlyCollection<IgnoreOptionId>? overrideOptions,
		CancellationToken cancellationToken)
	{
		if (overrideOptions is not null)
			return overrideOptions.Distinct().ToArray();

		var availability = ignoreRules.GetIgnoreOptionsAvailability(rootPath, selectedRootFolders);
		var discoveryOptions = ignoreOptions.GetOptions(availability)
			.Where(static option => option.DefaultChecked)
			.Select(static option => option.Id)
			.ToArray();
		var discoveryRules = ignoreRules.Build(rootPath, discoveryOptions, selectedRootFolders);
		var discoveryRootFolders = useAllRootFoldersForDefaults
			? scanOptions.GetRootFolders(rootPath, discoveryRules, cancellationToken).Value
			: selectedRootFolders;
		var scan = scanOptions.GetExtensionsAndIgnoreCountsForRootFolders(
			rootPath,
			discoveryRootFolders,
			discoveryRules,
			cancellationToken);
		var counts = scan.Value.IgnoreOptionCounts;

		// Headless automation has to start from the same dynamic defaults as the UI.
		// Git/smart/dot/hidden availability is structural, while empty and extensionless
		// options only exist when the current root selection exposes matching entries.
		var dynamicAvailability = availability with
		{
			IncludeEmptyFolders = counts.EmptyFolders > 0,
			EmptyFoldersCount = counts.EmptyFolders,
			IncludeEmptyFiles = counts.EmptyFiles > 0,
			EmptyFilesCount = counts.EmptyFiles,
			IncludeExtensionlessFiles = counts.ExtensionlessFiles > 0,
			ExtensionlessFilesCount = counts.ExtensionlessFiles
		};

		return ignoreOptions.GetOptions(dynamicAvailability)
			.Where(static option => option.DefaultChecked)
			.Select(static option => option.Id)
			.Distinct()
			.ToArray();
	}

	private async Task<ExportOutputMetrics> CalculateContentMetricsAsync(
		IReadOnlyList<string>? orderedFilePaths,
		CancellationToken cancellationToken)
	{
		if (orderedFilePaths is null || orderedFilePaths.Count == 0)
			return ExportOutputMetrics.Empty;

		var metricsInputs = new List<ContentFileMetrics>(orderedFilePaths.Count);
		foreach (var path in orderedFilePaths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var metrics = await fileContentAnalyzer.GetTextFileMetricsAsync(path, cancellationToken)
				.ConfigureAwait(false);
			if (metrics is null)
				continue;

			metricsInputs.Add(new ContentFileMetrics(
				Path: path,
				SizeBytes: metrics.SizeBytes,
				LineCount: metrics.LineCount,
				CharCount: metrics.CharCount,
				IsEmpty: metrics.IsEmpty,
				IsWhitespaceOnly: metrics.IsWhitespaceOnly,
				IsEstimated: metrics.IsEstimated,
				CrLfPairCount: metrics.CrLfPairCount,
				TrailingNewlineChars: metrics.TrailingNewlineChars,
				TrailingNewlineLineBreaks: metrics.TrailingNewlineLineBreaks));
		}

		return ExportOutputMetricsCalculator.FromOrderedContentFiles(metricsInputs);
	}

	private static ProjectTreeSummaryReport CountTree(TreeNodeDescriptor root)
	{
		var directories = 0;
		var files = 0;
		var accessDeniedDirectories = 0;
		var stack = new Stack<TreeNodeDescriptor>();
		stack.Push(root);

		while (stack.Count > 0)
		{
			var node = stack.Pop();
			if (node.IsDirectory)
			{
				directories++;
				if (node.IsAccessDenied)
					accessDeniedDirectories++;
			}
			else
			{
				files++;
			}

			for (var i = node.Children.Count - 1; i >= 0; i--)
				stack.Push(node.Children[i]);
		}

		return new ProjectTreeSummaryReport(directories, files, accessDeniedDirectories);
	}

	private static IEnumerable<string> BuildWarnings(LoadedProjectAnalysisRequest request)
	{
		foreach (var root in request.SelectedRootFolders)
		{
			if (!request.AvailableRootFolders.Contains(root, PathComparer.Default))
				yield return $"Selected root folder was not found in the current project: {root}";
		}

		foreach (var extension in request.SelectedExtensions)
		{
			if (!request.AvailableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
				yield return $"Selected extension was not found in the current project: {extension}";
		}
	}

	private static IReadOnlyCollection<string> NormalizeRootFolders(IReadOnlyCollection<string>? rootFolders)
	{
		if (rootFolders is null || rootFolders.Count == 0)
			return [];

		return rootFolders
			.Where(static value => !string.IsNullOrWhiteSpace(value))
			.Select(static value => value.Trim())
			.Distinct(PathComparer.Default)
			.OrderBy(static value => value, PathComparer.Default)
			.ToArray();
	}

	private static IReadOnlyCollection<string> NormalizeExtensions(IReadOnlyCollection<string>? extensions)
	{
		if (extensions is null || extensions.Count == 0)
			return [];

		return extensions
			.Where(static value => !string.IsNullOrWhiteSpace(value))
			.Select(NormalizeExtension)
			.Where(static value => value.Length > 0)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static string NormalizeExtension(string value)
	{
		var trimmed = value.Trim();
		if (trimmed.Length == 0 || trimmed == ".")
			return string.Empty;

		return trimmed[0] == '.' ? trimmed : "." + trimmed;
	}

	private static ProjectOutputMetricsReport ToReportMetrics(ExportOutputMetrics metrics) =>
		new(metrics.Lines, metrics.Chars, metrics.Tokens);

	private static double ToMilliseconds(TimeSpan elapsed) =>
		Math.Round(elapsed.TotalMilliseconds, 3, MidpointRounding.AwayFromZero);
}

public sealed record ProjectAnalysisRequest(
	string RootPath,
	IReadOnlyCollection<string>? SelectedRootFolders = null,
	IReadOnlyCollection<string>? SelectedExtensions = null,
	IReadOnlyCollection<IgnoreOptionId>? SelectedIgnoreOptions = null);

public sealed record LoadedProjectAnalysisRequest(
	string RootPath,
	BuildTreeResult Tree,
	IReadOnlyCollection<string> AvailableRootFolders,
	IReadOnlyCollection<string> AvailableExtensions,
	IReadOnlyCollection<string> SelectedRootFolders,
	IReadOnlyCollection<string> SelectedExtensions,
	IReadOnlyCollection<IgnoreOptionId> SelectedIgnoreOptions,
	bool RootAccessDenied,
	bool HadAccessDenied,
	TimeSpan? KnownLoadingElapsed = null);
