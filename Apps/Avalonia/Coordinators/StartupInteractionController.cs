using DevProjex.Avalonia.Services;
using DevProjex.Application.Context;
using DevProjex.Application.DesktopControl;

namespace DevProjex.Avalonia.Coordinators;

internal sealed class StartupInteractionController(
    DesktopOpenRequest? desktopRequest,
    DesktopDiagnosticScenario? diagnosticScenario,
    MainWindowViewModel viewModel,
    SelectionSyncCoordinator selection,
    SearchFilterInteractionController searchFilter,
    PreviewWorkspaceController preview,
    WorkspacePresentationController workspace,
    SessionMetricsRecorder sessionMetrics,
    Func<string?> projectPathProvider,
    Func<TreeNodeDescriptor?> treeRootProvider,
    Func<Task> refreshTreeAsync,
    Func<string, Task<bool>> openProjectAsync,
    Action closeWindow,
	Action<Action>? applyTreeSelectionBatch = null)
{
    private const string BenchmarkIdleSecondsEnvironmentVariable =
        "DEVPROJEX_UI_BENCHMARK_IDLE_SECONDS";
    private const string BenchmarkSecondaryProjectEnvironmentVariable =
        "DEVPROJEX_UI_BENCHMARK_SECONDARY_PATH";
    private const string BenchmarkProjectReloadCountEnvironmentVariable =
        "DEVPROJEX_UI_BENCHMARK_PROJECT_RELOADS";
    private static readonly TimeSpan DefaultBenchmarkIdleSettleDuration =
        TimeSpan.FromSeconds(8);
    private static readonly TimeSpan UiDispatchProbeInterval =
        TimeSpan.FromMilliseconds(25);

    public async Task ApplySelectionOverridesAsync()
    {
        var selectionSpec = desktopRequest?.Selection;
        var currentPath = projectPathProvider();
        if (selectionSpec is null ||
            !viewModel.IsProjectLoaded ||
            string.IsNullOrWhiteSpace(currentPath))
        {
            return;
		}
		var applicationIntent = selectionSpec.ApplicationIntent;
		// TODO(cli): Remove root selection from Desktop requests when the public --root contract
		// is revised. Desktop intentionally applies the complete project scope.
		var extensionMode = applicationIntent?.Extensions ?? ResolveLegacyMode(selectionSpec.Extensions);
		var gitMode = applicationIntent?.GitMode ?? ResolveLegacyMode(selectionSpec.GitMode);
		var exclusionMode = applicationIntent?.Exclusions ?? ResolveLegacyMode(selectionSpec.Exclusions);
		var hideSecretsMode = applicationIntent?.HideSecrets ?? ResolveLegacyMode(selectionSpec.HideSecrets);
		var compressCodeMode = applicationIntent?.CompressCode ?? ResolveLegacyMode(selectionSpec.CompressCode);
		var stripCommentsMode = applicationIntent?.StripComments ?? ResolveLegacyMode(selectionSpec.StripComments);
		var stripBlankLinesMode = applicationIntent?.StripBlankLines ?? ResolveLegacyMode(selectionSpec.StripBlankLines);
		var applyGitMode = gitMode == ProjectSelectionApplicationMode.ApplyResolvedValue;
		var applyExclusions = exclusionMode == ProjectSelectionApplicationMode.ApplyResolvedValue;
		var applyHideSecrets = hideSecretsMode == ProjectSelectionApplicationMode.ApplyResolvedValue;
		var applyCompressCode = compressCodeMode == ProjectSelectionApplicationMode.ApplyResolvedValue;
		var applyStripComments = stripCommentsMode == ProjectSelectionApplicationMode.ApplyResolvedValue;
		var applyStripBlankLines = stripBlankLinesMode == ProjectSelectionApplicationMode.ApplyResolvedValue;
        var selectedExtensions = extensionMode != ProjectSelectionApplicationMode.ApplyResolvedValue ||
		                         selectionSpec.Extensions is null
            ? null
            : new HashSet<string>(
                selectionSpec.Extensions,
                StringComparer.OrdinalIgnoreCase);

        HashSet<IgnoreOptionId>? selectedIgnoreOptions = null;
		if (applyGitMode || applyExclusions)
        {
            var persistedIgnoreStates = selection.SnapshotIgnoreOptionStatesForPersistence();
            var inheritedIgnoreOptions = persistedIgnoreStates is null
                ? selection.GetSelectedIgnoreOptionIds()
                : persistedIgnoreStates
                    .Where(static state => state.Value)
                    .Select(static state => state.Key)
                    .ToArray();
            selectedIgnoreOptions = ResolveIgnoreSelectionOverride(
				selectionSpec with
				{
					GitMode = applyGitMode ? selectionSpec.GitMode : null,
					Exclusions = applyExclusions ? selectionSpec.Exclusions : null,
					HideSecrets = null,
					CompressCode = null,
					StripComments = null,
					StripBlankLines = null
				},
                inheritedIgnoreOptions);
        }

		var pathSelectionChanged = selection.ApplySelectionOverrides(
            currentPath,
            selectedExtensions,
            selectedIgnoreOptions,
            ignoreOptionStateIsComplete: applyExclusions,
			resetExtensionSelectionToDefaults:
				extensionMode == ProjectSelectionApplicationMode.ResetToDefaults);
		if (applyHideSecrets)
			selection.ApplyHideSecretsOverride(selectionSpec.HideSecrets);
		if (applyCompressCode)
			selection.ApplyCompressCodeOverride(selectionSpec.CompressCode);
		if (applyStripComments)
			selection.ApplyStripCommentsOverride(selectionSpec.StripComments);
		if (applyStripBlankLines)
			selection.ApplyStripBlankLinesOverride(selectionSpec.StripBlankLines);

		if (pathSelectionChanged)
		{
			await selection.WaitForPendingRefreshesAsync();
			await refreshTreeAsync();

			await selection.UpdateLiveOptionsForProjectScopeIfDirtyAsync(
				currentPath);
			await selection.WaitForPendingRefreshesAsync();
		}

        if (selectionSpec.SelectedPaths is { Count: > 0 })
            await ApplySelectedPathsAsync(selectionSpec.SelectedPaths);
    }

    internal static HashSet<IgnoreOptionId> ResolveIgnoreSelectionOverride(
        ProjectSelectionSpec selectionSpec,
        IReadOnlyCollection<IgnoreOptionId> inheritedIgnoreOptions)
    {
        ArgumentNullException.ThrowIfNull(selectionSpec);
        ArgumentNullException.ThrowIfNull(inheritedIgnoreOptions);

        // Null inherits one component; an empty exclusions collection explicitly disables
        // every exclusion without silently replacing the independently selected Git mode.
        var resolvedGitMode = selectionSpec.GitMode ??
                              GitFilteringModeResolver.Resolve(inheritedIgnoreOptions);
		var resolvedExclusions = selectionSpec.Exclusions ??
                                 ProjectSelectionAdapter.ToExclusions(inheritedIgnoreOptions);
		var resolvedHideSecrets = selectionSpec.HideSecrets ??
		                          inheritedIgnoreOptions.Contains(IgnoreOptionId.HideSecrets);
        return new HashSet<IgnoreOptionId>(
            ProjectSelectionAdapter.ToIgnoreOptions(
                selectionSpec with
                {
                    GitMode = resolvedGitMode,
					Exclusions = resolvedExclusions,
					HideSecrets = resolvedHideSecrets
                }));
    }

	private static ProjectSelectionApplicationMode ResolveLegacyMode<T>(T? value) =>
		value is null
			? ProjectSelectionApplicationMode.Preserve
			: ProjectSelectionApplicationMode.ApplyResolvedValue;

    public async Task ApplyUiOptionsAsync()
    {
        if (desktopRequest is null || !viewModel.IsProjectLoaded)
            return;

        if (desktopRequest.TreeFormat is { } treeFormat)
            viewModel.SelectedExportFormat = MapTreeFormat(treeFormat);

        if (desktopRequest.OpenPreview)
            viewModel.SelectedPreviewContentMode =
                MapPreviewMode(desktopRequest.PreviewView);

        if (!string.IsNullOrWhiteSpace(desktopRequest.Filter))
            await searchFilter.ApplyStartupFilterAsync(desktopRequest.Filter);

        var shouldOpenPreview =
            desktopRequest.OpenPreview ||
            !string.IsNullOrWhiteSpace(desktopRequest.Search);
        if (shouldOpenPreview && !viewModel.IsPreviewMode)
            await preview.OpenAsync();

        if (!string.IsNullOrWhiteSpace(desktopRequest.Search))
        {
            await searchFilter.ApplyStartupSearchAsync(
                desktopRequest.Search);
        }
    }

    public async Task<bool> RunBenchmarkScriptAsync()
    {
        if (diagnosticScenario is null ||
            !viewModel.IsProjectLoaded)
        {
            return false;
        }

        try
        {
            if (diagnosticScenario ==
                DesktopDiagnosticScenario.Standard)
            {
                await RunStandardBenchmarkScriptAsync();
            }
            else if (diagnosticScenario ==
                     DesktopDiagnosticScenario.PreviewSearchRetention)
            {
                await RunPreviewSearchRetentionBenchmarkScriptAsync();
            }
            else if (diagnosticScenario ==
                     DesktopDiagnosticScenario.ProjectMemoryLifecycle)
            {
                await RunProjectMemoryLifecycleBenchmarkScriptAsync();
            }
        }
        catch (Exception ex)
        {
            sessionMetrics.RecordUiBenchmarkStep(
                "scenario.failed",
                TimeSpan.Zero,
                success: false,
                ex.GetType().Name);
        }
        finally
        {
            await SettleBenchmarkStepAsync(
                TimeSpan.FromMilliseconds(250));
            closeWindow();
        }

        return true;
    }

    internal static ExportFormat MapTreeFormat(
        TreeTextFormat format)
        => format switch
        {
            TreeTextFormat.Json => ExportFormat.Json,
            TreeTextFormat.Xml => ExportFormat.Xml,
            TreeTextFormat.Markdown => ExportFormat.Markdown,
            _ => ExportFormat.Ascii
        };

    internal static PreviewContentMode MapPreviewMode(
        DesktopPreviewView mode)
        => mode switch
        {
            DesktopPreviewView.Content => PreviewContentMode.Content,
            DesktopPreviewView.TreeContent =>
                PreviewContentMode.TreeAndContent,
            _ => PreviewContentMode.Tree
        };

    private async Task ApplySelectedPathsAsync(
        IReadOnlyCollection<string> selectedPaths)
    {
        var rootPath = projectPathProvider();
        if (string.IsNullOrWhiteSpace(rootPath))
            return;

        var selectedFullPaths = selectedPaths
            .Select(path => ResolveSelectedPath(rootPath, path))
            .ToHashSet(PathComparer.Default);
        var nodes = new List<TreeNodeViewModel>();
        TreeNodeViewModel.ForEachDescendant(
            viewModel.TreeNodes,
            nodes.Add);

        var nodePaths = nodes
            .Select(static node => node.FullPath)
            .ToArray();
        var checkedStates = await Task.Run(
            () =>
            {
                var selectedDirectories = selectedFullPaths
                    .Where(Directory.Exists)
                    .ToArray();

                return nodePaths
                    .Select(nodePath =>
                        selectedFullPaths.Contains(nodePath) ||
                        selectedDirectories.Any(selectedDirectory =>
                            PathUtility.IsPathInside(
                                nodePath,
                                selectedDirectory)))
                    .ToArray();
            });

		ApplyCheckedStates(nodes, checkedStates, applyTreeSelectionBatch);
    }

	internal static void ApplyCheckedStates(
		IReadOnlyList<TreeNodeViewModel> nodes,
		IReadOnlyList<bool> checkedStates,
		Action<Action>? applyBatch)
	{
		ArgumentNullException.ThrowIfNull(nodes);
		ArgumentNullException.ThrowIfNull(checkedStates);
		if (nodes.Count != checkedStates.Count)
			throw new ArgumentException("Node and state counts must match.", nameof(checkedStates));

		void ApplyStates()
		{
			for (var index = 0; index < nodes.Count; index++)
				nodes[index].IsChecked = checkedStates[index];
		}

		if (applyBatch is null)
			ApplyStates();
		else
			applyBatch(ApplyStates);
	}

    private static string ResolveSelectedPath(string rootPath, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new ProjectContextValidationException(
                "DPX-SELECTION-PATH-INVALID",
                "Selected paths must be relative to the project root.");
        }

        var fullPath = Path.GetFullPath(relativePath, rootPath);
        if (!PathComparer.Default.Equals(fullPath, rootPath) &&
            !PathUtility.IsPathInside(fullPath, rootPath))
        {
            throw new ProjectContextValidationException(
                "DPX-SELECTION-PATH-INVALID",
                "Selected paths cannot leave the project root.");
        }
        return fullPath;
    }

    private async Task RunStandardBenchmarkScriptAsync()
    {
        await RunBenchmarkStepAsync("startup.settle", async () =>
        {
            await selection.WaitForPendingRefreshesAsync();
            await SettleBenchmarkStepAsync(TimeSpan.FromSeconds(1));
        });

        await RunBenchmarkStepAsync("preview.open", async () =>
        {
            await preview.OpenAsync();
            await WaitForConditionAsync(
                () => viewModel.IsPreviewMode &&
                      !viewModel.IsPreviewLoading &&
                      !workspace.IsPreviewPaneAnimating,
                TimeSpan.FromSeconds(30),
                "preview did not open");
            await SettleBenchmarkStepAsync(TimeSpan.FromSeconds(1));
        });

        await RunBenchmarkStepAsync(
            "tree-format.json",
            () => ApplyTreeFormatAsync(ExportFormat.Json));
        await RunBenchmarkStepAsync(
            "tree-format.xml",
            () => ApplyTreeFormatAsync(ExportFormat.Xml));
        await RunBenchmarkStepAsync(
            "tree-format.md",
            () => ApplyTreeFormatAsync(ExportFormat.Markdown));
        await RunBenchmarkStepAsync(
            "tree-format.ascii",
            () => ApplyTreeFormatAsync(ExportFormat.Ascii));

        await RunBenchmarkStepAsync(
            "preview-mode.content",
            () => ApplyPreviewModeAsync(
                PreviewContentMode.Content));
        await RunBenchmarkStepAsync(
            "preview-mode.tree-content",
            () => ApplyPreviewModeAsync(
                PreviewContentMode.TreeAndContent));
        await RunBenchmarkStepAsync(
            "preview-mode.tree",
            () => ApplyPreviewModeAsync(PreviewContentMode.Tree));

        var searchQuery = ResolveQuery(
            "service",
            "test",
            "src",
            "app");
        await RunBenchmarkStepAsync("search.apply", async () =>
        {
            await searchFilter.ApplyStartupSearchAsync(searchQuery);
            await WaitForConditionAsync(
                () => viewModel.SearchVisible &&
                      string.Equals(
                          viewModel.SearchQuery,
                          searchQuery,
                          StringComparison.Ordinal) &&
                      !viewModel.IsSearchInProgress,
                TimeSpan.FromSeconds(10),
                "search did not apply");
            await SettleBenchmarkStepAsync(
                TimeSpan.FromMilliseconds(500));
        });

        await RunBenchmarkStepAsync("search.navigate", async () =>
        {
            searchFilter.NavigateSearch(1);
            await SettleBenchmarkStepAsync(
                TimeSpan.FromMilliseconds(500));
        });

        var filterQuery = ResolveQuery(
            "test",
            "src",
            "service",
            "app");
        await RunBenchmarkStepAsync("filter.apply", async () =>
        {
            await searchFilter.ApplyStartupFilterAsync(filterQuery);
            await WaitForConditionAsync(
                () => viewModel.FilterVisible &&
                      string.Equals(
                          viewModel.NameFilter,
                          filterQuery,
                          StringComparison.Ordinal) &&
                      !viewModel.IsFilterInProgress,
                TimeSpan.FromSeconds(20),
                "filter did not apply");
            await SettleBenchmarkStepAsync(
                TimeSpan.FromMilliseconds(500));
        });

        await RunBenchmarkStepAsync("filter.close", async () =>
        {
            await searchFilter.CloseFilterAsync(focusTree: false);
            await WaitForConditionAsync(
                () => !viewModel.FilterVisible &&
                      string.IsNullOrEmpty(viewModel.NameFilter),
                TimeSpan.FromSeconds(10),
                "filter did not close");
            await SettleBenchmarkStepAsync(
                TimeSpan.FromMilliseconds(500));
        });

        await RunBenchmarkStepAsync("preview.close", async () =>
        {
            await preview.CloseAsync();
            await WaitForConditionAsync(
                () => !viewModel.IsPreviewMode &&
                      !workspace.IsPreviewPaneAnimating &&
                      !workspace.IsTreePaneAnimating,
                TimeSpan.FromSeconds(30),
                "preview did not close");
            await SettleBenchmarkStepAsync(
                TimeSpan.FromMilliseconds(500));
        });
        sessionMetrics.CaptureSample();

        await RunBenchmarkStepAsync(
            "idle.settle",
            () => SettleBenchmarkStepAsync(
                ResolveBenchmarkIdleSettleDuration()));
        sessionMetrics.CaptureSample();
    }

    private async Task RunPreviewSearchRetentionBenchmarkScriptAsync()
    {
        await RunBenchmarkStepAsync("startup.settle", async () =>
        {
            await selection.WaitForPendingRefreshesAsync();
            await SettleBenchmarkStepAsync(TimeSpan.FromSeconds(1));
        });

        await RunBenchmarkStepAsync("preview.open", async () =>
        {
            await preview.OpenAsync();
            await WaitForConditionAsync(
                () => viewModel.IsPreviewMode &&
                      !viewModel.IsPreviewLoading &&
                      !workspace.IsPreviewPaneAnimating,
                TimeSpan.FromSeconds(30),
                "preview did not open");
            await SettleBenchmarkStepAsync(TimeSpan.FromSeconds(1));
        });

        await RunPreviewSearchRetentionCycleAsync(
            stepSuffix: string.Empty,
            idleStepName: "search-cycle.idle-settle");
        await RunPreviewSearchRetentionCycleAsync(
            stepSuffix: ".repeat",
            idleStepName: "search-cycle.repeat.idle-settle");
    }

    private async Task RunProjectMemoryLifecycleBenchmarkScriptAsync()
    {
        var primaryPath = projectPathProvider();
        if (string.IsNullOrWhiteSpace(primaryPath))
            throw new InvalidOperationException("The primary benchmark project is unavailable.");

        var secondaryPath = Environment.GetEnvironmentVariable(
            BenchmarkSecondaryProjectEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(secondaryPath))
        {
            throw new InvalidOperationException(
                $"{BenchmarkSecondaryProjectEnvironmentVariable} must point to the secondary benchmark project.");
        }

        await RunBenchmarkStepAsync("startup.settle", async () =>
        {
            await selection.WaitForPendingRefreshesAsync();
            await SettleBenchmarkStepAsync(TimeSpan.FromSeconds(1));
        });
        sessionMetrics.CaptureSample();

        var reloadCount = ResolveBenchmarkProjectReloadCount();
        for (var iteration = 1; iteration <= reloadCount; iteration++)
        {
            await RunProjectLoadCycleAsync(
                primaryPath,
                $"project.reload.{iteration}");
        }

        await RunProjectLoadCycleAsync(
            secondaryPath,
            "project.switch.secondary");
    }

    private async Task RunProjectLoadCycleAsync(
        string path,
        string stepName)
    {
        await RunBenchmarkStepAsync(stepName, async () =>
        {
            if (!await openProjectAsync(path))
                throw new InvalidOperationException($"Project load failed for benchmark step {stepName}.");

            await selection.WaitForPendingRefreshesAsync();
            await WaitForConditionAsync(
                () => viewModel.IsProjectLoaded &&
                      !viewModel.StatusBusy &&
                      PathsEqual(projectPathProvider(), path),
                TimeSpan.FromMinutes(2),
                $"project load did not settle for {stepName}");
            await SettleBenchmarkStepAsync(TimeSpan.FromMilliseconds(500));
        });
        sessionMetrics.CaptureSample();

        await RunBenchmarkStepAsync(
            $"{stepName}.idle-settle",
            () => SettleBenchmarkStepAsync(
                ResolveBenchmarkIdleSettleDuration()));
        sessionMetrics.CaptureSample();
    }

    private async Task RunPreviewSearchRetentionCycleAsync(
        string stepSuffix,
        string idleStepName)
    {
        const string searchQuery = "app";
        await RunBenchmarkStepAsync($"search.apply{stepSuffix}", async () =>
        {
            await searchFilter.ApplyStartupSearchAsync(searchQuery);
            await WaitForConditionAsync(
                () => viewModel.SearchVisible &&
                      string.Equals(
                          viewModel.SearchQuery,
                          searchQuery,
                          StringComparison.Ordinal) &&
                      !viewModel.IsSearchInProgress,
                TimeSpan.FromSeconds(30),
                "search did not apply");
            await SettleBenchmarkStepAsync(TimeSpan.FromSeconds(2));
        });
        sessionMetrics.CaptureSample();

        await RunBenchmarkStepAsync($"search.close{stepSuffix}", async () =>
        {
            await searchFilter.CloseSearchAsync(focusTree: false);
            await WaitForConditionAsync(
                () => !viewModel.SearchVisible &&
                      string.IsNullOrEmpty(viewModel.SearchQuery) &&
                      !viewModel.IsSearchInProgress,
                TimeSpan.FromSeconds(15),
                "search did not close");
            await SettleBenchmarkStepAsync(TimeSpan.FromMilliseconds(500));
        });
        sessionMetrics.CaptureSample();

        await RunBenchmarkStepAsync(
            idleStepName,
            () => SettleBenchmarkStepAsync(
                ResolveBenchmarkIdleSettleDuration()));
        sessionMetrics.CaptureSample();
    }

    private async Task RunBenchmarkStepAsync(
        string stepName,
        Func<Task> action)
    {
        using var responsivenessCts = new CancellationTokenSource();
        var responsivenessTask = Task.Run(
            () => MeasureMaximumUiDispatchLatencyAsync(
                responsivenessCts.Token));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await action();
            stopwatch.Stop();
            var maximumUiDispatchLatency =
                await StopUiDispatchProbeAsync(
                    responsivenessCts,
                    responsivenessTask);
            sessionMetrics.RecordUiBenchmarkStep(
                stepName,
                stopwatch.Elapsed,
                success: true,
                maximumUiDispatchLatency:
                    maximumUiDispatchLatency);
        }
        catch
        {
            stopwatch.Stop();
            var maximumUiDispatchLatency =
                await StopUiDispatchProbeAsync(
                    responsivenessCts,
                    responsivenessTask);
            sessionMetrics.RecordUiBenchmarkStep(
                stepName,
                stopwatch.Elapsed,
                success: false,
                maximumUiDispatchLatency:
                    maximumUiDispatchLatency);
            throw;
        }
    }

    private static async Task<TimeSpan> MeasureMaximumUiDispatchLatencyAsync(
        CancellationToken cancellationToken)
    {
        var maximumLatency = TimeSpan.Zero;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(
                    UiDispatchProbeInterval,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var startedAt = Stopwatch.GetTimestamp();
            await Dispatcher.UIThread.InvokeAsync(
                static () => { },
                DispatcherPriority.Input);
            var latency = Stopwatch.GetElapsedTime(startedAt);
            if (latency > maximumLatency)
                maximumLatency = latency;
        }

        return maximumLatency;
    }

    private static async Task<TimeSpan> StopUiDispatchProbeAsync(
        CancellationTokenSource cancellationSource,
        Task<TimeSpan> responsivenessTask)
    {
        cancellationSource.Cancel();
        return await responsivenessTask;
    }

    private async Task ApplyTreeFormatAsync(ExportFormat format)
    {
        viewModel.SelectedExportFormat = format;
        await WaitForConditionAsync(
            () => viewModel.SelectedExportFormat == format &&
                  !viewModel.IsPreviewLoading,
            TimeSpan.FromSeconds(20),
            $"tree format {format} did not settle");
        await SettleBenchmarkStepAsync(
            TimeSpan.FromMilliseconds(500));
    }

    private async Task ApplyPreviewModeAsync(
        PreviewContentMode mode)
    {
        await preview.SwitchModeAsync(mode);
        await WaitForConditionAsync(
            () => viewModel.SelectedPreviewContentMode == mode &&
                  !viewModel.IsPreviewLoading &&
                  !preview.IsModeSwitchInProgress,
            TimeSpan.FromSeconds(20),
            $"preview mode {mode} did not settle");
        await SettleBenchmarkStepAsync(
            TimeSpan.FromMilliseconds(500));
    }

    private string ResolveQuery(params string[] candidates)
    {
        var root = treeRootProvider();
        if (root is null)
            return candidates[0];

        foreach (var candidate in candidates)
        {
            if (NameFilterMatchCounter.CountMatchesUnderRoot(
                    root,
                    candidate) > 0)
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    private static async Task WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string timeoutMessage)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
                return;

            await DispatcherTaskSchedulerProvider.YieldAsync(
                DispatcherPriority.Background);
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException(timeoutMessage);
    }

    private static async Task SettleBenchmarkStepAsync(
        TimeSpan minimumDelay)
    {
        await DispatcherTaskSchedulerProvider.YieldAsync(
            DispatcherPriority.Render);
        await DispatcherTaskSchedulerProvider.YieldAsync(
            DispatcherPriority.Render);
        await Task.Delay(minimumDelay);
        await DispatcherTaskSchedulerProvider.YieldAsync(
            DispatcherPriority.Background);
    }

    internal static TimeSpan ResolveBenchmarkIdleSettleDuration()
    {
        var configured = Environment.GetEnvironmentVariable(
            BenchmarkIdleSecondsEnvironmentVariable);
        return int.TryParse(
            configured,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var seconds)
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 60))
            : DefaultBenchmarkIdleSettleDuration;
    }

    internal static int ResolveBenchmarkProjectReloadCount()
    {
        var configured = Environment.GetEnvironmentVariable(
            BenchmarkProjectReloadCountEnvironmentVariable);
        return int.TryParse(
            configured,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var reloadCount)
            ? Math.Clamp(reloadCount, 1, 8)
            : 3;
    }

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
            return false;

        try
        {
            return PathComparer.Default.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right));
        }
        catch
        {
            return false;
        }
    }
}
