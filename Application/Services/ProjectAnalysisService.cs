using System.Diagnostics;
using DevProjex.Application.Selection;

namespace DevProjex.Application.Services;

public sealed class ProjectAnalysisService(
	ScanOptionsUseCase scanOptions,
	BuildTreeUseCase buildTree,
	FilterOptionSelectionService filterSelectionService,
	IgnoreOptionsService ignoreOptions,
	IgnoreRulesService ignoreRules,
	TreeExportService treeExport,
	IFileContentAnalyzer fileContentAnalyzer,
	Func<DateTimeOffset>? utcNowProvider = null)
{
	private const int MaximumConcurrentContentMetricReads = 4;
	private const int ContentMetricBatchSize = 1024;
	private static readonly IReadOnlySet<string> EmptyRootSelection = new HashSet<string>(PathComparer.Default);
	private static readonly IReadOnlySet<string> EmptyExtensionSelection =
		new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	private static readonly IReadOnlySet<IgnoreOptionId> EmptyIgnoreSelection = new HashSet<IgnoreOptionId>();
	private static readonly IReadOnlyDictionary<IgnoreOptionId, bool> EmptyIgnoreState =
		new Dictionary<IgnoreOptionId, bool>();
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
		if (CanUseUnifiedDefaultSelectionPipeline(request) &&
		    buildTree.SupportsCompositeInventory)
		{
			return LoadWithUnifiedDefaultSelection(
				rootPath,
				loadingStopwatch,
				cancellationToken);
		}

		var selectedRootFolders = NormalizeRootFolders(request.SelectedRootFolders);
		var selectedIgnoreOptions = ResolveSelectedIgnoreOptions(
			rootPath,
			selectedRootFolders,
			useAllRootFoldersForDefaults: request.SelectedRootFolders is null,
			request.SelectedIgnoreOptions,
			cancellationToken);
		var discoveryRules = ignoreRules.Build(rootPath, selectedIgnoreOptions, selectedRootFolders);
		ScanOptionsResult scan;
		ProjectTreeInventorySnapshot? treeInventory = null;
		IReadOnlyList<string> allowedRootFolders;
		IgnoreRules rules;
		if (request.SelectedRootFolders is null &&
		    buildTree.SupportsCompositeInventory)
		{
			var rootFolders = scanOptions.GetRootFolders(rootPath, discoveryRules, cancellationToken);
			var rootProjectionRules = ignoreRules.Build(rootPath, selectedIgnoreOptions, rootFolders.Value);
			allowedRootFolders = RootFolderVisibilityProjection.ApplyScopedControllerRules(
				rootPath,
				rootFolders.Value,
				rootProjectionRules,
				cancellationToken);
			rules = ignoreRules.Build(rootPath, selectedIgnoreOptions, allowedRootFolders);
			var allowedRootFolderSet = allowedRootFolders.ToHashSet(PathComparer.Default);
			treeInventory = buildTree.ReadCompositeInventory(
				rootPath,
				allowedRootFolderSet,
				discoveryRules,
				rules,
				cancellationToken);

			var availableExtensions = new List<string>(
				ProjectTreeInventoryExtensionDiscovery.GetVisibleExtensions(
					treeInventory,
					discoveryRules,
					cancellationToken));
			availableExtensions.Sort(StringComparer.OrdinalIgnoreCase);
			scan = new ScanOptionsResult(
				Extensions: availableExtensions,
				RootFolders: allowedRootFolders,
				RootAccessDenied: rootFolders.RootAccessDenied || treeInventory.RootAccessDenied,
				HadAccessDenied: rootFolders.HadAccessDenied || treeInventory.HadAccessDenied);
		}
		else
		{
			scan = scanOptions.Execute(
				new ScanOptionsRequest(rootPath, discoveryRules),
				cancellationToken);
			if (request.SelectedRootFolders is null)
			{
				var rootProjectionRules = ignoreRules.Build(rootPath, selectedIgnoreOptions, scan.RootFolders);
				allowedRootFolders = RootFolderVisibilityProjection.ApplyScopedControllerRules(
					rootPath,
					scan.RootFolders,
					rootProjectionRules,
					cancellationToken);
				scan = scan with { RootFolders = allowedRootFolders };
			}
			else
			{
				allowedRootFolders = selectedRootFolders.ToArray();
			}
			rules = ignoreRules.Build(rootPath, selectedIgnoreOptions, allowedRootFolders);
		}

		var allowedExtensions = request.SelectedExtensions is null
			? scan.Extensions.ToArray()
			: NormalizeExtensions(request.SelectedExtensions).ToArray();

		var treeRequest = new BuildTreeRequest(
			rootPath,
			new TreeFilterOptions(
				AllowedExtensions: allowedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase),
				AllowedRootFolders: allowedRootFolders.ToHashSet(PathComparer.Default),
				IgnoreRules: rules));
		var treeResult = treeInventory is null
			? buildTree.Execute(treeRequest, cancellationToken)
			: buildTree.ExecuteWithInventory(treeRequest, treeInventory, cancellationToken).Tree;
		if (request.SelectedRootFolders is null)
		{
			allowedRootFolders = GetVisibleRootFolderNames(treeResult.Root);
			scan = scan with { RootFolders = allowedRootFolders };
		}
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

	private static bool CanUseUnifiedDefaultSelectionPipeline(ProjectAnalysisRequest request) =>
		request.SelectedRootFolders is null &&
		request.SelectedExtensions is null &&
		request.SelectedIgnoreOptions is null;

	private LoadedProjectAnalysisRequest LoadWithUnifiedDefaultSelection(
		string rootPath,
		Stopwatch loadingStopwatch,
		CancellationToken cancellationToken)
	{
		var selectionRefreshEngine = new SelectionRefreshEngine(
			scanOptions,
			filterSelectionService,
			ignoreOptions,
			(candidateRootPath, selectedOptions, selectedRootFolders) =>
				ignoreRules.Build(candidateRootPath, selectedOptions, selectedRootFolders),
			(candidateRootPath, selectedRootFolders) =>
				ignoreRules.GetIgnoreOptionsAvailability(candidateRootPath, selectedRootFolders));
		var snapshot = selectionRefreshEngine.ComputeFullRefreshSnapshot(
			new SelectionRefreshContext(
				Path: rootPath,
				PreparedSelectionMode: PreparedSelectionMode.Defaults,
				AllRootFoldersChecked: true,
				AllExtensionsChecked: true,
				RootSelectionInitialized: false,
				RootSelectionCache: EmptyRootSelection,
				ExtensionsSelectionInitialized: false,
				ExtensionsSelectionCache: EmptyExtensionSelection,
				IgnoreSelectionInitialized: false,
				IgnoreSelectionCache: EmptyIgnoreSelection,
				IgnoreOptionStateCache: EmptyIgnoreState,
				IgnoreAllPreference: null,
				CurrentSnapshotState: default,
				CaptureTreeInventory: true),
			cancellationToken);
		var inventory = snapshot.TreeInventory ??
		                throw new InvalidOperationException(
			                "The unified project selection scan did not produce a tree inventory.");
		var rootOptions = snapshot.RootOptions ?? [];
		var selectedRootFolders = rootOptions
			.Where(static option => option.IsChecked)
			.Select(static option => option.Name)
			.ToArray();
		var extensionOptions = snapshot.EffectiveExtensionOptions;
		var selectedExtensions = extensionOptions
			.Where(static option => option.IsChecked)
			.Select(static option => option.Name)
			.ToArray();
		var selectedIgnoreOptions = snapshot.IgnoreOptions
			.Where(static option => option.IsChecked)
			.Select(static option => option.Id)
			.ToArray();
		var rules = ignoreRules.Build(rootPath, selectedIgnoreOptions, selectedRootFolders);
		var treeRequest = new BuildTreeRequest(
			rootPath,
			new TreeFilterOptions(
				AllowedExtensions: selectedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase),
				AllowedRootFolders: selectedRootFolders.ToHashSet(PathComparer.Default),
				IgnoreRules: rules));
		var treeResult = buildTree.ExecuteWithInventory(
			treeRequest,
			inventory,
			cancellationToken).Tree;
		loadingStopwatch.Stop();

		return new LoadedProjectAnalysisRequest(
			RootPath: rootPath,
			Tree: treeResult,
			AvailableRootFolders: rootOptions.Select(static option => option.Name).ToArray(),
			AvailableExtensions: extensionOptions.Select(static option => option.Name).ToArray(),
			SelectedRootFolders: selectedRootFolders,
			SelectedExtensions: selectedExtensions,
			SelectedIgnoreOptions: selectedIgnoreOptions,
			RootAccessDenied: snapshot.RootAccessDenied || treeResult.RootAccessDenied,
			HadAccessDenied: snapshot.HadAccessDenied || treeResult.HadAccessDenied,
			KnownLoadingElapsed: loadingStopwatch.Elapsed);
	}

	private static IReadOnlyList<string> GetVisibleRootFolderNames(TreeNodeDescriptor root)
	{
		var names = new List<string>();
		foreach (var child in root.Children)
		{
			if (child.IsDirectory)
				names.Add(child.DisplayName);
		}

		return names;
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
				SelectedExtensions: NormalizeResolvedExtensionNames(request.SelectedExtensions).ToArray(),
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
		var extensionDiscoveryRules = IgnoreRulesProjection.ForExtensionAvailability(discoveryRules);
		var scan = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			rootPath,
			discoveryRootFolders,
			extensionDiscoveryRules,
			effectiveRules: discoveryRules,
			effectiveAllowedExtensions: (IReadOnlySet<string>?)null,
			includeDirectoryToggleProbeRoots: true,
			cancellationToken: cancellationToken,
			includeControllerImpactProbeRoots: true);
		var counts = scan.Value.EffectiveIgnoreOptionCounts;
		var controllerImpactCounts = scan.Value.ControllerImpactCounts;
		var snapshotState = new IgnoreSectionSnapshotState(
			HasIgnoreOptionCounts: true,
			IgnoreOptionCounts: counts,
			ControllerImpactCounts: controllerImpactCounts,
			HasExtensionlessEntries: counts.ExtensionlessFiles > 0,
			ExtensionlessEntriesCount: counts.ExtensionlessFiles);
		var dynamicAvailability = IgnoreOptionsAvailabilityResolver.Resolve(
			availability,
			snapshotState,
			new Dictionary<IgnoreOptionId, bool>(),
			stateCacheIsComplete: false);

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

		var parallelOptions = new ParallelOptions
		{
			MaxDegreeOfParallelism = Math.Min(
				MaximumConcurrentContentMetricReads,
				ScanParallelismPolicy.MaxDegreeOfParallelism),
			CancellationToken = cancellationToken
		};
		var accumulator = new ExportOutputMetricsCalculator.OrderedContentMetricsAccumulator();
		var batchMetrics = new TextFileMetrics?[Math.Min(ContentMetricBatchSize, orderedFilePaths.Count)];
		for (var batchStart = 0; batchStart < orderedFilePaths.Count; batchStart += batchMetrics.Length)
		{
			var batchCount = Math.Min(batchMetrics.Length, orderedFilePaths.Count - batchStart);
			await Parallel.ForAsync(
				0,
				batchCount,
				parallelOptions,
				async (batchIndex, token) =>
				{
					batchMetrics[batchIndex] = await fileContentAnalyzer
						.GetTextFileMetricsAsync(orderedFilePaths[batchStart + batchIndex], token)
						.ConfigureAwait(false);
				}).ConfigureAwait(false);

			for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var metrics = batchMetrics[batchIndex];
				if (metrics is null)
					continue;

				accumulator.AppendFile(new ContentFileMetrics(
					Path: orderedFilePaths[batchStart + batchIndex],
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

			Array.Clear(batchMetrics, 0, batchCount);
		}

		return accumulator.ToMetrics();
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

	private static IReadOnlyCollection<string> NormalizeResolvedExtensionNames(
		IReadOnlyCollection<string>? extensions)
	{
		if (extensions is null || extensions.Count == 0)
			return [];

		return extensions
			.Where(static value => !string.IsNullOrWhiteSpace(value))
			.Select(static value => value.Trim())
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
