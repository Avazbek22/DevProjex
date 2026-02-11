using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using DevProjex.Application.Services;
using DevProjex.Application.Models;
using DevProjex.Application.UseCases;
using DevProjex.Avalonia.ViewModels;
using DevProjex.Kernel.Contracts;
using DevProjex.Kernel.Models;

namespace DevProjex.Avalonia.Coordinators;

public sealed class SelectionSyncCoordinator
{
    private readonly MainWindowViewModel _viewModel;
    private readonly ScanOptionsUseCase _scanOptions;
    private readonly FilterOptionSelectionService _filterSelectionService;
    private readonly IgnoreOptionsService _ignoreOptionsService;
    private readonly Func<string, IgnoreRules> _buildIgnoreRules;
    private readonly Func<string, bool> _tryElevateAndRestart;
    private readonly Func<string?> _currentPathProvider;

    private IReadOnlyList<IgnoreOptionDescriptor> _ignoreOptions = Array.Empty<IgnoreOptionDescriptor>();
    private HashSet<IgnoreOptionId> _ignoreSelectionCache = new();
    private bool _ignoreSelectionInitialized;
    private HashSet<string> _extensionsSelectionCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _extensionsSelectionInitialized;
    private string? _lastLoadedPath;

    private bool _suppressRootAllCheck;
    private bool _suppressRootItemCheck;
    private bool _suppressExtensionAllCheck;
    private bool _suppressExtensionItemCheck;
    private bool _suppressIgnoreAllCheck;
    private bool _suppressIgnoreItemCheck;
    private int _rootScanVersion;
    private int _extensionScanVersion;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public SelectionSyncCoordinator(
        MainWindowViewModel viewModel,
        ScanOptionsUseCase scanOptions,
        FilterOptionSelectionService filterSelectionService,
        IgnoreOptionsService ignoreOptionsService,
        Func<string, IgnoreRules> buildIgnoreRules,
        Func<string, bool> tryElevateAndRestart,
        Func<string?> currentPathProvider)
    {
        _viewModel = viewModel;
        _scanOptions = scanOptions;
        _filterSelectionService = filterSelectionService;
        _ignoreOptionsService = ignoreOptionsService;
        _buildIgnoreRules = buildIgnoreRules;
        _tryElevateAndRestart = tryElevateAndRestart;
        _currentPathProvider = currentPathProvider;
    }

    public void HookOptionListeners(ObservableCollection<SelectionOptionViewModel> options)
    {
        // Subscribe to existing items
        foreach (var item in options)
            item.CheckedChanged += OnOptionCheckedChanged;

        // Handle collection changes - properly unsubscribe old and subscribe new
        options.CollectionChanged += (_, e) =>
        {
            // Unsubscribe from removed items
            if (e.OldItems is not null)
            {
                foreach (SelectionOptionViewModel item in e.OldItems)
                    item.CheckedChanged -= OnOptionCheckedChanged;
            }

            // Subscribe to new items
            if (e.NewItems is not null)
            {
                foreach (SelectionOptionViewModel item in e.NewItems)
                    item.CheckedChanged += OnOptionCheckedChanged;
            }

            // Handle Reset action (Clear)
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                // Re-subscribe to all current items after reset
                foreach (var item in options)
                    item.CheckedChanged += OnOptionCheckedChanged;
            }
        };
    }

    public void HookIgnoreListeners(ObservableCollection<IgnoreOptionViewModel> options)
    {
        // Subscribe to existing items
        foreach (var item in options)
            item.CheckedChanged += OnIgnoreCheckedChanged;

        // Handle collection changes - properly unsubscribe old and subscribe new
        options.CollectionChanged += (_, e) =>
        {
            // Unsubscribe from removed items
            if (e.OldItems is not null)
            {
                foreach (IgnoreOptionViewModel item in e.OldItems)
                    item.CheckedChanged -= OnIgnoreCheckedChanged;
            }

            // Subscribe to new items
            if (e.NewItems is not null)
            {
                foreach (IgnoreOptionViewModel item in e.NewItems)
                    item.CheckedChanged += OnIgnoreCheckedChanged;
            }

            // Handle Reset action (Clear)
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                // Re-subscribe to all current items after reset
                foreach (var item in options)
                    item.CheckedChanged += OnIgnoreCheckedChanged;
            }
        };
    }

    public void HandleRootAllChanged(bool isChecked, string? currentPath)
    {
        if (_suppressRootAllCheck) return;

        _suppressRootAllCheck = true;
        _viewModel.AllRootFoldersChecked = isChecked;
        _suppressRootAllCheck = false;

        SetAllChecked(_viewModel.RootFolders, isChecked, ref _suppressRootItemCheck);
        FireAndForgetSafe(UpdateLiveOptionsFromRootSelectionAsync(currentPath));
    }

    public void HandleExtensionsAllChanged(bool isChecked)
    {
        if (_suppressExtensionAllCheck) return;

        _extensionsSelectionInitialized = true;
        _suppressExtensionAllCheck = true;
        _viewModel.AllExtensionsChecked = isChecked;
        _suppressExtensionAllCheck = false;

        SetAllChecked(_viewModel.Extensions, isChecked, ref _suppressExtensionItemCheck);
        UpdateExtensionsSelectionCache();
    }

    public void HandleIgnoreAllChanged(bool isChecked, string? currentPath)
    {
        if (_suppressIgnoreAllCheck) return;

        _ignoreSelectionInitialized = true;

        _suppressIgnoreAllCheck = true;
        _viewModel.AllIgnoreChecked = isChecked;
        _suppressIgnoreAllCheck = false;

        SetAllChecked(_viewModel.IgnoreOptions, isChecked, ref _suppressIgnoreItemCheck);
        UpdateIgnoreSelectionCache();
        if (!string.IsNullOrEmpty(currentPath))
        {
            FireAndForgetSafe(RefreshRootAndDependentsAsync(currentPath));
        }
    }

    public Task PopulateExtensionsForRootSelectionAsync(string path, IReadOnlyCollection<string> rootFolders)
    {
        if (string.IsNullOrEmpty(path)) return Task.CompletedTask;
        var version = Interlocked.Increment(ref _extensionScanVersion);

        var prev = _extensionsSelectionCache.Count > 0
            ? new HashSet<string>(_extensionsSelectionCache, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(_viewModel.Extensions.Where(o => o.IsChecked).Select(o => o.Name),
                StringComparer.OrdinalIgnoreCase);

        if (rootFolders.Count == 0)
        {
            _viewModel.Extensions.Clear();
            _suppressExtensionAllCheck = true;
            _viewModel.AllExtensionsChecked = false;
            _suppressExtensionAllCheck = false;
            SyncAllCheckbox(_viewModel.Extensions, ref _suppressExtensionAllCheck,
                value => _viewModel.AllExtensionsChecked = value);
            return Task.CompletedTask;
        }

        var ignoreRules = _buildIgnoreRules(path);
        return Task.Run(async () =>
        {
            // Scan extensions off the UI thread to avoid freezing on large folders.
            var scan = _scanOptions.GetExtensionsForRootFolders(path, rootFolders, ignoreRules);
            if (scan.RootAccessDenied)
            {
                var elevated = await Dispatcher.UIThread.InvokeAsync(() => _tryElevateAndRestart(path));
                if (elevated) return;
            }

            var options = _filterSelectionService.BuildExtensionOptions(scan.Value, prev);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _extensionScanVersion) return;
                ApplyExtensionOptions(options);
            });
        });
    }

    public Task PopulateRootFoldersAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return Task.CompletedTask;
        var version = Interlocked.Increment(ref _rootScanVersion);

        var prev = new HashSet<string>(_viewModel.RootFolders.Where(o => o.IsChecked).Select(o => o.Name),
            StringComparer.OrdinalIgnoreCase);

        var ignoreRules = _buildIgnoreRules(path);
        return Task.Run(async () =>
        {
            // Scan root folders off the UI thread to keep the window responsive.
            var scan = _scanOptions.Execute(new ScanOptionsRequest(path, ignoreRules));
            if (scan.RootAccessDenied)
            {
                var elevated = await Dispatcher.UIThread.InvokeAsync(() => _tryElevateAndRestart(path));
                if (elevated) return;
            }

            var options = _filterSelectionService.BuildRootFolderOptions(scan.RootFolders, prev, ignoreRules);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _rootScanVersion) return;
                _viewModel.RootFolders.Clear();

                _suppressRootItemCheck = true;
                foreach (var option in options)
                    _viewModel.RootFolders.Add(new SelectionOptionViewModel(option.Name, option.IsChecked));
                _suppressRootItemCheck = false;

                if (_viewModel.AllRootFoldersChecked)
                    SetAllChecked(_viewModel.RootFolders, true, ref _suppressRootItemCheck);

                SyncAllCheckbox(_viewModel.RootFolders, ref _suppressRootAllCheck,
                    value => _viewModel.AllRootFoldersChecked = value);
            });
        });
    }

    public void PopulateIgnoreOptionsForRootSelection(IReadOnlyCollection<string> rootFolders, string? currentPath = null)
    {
        var previousSelections = _ignoreSelectionCache;

        _suppressIgnoreItemCheck = true;
        try
        {
            _viewModel.IgnoreOptions.Clear();

            if (rootFolders.Count == 0)
            {
                _ignoreOptions = Array.Empty<IgnoreOptionDescriptor>();
                _suppressIgnoreAllCheck = true;
                _viewModel.AllIgnoreChecked = false;
                _suppressIgnoreAllCheck = false;
                return;
            }

            var path = string.IsNullOrWhiteSpace(currentPath) ? _currentPathProvider() : currentPath;
            var hasGitIgnore = HasGitIgnore(path);
            _ignoreOptions = _ignoreOptionsService.GetOptions(includeGitIgnore: hasGitIgnore);
            bool hasPrevious = _ignoreSelectionInitialized;

            foreach (var option in _ignoreOptions)
            {
                bool isChecked = previousSelections.Contains(option.Id) ||
                                 (!hasPrevious && option.DefaultChecked);
                _viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(option.Id, option.Label, isChecked));
            }
        }
        finally
        {
            _suppressIgnoreItemCheck = false;
        }

        if (_viewModel.AllIgnoreChecked)
            SetAllChecked(_viewModel.IgnoreOptions, true, ref _suppressIgnoreItemCheck);

        UpdateIgnoreSelectionCache();
        SyncIgnoreAllCheckbox();
    }

    public IReadOnlyCollection<string> GetSelectedRootFolders()
    {
        return _viewModel.RootFolders.Where(o => o.IsChecked).Select(o => o.Name).ToList();
    }

    public async Task UpdateLiveOptionsFromRootSelectionAsync(string? currentPath)
    {
        if (string.IsNullOrEmpty(currentPath)) return;

        var selectedRoots = GetSelectedRootFolders();
        await PopulateExtensionsForRootSelectionAsync(currentPath, selectedRoots);
        PopulateIgnoreOptionsForRootSelection(selectedRoots, currentPath);
    }

    public async Task RefreshRootAndDependentsAsync(string currentPath)
    {
        // Serialize refresh operations to prevent race conditions
        await _refreshLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Clear old caches when switching to a different folder
            if (_lastLoadedPath is not null && !string.Equals(_lastLoadedPath, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                ClearCachesForNewProject();
            }
            _lastLoadedPath = currentPath;

            // Run in order so root folders are ready before extensions/ignore lists refresh.
            await PopulateRootFoldersAsync(currentPath);
            await UpdateLiveOptionsFromRootSelectionAsync(currentPath);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Clears internal caches when switching to a new project folder.
    /// This helps release memory from the previous project.
    /// </summary>
    private void ClearCachesForNewProject()
    {
        // Unsubscribe from old items before clearing to help GC
        UnsubscribeFromOptionItems();

        // Clear extension selection cache
        _extensionsSelectionCache.Clear();
        _extensionsSelectionCache.TrimExcess();
        _extensionsSelectionInitialized = false;

        // Clear ignore selection cache
        _ignoreSelectionCache.Clear();
        _ignoreSelectionCache.TrimExcess();
        _ignoreSelectionInitialized = false;

        // Clear ignore options
        _ignoreOptions = Array.Empty<IgnoreOptionDescriptor>();
    }

    /// <summary>
    /// Unsubscribes from CheckedChanged events on all option items.
    /// </summary>
    private void UnsubscribeFromOptionItems()
    {
        foreach (var item in _viewModel.RootFolders)
            item.CheckedChanged -= OnOptionCheckedChanged;

        foreach (var item in _viewModel.Extensions)
            item.CheckedChanged -= OnOptionCheckedChanged;

        foreach (var item in _viewModel.IgnoreOptions)
            item.CheckedChanged -= OnIgnoreCheckedChanged;
    }

    public IReadOnlyCollection<IgnoreOptionId> GetSelectedIgnoreOptionIds()
    {
        EnsureIgnoreSelectionCache();
        if (_ignoreOptions.Count == 0 || _viewModel.IgnoreOptions.Count == 0)
            return _ignoreSelectionCache;

        var selected = _viewModel.IgnoreOptions
            .Where(o => o.IsChecked)
            .Select(o => o.Id)
            .ToHashSet();

        _ignoreSelectionCache = selected;
        return selected;
    }

    private void EnsureIgnoreSelectionCache()
    {
        if (_ignoreSelectionInitialized || _ignoreSelectionCache.Count > 0)
            return;

        var hasGitIgnore = HasGitIgnore(_currentPathProvider() ?? _lastLoadedPath);
        _ignoreOptions = _ignoreOptionsService.GetOptions(includeGitIgnore: hasGitIgnore);
        _ignoreSelectionCache = new HashSet<IgnoreOptionId>(
            _ignoreOptions.Where(option => option.DefaultChecked).Select(option => option.Id));
    }

    private static bool HasGitIgnore(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return false;

        try
        {
            return File.Exists(Path.Combine(rootPath, ".gitignore"));
        }
        catch
        {
            return false;
        }
    }

    public void UpdateExtensionsSelectionCache()
    {
        if (_viewModel.Extensions.Count == 0)
            return;

        _extensionsSelectionInitialized = true;
        _extensionsSelectionCache = new HashSet<string>(
            _viewModel.Extensions.Where(o => o.IsChecked).Select(o => o.Name),
            StringComparer.OrdinalIgnoreCase);
    }

    internal void ApplyExtensionScan(IReadOnlyCollection<string> extensions)
    {
        var prev = _extensionsSelectionCache.Count > 0
            ? new HashSet<string>(_extensionsSelectionCache, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(_viewModel.Extensions.Where(o => o.IsChecked).Select(o => o.Name),
                StringComparer.OrdinalIgnoreCase);

        var options = _filterSelectionService.BuildExtensionOptions(extensions, prev);
        ApplyExtensionOptions(options);
    }

    public void UpdateIgnoreSelectionCache()
    {
        if (_ignoreOptions.Count == 0 || _viewModel.IgnoreOptions.Count == 0)
            return;

        _ignoreSelectionCache = new HashSet<IgnoreOptionId>(
            _viewModel.IgnoreOptions.Where(o => o.IsChecked).Select(o => o.Id));
    }

    public void SyncIgnoreAllCheckbox()
    {
        SyncAllCheckbox(_viewModel.IgnoreOptions, ref _suppressIgnoreAllCheck,
            value => _viewModel.AllIgnoreChecked = value);
    }

    private void OnOptionCheckedChanged(object? sender, EventArgs e)
    {
        if (sender is not SelectionOptionViewModel option)
            return;

        if (_viewModel.RootFolders.Contains(option))
        {
            if (_suppressRootItemCheck) return;

            SyncAllCheckbox(_viewModel.RootFolders, ref _suppressRootAllCheck,
                value => _viewModel.AllRootFoldersChecked = value);

            _ = UpdateLiveOptionsFromRootSelectionAsync(_currentPathProvider());
        }
        else if (_viewModel.Extensions.Contains(option))
        {
            if (_suppressExtensionItemCheck) return;

            _extensionsSelectionInitialized = true;
            SyncAllCheckbox(_viewModel.Extensions, ref _suppressExtensionAllCheck,
                value => _viewModel.AllExtensionsChecked = value);

            UpdateExtensionsSelectionCache();
        }
    }

    private void OnIgnoreCheckedChanged(object? sender, EventArgs e)
    {
        if (_suppressIgnoreItemCheck) return;

        _ignoreSelectionInitialized = true;

        SyncAllCheckbox(_viewModel.IgnoreOptions, ref _suppressIgnoreAllCheck,
            value => _viewModel.AllIgnoreChecked = value);

        UpdateIgnoreSelectionCache();

        var currentPath = _currentPathProvider();
        if (!string.IsNullOrEmpty(currentPath))
        {
            FireAndForgetSafe(RefreshRootAndDependentsAsync(currentPath));
        }
    }

    private static void SyncAllCheckbox<T>(
        IEnumerable<T> options,
        ref bool suppressFlag,
        Action<bool> setValue)
        where T : class
    {
        suppressFlag = true;
        try
        {
            // Avoid ToList() allocation - iterate once with early exit
            bool hasItems = false;
            bool allChecked = true;
            foreach (var option in options)
            {
                hasItems = true;
                bool isChecked = option switch
                {
                    SelectionOptionViewModel selection => selection.IsChecked,
                    IgnoreOptionViewModel ignore => ignore.IsChecked,
                    _ => false
                };
                if (!isChecked)
                {
                    allChecked = false;
                    break;
                }
            }
            setValue(hasItems && allChecked);
        }
        finally
        {
            suppressFlag = false;
        }
    }

    private void ApplyExtensionOptions(IReadOnlyList<SelectionOption> options)
    {
        _viewModel.Extensions.Clear();

        _suppressExtensionItemCheck = true;
        foreach (var option in options)
            _viewModel.Extensions.Add(new SelectionOptionViewModel(option.Name, option.IsChecked));
        _suppressExtensionItemCheck = false;

        if (_viewModel.AllExtensionsChecked)
            SetAllChecked(_viewModel.Extensions, true, ref _suppressExtensionItemCheck);

        SyncAllCheckbox(_viewModel.Extensions, ref _suppressExtensionAllCheck,
            value => _viewModel.AllExtensionsChecked = value);
        if (!_extensionsSelectionInitialized)
        {
            _extensionsSelectionInitialized = true;
            UpdateExtensionsSelectionCache();
        }
    }

    private static void SetAllChecked<T>(
        IEnumerable<T> options,
        bool isChecked,
        ref bool suppressFlag)
        where T : class
    {
        suppressFlag = true;
        try
        {
            foreach (var option in options)
            {
                switch (option)
                {
                    case SelectionOptionViewModel selection:
                        selection.IsChecked = isChecked;
                        break;
                    case IgnoreOptionViewModel ignore:
                        ignore.IsChecked = isChecked;
                        break;
                }
            }
        }
        finally
        {
            suppressFlag = false;
        }
    }

    /// <summary>
    /// Fire-and-forget wrapper that suppresses exceptions.
    /// Used for background refresh triggered by UI events where errors are non-critical.
    /// </summary>
    private static async void FireAndForgetSafe(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when operation is superseded
        }
        catch
        {
            // Log or handle if needed; suppressed to not crash UI
        }
    }
}
