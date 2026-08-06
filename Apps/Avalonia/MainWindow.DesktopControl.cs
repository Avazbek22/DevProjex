using DevProjex.Application.Context;
using DevProjex.Application.DesktopControl;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Terminal.DesktopControl;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
    private async Task EnsureDesktopControlServerAsync(CancellationToken cancellationToken)
    {
        if (_desktopControlServer is not null)
            return;

        _desktopControlServer = await DesktopControlServer.StartAsync(
            new AvaloniaDesktopInteractionHandler(this),
            _currentPath,
            cancellationToken: cancellationToken);
    }

    private async Task<DesktopInteractionResult> HandleDesktopInteractionAsync(
        DesktopInteractionRequest request,
        CancellationToken cancellationToken)
    {
        if (_windowLifetimeCts is null || _windowLifetimeCts.IsCancellationRequested)
            return Failure("DPX-DESKTOP-SHUTTING-DOWN");

        if (request is DesktopStatusRequest)
            return SuccessState();

        await _desktopInteractionGate.WaitAsync(cancellationToken);
        try
        {
            return request switch
            {
                DesktopActivateRequest => ActivateDesktop(),
                DesktopOpenProjectRequest open => await ApplyDesktopOpenRequestAsync(
                    open.Request,
                    cancellationToken),
                DesktopPreviewRequest preview => await ApplyDesktopPreviewRequestAsync(preview),
                DesktopPreviewViewRequest previewView => await ApplyDesktopPreviewViewAsync(previewView.View),
                DesktopTreeFormatRequest treeFormat => ApplyDesktopTreeFormat(treeFormat.Format),
                DesktopFilterRequest filter => await ApplyDesktopFilterAsync(filter.Query),
                DesktopSearchRequest search => await ApplyDesktopSearchAsync(search),
                _ => Failure("DPX-DESKTOP-UNKNOWN-ACTION")
            };
        }
        finally
        {
            _desktopInteractionGate.Release();
        }
    }

    private async Task<DesktopInteractionResult> ApplyDesktopOpenRequestAsync(
        DesktopOpenRequest request,
        CancellationToken cancellationToken)
    {
        if (_awaitingSystemDialogActivation || _gitCloneWindow is not null)
            return Failure("DPX-DESKTOP-MODAL-BUSY");
        if (_projectCopyExportCts is not null)
            return Failure("DPX-DESKTOP-BUSY");

        await WaitForProjectSwitchAvailabilityAsync(cancellationToken);
        var projectPath = request.ProjectPath;
        if (request.UseLastProject)
        {
            await EnsureRecentProjectsLoadedAsync(cancellationToken);
            projectPath = await FindFirstExistingDirectoryAsync(
                _recentProjectsDb.RecentFolders.Select(static folder => folder.Path),
                cancellationToken);
            if (projectPath is null)
                return Failure("DPX-DESKTOP-NO-RECENT-PROJECT");
        }

        if (!string.IsNullOrWhiteSpace(projectPath) &&
            (!PathComparer.Default.Equals(PathUtility.Normalize(projectPath), _currentPath) ||
             !_viewModel.IsProjectLoaded))
        {
            if (!await TryOpenFolderAsync(projectPath, fromDialog: false))
                return Failure("DPX-DESKTOP-PROJECT-OPEN-FAILED");
        }

        if (request.Language is { } language)
            SetLanguageForCurrentSession(language);

        var controller = CreateStartupInteractionController(
            request,
            diagnosticScenario: null);
        await controller.ApplySelectionOverridesAsync();
        var gitReadinessDiagnostic = GetDesktopGitReadinessDiagnostic(request);
        if (gitReadinessDiagnostic is { Severity: ContextDiagnosticSeverity.Error })
        {
            return Failure(gitReadinessDiagnostic.Code);
        }
        if (gitReadinessDiagnostic is { Severity: ContextDiagnosticSeverity.Warning })
            _toastService.Show(_localization["Terminal.Diagnostic.TrackedIndexPartial"]);
        await controller.ApplyUiOptionsAsync();
        ActivateDesktop();
        if (_desktopControlServer is not null)
            await _desktopControlServer.UpdateProjectAsync(_currentPath, cancellationToken);
        return SuccessState();
    }

    private async Task WaitForProjectSwitchAvailabilityAsync(CancellationToken cancellationToken)
    {
        while (!_viewModel.CanChangeProjectTree &&
               _projectCopyExportCts is null &&
               _windowLifetimeCts is { IsCancellationRequested: false })
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(50, cancellationToken);
        }
    }

    private async Task<DesktopInteractionResult> ApplyDesktopPreviewRequestAsync(
        DesktopPreviewRequest request)
    {
        if (!request.IsOpen)
        {
            if (_viewModel.IsPreviewMode)
                await _previewWorkspaceController.CloseAsync();
            return SuccessState();
        }

        if (request.View is { } view)
            await ApplyPreviewModeAsync(view);
        if (!_viewModel.IsPreviewMode)
            await _previewWorkspaceController.OpenAsync();
        return SuccessState();
    }

    private async Task<DesktopInteractionResult> ApplyDesktopPreviewViewAsync(
        DesktopPreviewView view)
    {
        await ApplyPreviewModeAsync(view);
        return SuccessState();
    }

    private async Task ApplyPreviewModeAsync(DesktopPreviewView view)
    {
        var mode = StartupInteractionController.MapPreviewMode(view);
        if (_viewModel.IsPreviewMode)
            await _previewWorkspaceController.SwitchModeAsync(mode);
        else
            _viewModel.SelectedPreviewContentMode = mode;
    }

    private DesktopInteractionResult ApplyDesktopTreeFormat(TreeTextFormat format)
    {
        _viewModel.SelectedExportFormat = StartupInteractionController.MapTreeFormat(format);
        return SuccessState();
    }

    private async Task<DesktopInteractionResult> ApplyDesktopFilterAsync(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            await _searchFilterController.CloseFilterAsync(focusTree: false);
        else
            await _searchFilterController.ApplyStartupFilterAsync(query);
        return SuccessState();
    }

    private async Task<DesktopInteractionResult> ApplyDesktopSearchAsync(
        DesktopSearchRequest request)
    {
        switch (request.Operation)
        {
            case DesktopSearchOperation.Set:
                if (!_viewModel.IsPreviewMode)
                    await _previewWorkspaceController.OpenAsync();
                await _searchFilterController.ApplyStartupSearchAsync(request.Query ?? string.Empty);
                break;
            case DesktopSearchOperation.Next:
                _searchFilterController.NavigateSearch(1);
                break;
            case DesktopSearchOperation.Previous:
                _searchFilterController.NavigateSearch(-1);
                break;
            case DesktopSearchOperation.Clear:
                await _searchFilterController.CloseSearchAsync(focusTree: false);
                break;
        }

        return SuccessState();
    }

    private DesktopInteractionResult ActivateDesktop()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Show();
        Activate();
        return SuccessState();
    }

    private DesktopInteractionResult SuccessState() =>
        new(
            true,
            State: new Dictionary<string, object?>
            {
                ["projectPath"] = _currentPath,
                ["projectLoaded"] = _viewModel.IsProjectLoaded,
                ["busy"] = _viewModel.StatusBusy,
                ["startupReady"] = _desktopStartupReady,
                ["startupError"] = _desktopStartupErrorCode,
                ["previewOpen"] = _viewModel.IsPreviewMode,
                ["previewView"] = _viewModel.SelectedPreviewContentMode switch
                {
                    PreviewContentMode.Tree => "tree",
                    PreviewContentMode.Content => "content",
                    _ => "tree-content"
                },
                ["treeFormat"] = GetCurrentTreeTextFormat() switch
                {
                    TreeTextFormat.Markdown => "markdown",
                    TreeTextFormat.Json => "json",
                    TreeTextFormat.Xml => "xml",
                    _ => "text"
                },
                ["filter"] = _viewModel.NameFilter,
                ["search"] = _viewModel.SearchQuery,
                ["gitMode"] = _selectionCoordinator.AppliedGitReadiness.Mode switch
                {
                    GitFilteringMode.RespectGitIgnore => "gitignore",
                    GitFilteringMode.TrackedFilesOnly => "tracked",
                    _ => "none"
                },
                ["trackedGitReady"] = _selectionCoordinator.AppliedGitReadiness.IsReady
            });

    private ContextDiagnostic? GetDesktopGitReadinessDiagnostic(DesktopOpenRequest request)
    {
        if (request.Selection?.GitMode != GitFilteringMode.TrackedFilesOnly)
            return null;

        var projectPath = _currentPath ?? request.ProjectPath;
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return ProjectContextGitReadiness
                .Evaluate(GitFilteringMode.TrackedFilesOnly, 0, 0)
                .CreateDiagnostic(string.Empty);
        }

        return _selectionCoordinator.GetAppliedGitReadinessDiagnostic(
            projectPath,
            GitFilteringMode.TrackedFilesOnly);
    }

    private static DesktopInteractionResult Failure(string code) =>
        new(false, code);

    private sealed class AvaloniaDesktopInteractionHandler(MainWindow window)
        : IDesktopInteractionHandler
    {
        public async Task<DesktopInteractionResult> HandleAsync(
            DesktopInteractionRequest request,
            CancellationToken cancellationToken)
        {
            if (Dispatcher.UIThread.CheckAccess())
                return await window.HandleDesktopInteractionAsync(request, cancellationToken);

            return await Dispatcher.UIThread.InvokeAsync(
                async () => await window.HandleDesktopInteractionAsync(request, cancellationToken));
        }
    }
}
