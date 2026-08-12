using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
    private static readonly TimeSpan RecentProjectsStartupStoreLockTimeout =
        TimeSpan.FromMilliseconds(100);

    #region Recent Projects

    private void LoadRecentProjectsSynchronously()
    {
        _recentProjectsDb = _recentProjectsStore.LoadForStartup(
            RecentProjectsStartupStoreLockTimeout);
        _recentProjectsLoadTask = Task.FromResult(_recentProjectsDb);
        _recentProjectsLoaded = true;
        SyncRecentProjectsToViewModel();
    }

    private void StartDeferredRecentProjectsLoad(CancellationToken cancellationToken)
        => ObserveDetachedTask(
            EnsureRecentProjectsLoadedAsync(cancellationToken),
            "LoadRecentProjects");

    private async Task EnsureRecentProjectsLoadedAsync(CancellationToken cancellationToken)
    {
        if (_recentProjectsLoaded)
            return;

        // Keep persistence IO off the dispatcher, but share one load between startup,
        // the Recent menu, Desktop IPC, and the Git clone dialog.
        _recentProjectsLoadTask ??= Task.Run(
            () => _recentProjectsStore.LoadForStartup(RecentProjectsStartupStoreLockTimeout),
            CancellationToken.None);

        var loaded = await _recentProjectsLoadTask.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (_recentProjectsLoaded)
            return;

        _recentProjectsDb = loaded;
        _recentProjectsLoaded = true;
        SyncRecentProjectsToViewModel();
    }

    private void SyncRecentProjectsToViewModel()
    {
        var workspaces = _recentWorkspacesService.Project(
            _recentProjectsDb.RecentFolders
                .Select(static entry => new RecentWorkspaceSource(
                    RecentWorkspaceKind.Folder,
                    entry.Path,
                    entry.OpenedUtc))
                .Concat(_recentProjectsDb.RecentRepositories.Select(static entry =>
                    new RecentWorkspaceSource(
                        RecentWorkspaceKind.Repository,
                        entry.Url,
                        entry.OpenedUtc))));
        _viewModel.RecentFolders.Clear();
        foreach (var workspace in workspaces.Where(static item =>
                     item.Kind == RecentWorkspaceKind.Folder))
        {
            _viewModel.RecentFolders.Add(new RecentProjectEntryViewModel(
                workspace.Source,
                RecentProjectPresentationService.CreateFolderDisplayText(workspace.Source),
                RecentProjectPresentationService.CreateFolderToolTip(workspace.Source)));
        }

        _viewModel.RecentRepositories.Clear();
        foreach (var workspace in workspaces.Where(static item =>
                     item.Kind == RecentWorkspaceKind.Repository))
        {
            _viewModel.RecentRepositories.Add(new RecentProjectEntryViewModel(
                workspace.Source,
                RecentProjectPresentationService.CreateRepositoryDisplayText(workspace.Source),
                RecentProjectPresentationService.CreateRepositoryToolTip(workspace.Source)));
        }
    }

    private void AttachRecentMenuHandlers()
    {
        if (_topMenuBar?.RecentMenuItemControl is { } recentMenuItem)
            recentMenuItem.SubmenuOpened += OnRecentMenuSubmenuOpened;
    }

    private void DetachRecentMenuHandlers()
    {
        if (_topMenuBar?.RecentMenuItemControl is { } recentMenuItem)
            recentMenuItem.SubmenuOpened -= OnRecentMenuSubmenuOpened;
    }

    private async void OnRecentMenuSubmenuOpened(object? sender, RoutedEventArgs e)
    {
        try
        {
            var lifetimeToken = _windowLifetimeCts?.Token ?? CancellationToken.None;
            await EnsureRecentProjectsLoadedAsync(lifetimeToken);
            _recentMenuMaterialized = true;
            RefreshRecentFoldersMenu();
            StartRecentFolderAvailabilityRefresh();
        }
        catch (OperationCanceledException)
        {
            // Closing the window owns cancellation of deferred menu work.
        }
    }

    private void RefreshRecentFoldersMenu()
    {
        var recentMenuItem = _topMenuBar?.RecentMenuItemControl;
        if (recentMenuItem is null)
            return;

        recentMenuItem.Items.Clear();

        if (_viewModel.RecentFolders.Count == 0)
        {
            recentMenuItem.Items.Add(new MenuItem
            {
                Header = _viewModel.MenuFileRecentEmpty,
                IsEnabled = false
            });
            return;
        }

        foreach (var recentFolder in _viewModel.RecentFolders)
        {
            var item = new MenuItem
            {
                Header = recentFolder.DisplayText,
                Tag = recentFolder.Value
            };

            ToolTip.SetTip(item, null);
            SetRecentFolderMenuItemAvailability(
                item,
                !_unavailableRecentFolderPaths.Contains(recentFolder.Value));
            item.Click += OnRecentFolderMenuItemClick;
            recentMenuItem.Items.Add(item);
        }
    }

    private async void OnRecentFolderMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanChangeProjectTree || sender is not MenuItem { Tag: string path })
            return;

        var lifetimeToken = _windowLifetimeCts?.Token ?? CancellationToken.None;
        bool isAvailable;
        try
        {
            isAvailable = await _recentFolderAvailabilityService.IsAvailableAsync(path, lifetimeToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        UpdateRecentFolderAvailability(path, isAvailable);
        ApplyRecentFolderAvailabilityToMenu();
        if (!isAvailable)
        {
            var shouldRemove = await MessageDialog.ShowConfirmationAsync(
                this,
                _localization["Dialog.RecentFolderUnavailable.Title"],
                _localization.Format("Dialog.RecentFolderUnavailable.Message", path),
                _localization["Dialog.RecentFolderUnavailable.Remove"],
                _localization["Dialog.RecentFolderUnavailable.Keep"],
                width: 450,
                height: 180);

            if (shouldRemove)
                await RemoveRecentFolderAsync(path, lifetimeToken);
            return;
        }

        await TryOpenFolderAsync(path, fromDialog: true);
    }

    private void StartRecentFolderAvailabilityRefresh()
    {
        if (_recentFolderAvailabilityRefreshTask is { IsCompleted: false } ||
            _windowLifetimeCts is not { } lifetime)
        {
            return;
        }

        var refreshTask = RefreshRecentFolderAvailabilityAsync(lifetime.Token);
        _recentFolderAvailabilityRefreshTask = refreshTask;
        ObserveDetachedTask(refreshTask, "RefreshRecentFolderAvailability");
    }

    private async Task RefreshRecentFolderAvailabilityAsync(CancellationToken cancellationToken)
    {
        var paths = _viewModel.RecentFolders
            .Select(static folder => folder.Value)
            .ToArray();
        if (paths.Length == 0)
        {
            _unavailableRecentFolderPaths.Clear();
            return;
        }

        var availability = await _recentFolderAvailabilityService.CheckAsync(paths, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        _unavailableRecentFolderPaths.IntersectWith(paths);
        foreach (var (path, isAvailable) in availability)
            UpdateRecentFolderAvailability(path, isAvailable);

        ApplyRecentFolderAvailabilityToMenu();
    }

    private void ApplyRecentFolderAvailabilityToMenu()
    {
        if (_topMenuBar?.RecentMenuItemControl is not { } recentMenuItem)
            return;

        foreach (var item in recentMenuItem.Items.OfType<MenuItem>())
        {
            if (item.Tag is string path)
            {
                SetRecentFolderMenuItemAvailability(
                    item,
                    !_unavailableRecentFolderPaths.Contains(path));
            }
        }
    }

    private void UpdateRecentFolderAvailability(string path, bool isAvailable)
    {
        if (isAvailable)
            _unavailableRecentFolderPaths.Remove(path);
        else
            _unavailableRecentFolderPaths.Add(path);
    }

    private static void SetRecentFolderMenuItemAvailability(MenuItem item, bool isAvailable)
    {
        const string unavailableClass = "recent-folder-unavailable";
        if (isAvailable)
            item.Classes.Remove(unavailableClass);
        else if (!item.Classes.Contains(unavailableClass))
            item.Classes.Add(unavailableClass);
    }

    private async Task RemoveRecentFolderAsync(string path, CancellationToken cancellationToken)
    {
        await EnsureRecentProjectsLoadedAsync(cancellationToken);
        var recentProjectsSnapshot = _recentProjectsDb;
        var updatedRecentProjects = await Task.Run(
            () => _recentProjectsStore.RemoveFolder(recentProjectsSnapshot, path),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        _recentProjectsDb = updatedRecentProjects;
        _unavailableRecentFolderPaths.Remove(path);
        SyncRecentProjectsToViewModel();
        RefreshRecentFoldersMenuIfMaterialized();
    }

    private async Task RecordRecentFolderAsync(string path, CancellationToken cancellationToken)
    {
        await EnsureRecentProjectsLoadedAsync(cancellationToken);
        var recentProjectsSnapshot = _recentProjectsDb;
        var updatedRecentProjects = await Task.Run(
            () => _recentProjectsStore.AddFolder(recentProjectsSnapshot, path),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        _recentProjectsDb = updatedRecentProjects;
        _unavailableRecentFolderPaths.Remove(path);
        SyncRecentProjectsToViewModel();
        RefreshRecentFoldersMenuIfMaterialized();
    }

    private async Task RecordRecentRepositoryAsync(
        string repositoryUrl,
        CancellationToken cancellationToken)
    {
        await EnsureRecentProjectsLoadedAsync(cancellationToken);
        var recentProjectsSnapshot = _recentProjectsDb;
        var updatedRecentProjects = await Task.Run(
            () => _recentProjectsStore.AddRepository(recentProjectsSnapshot, repositoryUrl),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        _recentProjectsDb = updatedRecentProjects;
        SyncRecentProjectsToViewModel();
    }

    private void RefreshRecentFoldersMenuIfMaterialized()
    {
        if (_recentMenuMaterialized)
            RefreshRecentFoldersMenu();
    }

    #endregion

    #region Language Menu

    private void RefreshLanguageMenuChecks()
    {
        foreach (var (item, language, label) in EnumerateLanguageMenuItems())
        {
            if (item is null)
                continue;

            item.Header = CreateCheckedMenuHeader(_localization.CurrentLanguage == language, label);
        }
    }

    private IEnumerable<(MenuItem? Item, AppLanguage Language, string Label)> EnumerateLanguageMenuItems()
    {
        var topMenuBar = _topMenuBar;
        if (topMenuBar is null)
            yield break;

        yield return (topMenuBar.LanguageEnMenuItemControl, AppLanguage.En, "English");
        yield return (topMenuBar.LanguageRuMenuItemControl, AppLanguage.Ru, "Русский");
        yield return (topMenuBar.LanguageEsMenuItemControl, AppLanguage.Es, "Español");
        yield return (topMenuBar.LanguagePtMenuItemControl, AppLanguage.Pt, "Português (Brasil)");
        yield return (topMenuBar.LanguagePtPtMenuItemControl, AppLanguage.PtPt, "Português (Portugal)");
        yield return (topMenuBar.LanguageDeMenuItemControl, AppLanguage.De, "Deutsch");
        yield return (topMenuBar.LanguageFrMenuItemControl, AppLanguage.Fr, "Français");
        yield return (topMenuBar.LanguageItMenuItemControl, AppLanguage.It, "Italiano");
        yield return (topMenuBar.LanguageTgMenuItemControl, AppLanguage.Tg, "Тоҷикӣ");
        yield return (topMenuBar.LanguageUzMenuItemControl, AppLanguage.Uz, "Oʻzbek");
        yield return (topMenuBar.LanguageKkMenuItemControl, AppLanguage.Kk, "Қазақ");
    }

    private static string CreateCheckedMenuHeader(bool isChecked, string label)
        => isChecked ? $"✓ {label}" : $"   {label}";

    #endregion

    #region Tree Font Menu

    private void AttachTreeFontMenuHandlers()
    {
        if (_topMenuBar?.TreeFontMenuItemControl is { } treeFontMenuItem)
            treeFontMenuItem.SubmenuOpened += OnTreeFontMenuSubmenuOpened;
    }

    private void DetachTreeFontMenuHandlers()
    {
        if (_topMenuBar?.TreeFontMenuItemControl is { } treeFontMenuItem)
            treeFontMenuItem.SubmenuOpened -= OnTreeFontMenuSubmenuOpened;
    }

    private void OnTreeFontMenuSubmenuOpened(object? sender, RoutedEventArgs e)
    {
        EnsureOptionalFontCatalogLoaded();
        RefreshTreeFontMenu();
    }

    private void RefreshTreeFontMenu()
    {
        var treeFontMenuItem = _topMenuBar?.TreeFontMenuItemControl;
        if (treeFontMenuItem is null)
            return;

        treeFontMenuItem.Items.Clear();
        foreach (var fontFamily in EnumerateTreeFontMenuFamilies())
            treeFontMenuItem.Items.Add(CreateTreeFontMenuItem(fontFamily));
    }

    private IEnumerable<FontFamily> EnumerateTreeFontMenuFamilies()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fontFamily in _viewModel.FontFamilies)
        {
            if (seen.Add(GetTreeFontKey(fontFamily)))
                yield return fontFamily;
        }
    }

    private MenuItem CreateTreeFontMenuItem(FontFamily fontFamily)
    {
        var displayName = GetTreeFontDisplayName(fontFamily);
        var item = new MenuItem
        {
            Header = CreateCheckedMenuHeader(IsSelectedTreeFont(fontFamily), displayName),
            Tag = fontFamily,
            MinHeight = TreeFontMenuItemHeight
        };

        item.Click += OnTreeFontMenuItemClick;
        return item;
    }

    private void OnTreeFontMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: FontFamily fontFamily })
            return;

        _viewModel.SelectedFontFamily = fontFamily;
        e.Handled = true;
    }

    private bool IsSelectedTreeFont(FontFamily fontFamily)
        => AreSameTreeFont(_viewModel.SelectedFontFamily, fontFamily);

    private string GetTreeFontDisplayName(FontFamily fontFamily)
    {
        if (IsDefaultTreeFont(fontFamily))
            return _viewModel.SettingsFontDefault;

        var name = fontFamily.Name?.Trim();
        return string.IsNullOrWhiteSpace(name) ? _viewModel.SettingsFontDefault : name;
    }

    private static bool AreSameTreeFont(FontFamily? left, FontFamily? right)
    {
        if (IsDefaultTreeFont(left) && IsDefaultTreeFont(right))
            return true;

        return string.Equals(left?.Name, right?.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetTreeFontKey(FontFamily fontFamily)
        => IsDefaultTreeFont(fontFamily) ? string.Empty : fontFamily.Name ?? string.Empty;

    private static bool IsDefaultTreeFont(FontFamily? fontFamily)
    {
        var name = fontFamily?.Name;
        return string.IsNullOrWhiteSpace(name) || name.StartsWith("$", StringComparison.Ordinal);
    }

    #endregion
}
