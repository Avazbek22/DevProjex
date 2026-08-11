using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.Services;
using DevProjex.Avalonia.Views;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
    #region Git Operations

    private async void OnGitClone(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanChangeProjectTree)
            return;

        try
        {
            await EnsureRecentProjectsLoadedAsync(
                _windowLifetimeCts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _viewModel.GitCloneUrl = string.Empty;
        _viewModel.GitCloneStatus = string.Empty;
        _viewModel.GitCloneInProgress = false;

        // Create and show Git Clone window
        _gitCloneWindow = new GitCloneWindow
        {
            DataContext = _viewModel
        };

        _gitCloneWindow.StartCloneRequested += OnGitCloneStart;
        _gitCloneWindow.CancelRequested += OnGitCloneCancel;

        _ = _gitCloneWindow.ShowDialog(this);
        e.Handled = true;
    }

    private void OnGitCloneClose(object? sender, RoutedEventArgs e)
    {
        CancelGitCloneOperation();
        _gitCloneWindow?.Close();
        _gitCloneWindow = null;
        e.Handled = true;
    }

    private async void OnGitCloneStart(object? sender, RoutedEventArgs e)
    {
        var url = _viewModel.GitCloneUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            await ShowErrorAsync(_viewModel.GitErrorInvalidUrl);
            return;
        }

        // Validate URL format before attempting to clone
        if (!IsValidGitRepositoryUrl(url))
        {
            await ShowErrorAsync(_viewModel.GitErrorInvalidUrl);
            return;
        }

        var gitCloneCts = ReplaceCancellationSource(ref _gitCloneCts);
        var cancellationToken = gitCloneCts.Token;

        _viewModel.GitCloneInProgress = true;
        _viewModel.GitCloneStatus = _viewModel.GitCloneProgressCheckingGit;
        _taskbarProgress.BeginGitClone();

        string? stagingPath = null;

        try
        {
            // Track current operation for progress reporting
            string currentOperation = string.Empty;

            var progress = new Progress<string>(status =>
            {
                Dispatcher.Post(() =>
                {
                    _taskbarProgress.UpdateGitClone(status);

                    // Handle phase transition markers
                    if (status == "::EXTRACTING::")
                    {
                        currentOperation = _viewModel.GitCloneProgressExtracting;
                        _viewModel.GitCloneStatus = currentOperation;
                        return;
                    }

                    // Keep localized phase labels and append numeric progress only.
                    // Raw git stderr lines (e.g. "Cloning into ...") are not shown in UI.
                    if (status.EndsWith('%') && status.Length <= 4 && !string.IsNullOrEmpty(currentOperation))
                    {
                        _viewModel.GitCloneStatus = $"{currentOperation} {status}";
                    }
                    else if (!string.IsNullOrEmpty(currentOperation))
                    {
                        _viewModel.GitCloneStatus = currentOperation;
                    }
                });
            });

            GitCloneResult result;
            IRepositoryCacheSession? cacheSession;
            await using (await _repoCacheService.AcquireRepositoryOperationAsync(url, cancellationToken))
            {
                cacheSession = await _repoCacheService.TryAcquireRepositorySessionAsync(
                    url,
                    cancellationToken: cancellationToken);
                if (cacheSession is not null)
                {
                    result = new GitCloneResult(
                        Success: true,
                        LocalPath: cacheSession.RepositoryPath,
                        SourceType: cacheSession.ContentKind == RepositoryCacheContentKind.Git
                            ? ProjectSourceType.GitClone
                            : ProjectSourceType.ZipDownload,
                        DefaultBranch: cacheSession.Branch,
                        RepositoryName: RepositoryUrlUtility.GetRepositoryName(cacheSession.RepositoryUrl),
                        RepositoryUrl: cacheSession.RepositoryUrl,
                        ErrorMessage: null);
                }
                else
                {
                    var hasInternet = await CheckInternetConnectionAsync(cancellationToken);
                    if (!hasInternet)
                    {
                        _viewModel.GitCloneInProgress = false;
                        _gitCloneWindow?.Close();
                        _gitCloneWindow = null;
                        _taskbarProgress.MarkGitCloneError();
                        await ShowErrorAsync(_viewModel.GitErrorNoInternetConnection);
                        return;
                    }

                    stagingPath = _repoCacheService.CreateRepositoryStagingDirectory(url);
                    var gitAvailable = await _gitService.IsGitAvailableAsync(cancellationToken);
                    if (gitAvailable)
                    {
                        currentOperation = _viewModel.GitCloneProgressCloning;
                        _viewModel.GitCloneStatus = currentOperation;
                        _taskbarProgress.SetGitCloneIndeterminate();
                        result = await _gitService.CloneAsync(url, stagingPath, progress, cancellationToken);
                    }
                    else
                    {
                        _viewModel.GitCloneStatus = _viewModel.GitErrorGitNotFound;
                        await Task.Delay(1500, cancellationToken);
                        currentOperation = _viewModel.GitCloneProgressDownloading;
                        _viewModel.GitCloneStatus = currentOperation;
                        _taskbarProgress.SetGitCloneIndeterminate();
                        result = await _zipDownloadService.DownloadAndExtractAsync(
                            url,
                            stagingPath,
                            progress,
                            cancellationToken);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (!result.Success)
                    {
                        await DeleteRepositoryDirectoryAsync(stagingPath, CancellationToken.None);
                        _gitCloneWindow?.Close();
                        _gitCloneWindow = null;
                        _viewModel.GitCloneInProgress = false;
                        _taskbarProgress.MarkGitCloneError();
                        await ShowErrorAsync(_localization.Format(
                            "Git.Error.CloneFailed",
                            result.ErrorMessage ?? "Unknown error"));
                        _toastService.Show(_localization["Toast.Git.CloneError"]);
                        return;
                    }

                    var repositoryUrl = string.IsNullOrWhiteSpace(result.RepositoryUrl)
                        ? url
                        : result.RepositoryUrl;
                    var publishedPath = _repoCacheService.PublishRepositoryDirectory(
                        stagingPath,
                        repositoryUrl);
                    stagingPath = null;
                    _repoCacheService.RecordIndexedRepository(
                        repositoryUrl,
                        publishedPath,
                        result.DefaultBranch);
                    cacheSession = await _repoCacheService.TryAcquireRepositorySessionAsync(
                        repositoryUrl,
                        result.DefaultBranch,
                        cancellationToken);
                    if (cacheSession is null)
                        throw new IOException("Published repository session could not be acquired.");
                    result = result with { LocalPath = cacheSession.RepositoryPath };
                }
            }

            try
            {
                await ApplySuccessfulGitCloneAsync(
                    result,
                    cacheSession.RepositoryPath,
                    url,
                    cancellationToken,
                    cacheSession);
            }
            finally
            {
                if (!ReferenceEquals(_currentRepositorySession, cacheSession))
                    cacheSession.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            if (stagingPath is not null)
            {
                await DeleteRepositoryDirectoryAsync(stagingPath, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            if (stagingPath is not null)
            {
                await DeleteRepositoryDirectoryAsync(stagingPath, CancellationToken.None);
            }

            _gitCloneWindow?.Close();
            _gitCloneWindow = null;
            _taskbarProgress.MarkGitCloneError();
            await ShowErrorAsync(_localization.Format("Git.Error.CloneFailed", ex.Message));
            _toastService.Show(_localization["Toast.Git.CloneError"]);
        }
        finally
        {
            _viewModel.GitCloneInProgress = false;
            _taskbarProgress.CompleteGitClone();
            DisposeIfCurrent(ref _gitCloneCts, gitCloneCts);
        }

        e.Handled = true;
    }

    private Task DeleteRepositoryDirectoryAsync(
        string path,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => _repoCacheService.DeleteRepositoryDirectory(path),
            cancellationToken);

    internal async Task ApplySuccessfulGitCloneAsync(
        GitCloneResult result,
        string cachePath,
        string requestedUrl,
        CancellationToken cancellationToken,
        IRepositoryCacheSession? preparedSession = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();

        _gitCloneWindow?.Close();
        _gitCloneWindow = null;
        _viewModel.GitCloneInProgress = false;

        var opened = await TryOpenFolderAsync(
            result.LocalPath,
            fromDialog: false,
            recordRecentFolder: false,
            preparedSession);
        if (!opened || !PathComparer.Default.Equals(_currentPath, result.LocalPath))
            return;

        _viewModel.ProjectSourceType = result.SourceType;
        _viewModel.CurrentBranch = result.DefaultBranch ?? string.Empty;
        _currentProjectDisplayName = result.RepositoryName;
        _currentRepositoryUrl = string.IsNullOrWhiteSpace(result.RepositoryUrl)
            ? requestedUrl
            : result.RepositoryUrl;
        _currentCachedRepoPath = cachePath;
        await RecordCachedRepositoryAsync(
            cachePath,
            _currentRepositoryUrl,
            result.DefaultBranch,
            commitHash: null,
            cancellationToken);
        UpdateTitle();

        await RecordRecentRepositoryAsync(
            string.IsNullOrWhiteSpace(result.RepositoryUrl) ? requestedUrl : result.RepositoryUrl,
            cancellationToken);

        // Clone-only branch discovery stays behind the shared post-reveal stability gate. Waiting
        // only for transition completion can still compete with the island's final layout pass.
        if (result.SourceType == ProjectSourceType.GitClone)
        {
            var visualReadyTask = _postLoadVisualReadyTask;
            await MetricsCalculationPolicy.WaitForInitialVisualReadyAsync(
                visualReadyTask,
                MetricsCalculationPolicy.InitialVisualReadyTimeout,
                cancellationToken);

            if (PathComparer.Default.Equals(_currentPath, result.LocalPath))
                await RefreshGitBranchesAsync(result.LocalPath, cancellationToken);
        }

        if (PathComparer.Default.Equals(_currentPath, result.LocalPath))
            _toastService.Show(_localization["Toast.Git.CloneSuccess"]);
    }

    private void OnGitCloneCancel(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.GitCloneInProgress)
        {
            CancelGitCloneOperation();
        }
        else
        {
            _gitCloneWindow?.Close();
            _gitCloneWindow = null;
        }
        e.Handled = true;
    }

    private void CancelGitCloneOperation()
    {
        _gitCloneCts?.Cancel();
        _viewModel.GitCloneInProgress = false;
        _taskbarProgress.CompleteGitClone();
    }

    private async void OnGitGetUpdates(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanGetGitUpdates)
            return;

        await GetGitUpdatesAsync();
        e.Handled = true;
    }

    private async Task GetGitUpdatesAsync()
    {
        if (!_viewModel.IsGitMode || string.IsNullOrEmpty(_currentPath))
            return;

        var gitCts = ReplaceCancellationSource(ref _gitOperationCts);
        var cancellationToken = gitCts.Token;
        long? statusOperationId = null;
        try
        {
            var statusText = string.IsNullOrWhiteSpace(_viewModel.CurrentBranch)
                ? _viewModel.StatusOperationGettingUpdates
                : _localization.Format("Status.Operation.GettingUpdatesBranch", _viewModel.CurrentBranch);
            statusOperationId = _statusOperations.Begin(
                statusText,
                indeterminate: true,
                operationType: StatusOperationType.GitPullUpdates,
                cancelAction: () => gitCts.Cancel());

            var progress = new Progress<string>(status =>
            {
                Dispatcher.Post(() =>
                {
                    if (GitProgressStatusParser.TryParseTrailingPercent(status, out var percent))
                        _statusOperations.UpdateProgress(percent, statusText, statusOperationId);
                    else
                        _statusOperations.UpdateText(statusText, statusOperationId);
                });
            });
            var beforeHash = await _gitService.GetHeadCommitAsync(_currentPath, cancellationToken);
            var success = await _gitService.PullUpdatesAsync(_currentPath, progress, cancellationToken);

            if (!success)
            {
                _statusOperations.Complete(statusOperationId);
                await ShowErrorAsync(_localization.Format("Git.Error.UpdateFailed", "Pull failed"));
                return;
            }

            // Refresh branches and tree
            await RefreshGitBranchesAsync(_currentPath, cancellationToken);
            await ReloadProjectAsync(cancellationToken);

            var afterHash = await _gitService.GetHeadCommitAsync(_currentPath, cancellationToken);
            await RecordCachedRepositoryAsync(
                _currentPath,
                _currentRepositoryUrl,
                _viewModel.CurrentBranch,
                afterHash,
                cancellationToken);
            await Task.Run(
                () => _repoCacheService.RefreshIndexedRepositorySize(_currentPath),
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(beforeHash) && !string.IsNullOrWhiteSpace(afterHash) && beforeHash == afterHash)
            {
                _toastService.Show(_localization["Toast.Git.NoUpdates"]);
                _statusOperations.Complete(statusOperationId);
            }
            else
            {
                _toastService.Show(_localization["Toast.Git.UpdatesApplied"]);
                _statusOperations.Complete(statusOperationId);
                // Clean up memory from old tree after successful update.
                ScheduleBackgroundMemoryCleanup(MemoryCleanupReason.GitPullUpdate);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _statusOperations.Complete(statusOperationId);
            _toastService.Show(_localization["Toast.Operation.GitCanceled"]);
        }
        catch (Exception ex)
        {
            _statusOperations.Complete(statusOperationId);
            await ShowErrorAsync(_localization.Format("Git.Error.UpdateFailed", ex.Message));
        }
        finally
        {
            DisposeIfCurrent(ref _gitOperationCts, gitCts);
        }
    }

    private async void OnGitBranchSwitch(object? sender, string branchName)
    {
        if (!_viewModel.CanGetGitUpdates || string.IsNullOrEmpty(_currentPath))
            return;

        var gitCts = ReplaceCancellationSource(ref _gitOperationCts);
        var cancellationToken = gitCts.Token;
        long? statusOperationId = null;
        try
        {
            var statusText = _localization.Format("Status.Operation.SwitchingBranch", branchName);
            statusOperationId = _statusOperations.Begin(
                statusText,
                indeterminate: true,
                operationType: StatusOperationType.GitSwitchBranch,
                cancelAction: () => gitCts.Cancel());

            var progress = new Progress<string>(status =>
            {
                Dispatcher.Post(() =>
                {
                    if (GitProgressStatusParser.TryParseTrailingPercent(status, out var percent))
                        _statusOperations.UpdateProgress(percent, statusText, statusOperationId);
                    else
                        _statusOperations.UpdateText(statusText, statusOperationId);
                });
            });
            var success = await _gitService.SwitchBranchAsync(_currentPath, branchName, progress, cancellationToken);

            // A lightweight retry helps recover from transient remote/network hiccups.
            if (!success)
                success = await _gitService.SwitchBranchAsync(_currentPath, branchName, progress: null, cancellationToken);

            if (!success)
            {
                _statusOperations.Complete(statusOperationId);
                await ShowErrorAsync(_localization.Format("Git.Error.BranchSwitchFailed", branchName));
                return;
            }

            // Reload tree first so branch/title state is only updated after full success.
            // This keeps UI stable if reload fails or gets cancelled mid-flight.
            await ReloadProjectAsync(cancellationToken);
            await RefreshGitBranchesAsync(_currentPath, cancellationToken);
            _statusOperations.Complete(statusOperationId);

            _viewModel.CurrentBranch = branchName;
            var commitHash = await _gitService.GetHeadCommitAsync(_currentPath, cancellationToken);
            await RecordCachedRepositoryAsync(
                _currentPath,
                _currentRepositoryUrl,
                branchName,
                commitHash,
                cancellationToken);
            UpdateTitle();
            _toastService.Show(_localization.Format("Toast.Git.BranchSwitched", branchName));

            // Clean up memory from old branch tree.
            ScheduleBackgroundMemoryCleanup(MemoryCleanupReason.GitBranchSwitch);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _statusOperations.Complete(statusOperationId);
            _toastService.Show(_localization["Toast.Operation.GitCanceled"]);
        }
        catch (Exception ex)
        {
            _statusOperations.Complete(statusOperationId);
            await ShowErrorAsync(_localization.Format("Git.Error.BranchSwitchFailed", ex.Message));
        }
        finally
        {
            DisposeIfCurrent(ref _gitOperationCts, gitCts);
        }
    }

    private async Task RefreshGitBranchesAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var branches = await _gitService.GetBranchesAsync(repositoryPath, cancellationToken);

            _viewModel.GitBranches.Clear();
            foreach (var branch in branches)
                _viewModel.GitBranches.Add(branch);

            // Update branch menu
            UpdateBranchMenu();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Ignore branch loading errors
        }
    }

    private Task RecordCachedRepositoryAsync(
        string repositoryPath,
        string? repositoryUrl,
        string? branch,
        string? commitHash,
        CancellationToken cancellationToken)
    {
        if (!_repoCacheService.IsInCache(repositoryPath) ||
            string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return Task.CompletedTask;
        }

        return Task.Run(
            () => _repoCacheService.RecordIndexedRepository(
                repositoryUrl,
                repositoryPath,
                branch,
                commitHash),
            cancellationToken);
    }

    private void UpdateBranchMenu()
    {
        var branchMenuItem = _topMenuBar?.GitBranchMenuItemControl;
        if (branchMenuItem is null)
            return;

        // Clear old items - they will be garbage collected since they have no external references
        // and we're using a named handler method instead of lambda captures
        branchMenuItem.Items.Clear();
        GitBranchMenuScrollBehavior.SetScrollable(branchMenuItem, _viewModel.GitBranches.Count);

        foreach (var branch in _viewModel.GitBranches)
            branchMenuItem.Items.Add(CreateBranchMenuItem(branch));
    }

    private MenuItem CreateBranchMenuItem(GitBranch branch)
    {
        var item = new MenuItem
        {
            Header = CreateCheckedMenuHeader(branch.IsActive, branch.Name),
            Tag = branch.Name,
            MinHeight = BranchMenuItemHeight
        };

        // Use a named handler to avoid closure captures and keep menu rebuilds cheap.
        item.Click += OnBranchMenuItemClick;
        return item;
    }

    private void OnBranchMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.CanChangeProjectTree && sender is MenuItem { Tag: string name })
            _topMenuBar?.OnGitBranchSwitch(name);
    }

    #endregion
}
