using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
    private static readonly TimeSpan RecentProjectsStartupStoreLockTimeout =
        TimeSpan.FromMilliseconds(100);

    #region Recent Projects

    private void LoadRecentProjects()
    {
        _recentProjectsDb = _recentProjectsStore.LoadForStartup(
            RecentProjectsStartupStoreLockTimeout);
        SyncRecentProjectsToViewModel();
    }

    private void SyncRecentProjectsToViewModel()
    {
        _viewModel.RecentFolders.Clear();
        foreach (var entry in _recentProjectsDb.RecentFolders)
        {
            _viewModel.RecentFolders.Add(new RecentProjectEntryViewModel(
                entry.Path,
                RecentProjectPresentationService.CreateFolderDisplayText(entry.Path),
                RecentProjectPresentationService.CreateFolderToolTip(entry.Path)));
        }

        _viewModel.RecentRepositories.Clear();
        foreach (var entry in _recentProjectsDb.RecentRepositories)
        {
            _viewModel.RecentRepositories.Add(new RecentProjectEntryViewModel(
                entry.Url,
                RecentProjectPresentationService.CreateRepositoryDisplayText(entry.Url),
                RecentProjectPresentationService.CreateRepositoryToolTip(entry.Url)));
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

    private void OnRecentMenuSubmenuOpened(object? sender, RoutedEventArgs e)
    {
        RefreshRecentFoldersMenu();
        StartRecentFolderAvailabilityRefresh();
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
                RemoveRecentFolder(path);
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

    private void RemoveRecentFolder(string path)
    {
        _recentProjectsDb = _recentProjectsStore.RemoveFolder(_recentProjectsDb, path);
        _unavailableRecentFolderPaths.Remove(path);
        SyncRecentProjectsToViewModel();
        RefreshRecentFoldersMenu();
    }

    private void RecordRecentFolder(string path)
    {
        _recentProjectsDb = _recentProjectsStore.AddFolder(_recentProjectsDb, path);
        _unavailableRecentFolderPaths.Remove(path);
        SyncRecentProjectsToViewModel();
        RefreshRecentFoldersMenu();
    }

    private void RecordRecentRepository(string repositoryUrl)
    {
        _recentProjectsDb = _recentProjectsStore.AddRepository(_recentProjectsDb, repositoryUrl);
        SyncRecentProjectsToViewModel();
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
            Header = CreateCheckedMenuHeader(IsPendingTreeFont(fontFamily), displayName),
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

        _viewModel.PendingFontFamily = fontFamily;
        e.Handled = true;
    }

    private bool IsPendingTreeFont(FontFamily fontFamily)
        => AreSameTreeFont(_viewModel.PendingFontFamily, fontFamily);

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
