using DevProjex.Avalonia.Services;
using DevProjex.Kernel;

namespace DevProjex.Avalonia.Coordinators;

internal sealed class StartupInteractionController(
    CommandLineOptions options,
    MainWindowViewModel viewModel,
    SelectionSyncCoordinator selection,
    SearchFilterInteractionController searchFilter,
    PreviewWorkspaceController preview,
    WorkspacePresentationController workspace,
    SessionMetricsRecorder sessionMetrics,
    Func<string?> projectPathProvider,
    Func<TreeNodeDescriptor?> treeRootProvider,
    Func<Task> refreshTreeAsync,
    Action closeWindow)
{
    public async Task ApplySelectionOverridesAsync()
    {
        if (!options.HasSelectionOverrides ||
            !viewModel.IsProjectLoaded ||
            string.IsNullOrWhiteSpace(projectPathProvider()))
        {
            return;
        }

        if (options.HasRootFolderOverrides)
        {
            var selectedRoots = new HashSet<string>(
                options.IncludeRootFolders,
                PathComparer.Default);
            foreach (var option in viewModel.RootFolders)
                option.IsChecked = selectedRoots.Contains(option.Name);
        }

        if (options.HasExtensionOverrides)
        {
            var selectedExtensions = new HashSet<string>(
                options.IncludeExtensions,
                StringComparer.OrdinalIgnoreCase);
            foreach (var option in viewModel.Extensions)
            {
                option.IsChecked =
                    selectedExtensions.Contains(option.Name);
            }
        }

        if (options.HasIgnoreOverrides)
        {
            var selectedIgnoreOptions = new HashSet<IgnoreOptionId>(
                options.IgnoreOptions);
            foreach (var option in viewModel.IgnoreOptions)
            {
                option.IsChecked =
                    selectedIgnoreOptions.Contains(option.Id);
            }
        }

        await selection.WaitForPendingRefreshesAsync();
        await refreshTreeAsync();

        var currentPath = projectPathProvider();
        await selection.UpdateLiveOptionsFromRootSelectionIfDirtyAsync(
            currentPath);
        await selection.WaitForPendingRefreshesAsync();
    }

    public async Task ApplyUiOptionsAsync()
    {
        var ui = options.Ui;
        if (!ui.HasStartupActions || !viewModel.IsProjectLoaded)
            return;

        if (ui.TreeFormat is { } treeFormat)
        {
            viewModel.SelectedExportFormat =
                MapTreeFormat(treeFormat);
        }

        if (ui.PreviewMode is { } previewMode)
        {
            viewModel.SelectedPreviewContentMode =
                MapPreviewMode(previewMode);
        }

        if (!string.IsNullOrWhiteSpace(ui.TreeFilter))
            await searchFilter.ApplyStartupFilterAsync(ui.TreeFilter);

        var shouldOpenPreview =
            ui.OpenPreview ||
            ui.PreviewMode is not null ||
            !string.IsNullOrWhiteSpace(ui.PreviewSearch);
        if (shouldOpenPreview && !viewModel.IsPreviewMode)
            await preview.OpenAsync();

        if (!string.IsNullOrWhiteSpace(ui.PreviewSearch))
        {
            await searchFilter.ApplyStartupSearchAsync(
                ui.PreviewSearch);
        }
    }

    public async Task<bool> RunBenchmarkScriptAsync()
    {
        if (!options.UiBenchmarkScript.Enabled ||
            !viewModel.IsProjectLoaded)
        {
            return false;
        }

        try
        {
            if (options.UiBenchmarkScript.Script ==
                StartupUiBenchmarkScript.Standard)
            {
                await RunStandardBenchmarkScriptAsync();
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
        StartupPreviewMode mode)
        => mode switch
        {
            StartupPreviewMode.Content => PreviewContentMode.Content,
            StartupPreviewMode.TreeContent =>
                PreviewContentMode.TreeAndContent,
            _ => PreviewContentMode.Tree
        };

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

        await RunBenchmarkStepAsync(
            "idle.settle",
            () => SettleBenchmarkStepAsync(
                TimeSpan.FromMilliseconds(1500)));
    }

    private async Task RunBenchmarkStepAsync(
        string stepName,
        Func<Task> action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await action();
            stopwatch.Stop();
            sessionMetrics.RecordUiBenchmarkStep(
                stepName,
                stopwatch.Elapsed,
                success: true);
        }
        catch
        {
            stopwatch.Stop();
            sessionMetrics.RecordUiBenchmarkStep(
                stepName,
                stopwatch.Elapsed,
                success: false);
            throw;
        }
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
}
