using System.Collections.ObjectModel;
using System.Collections.Specialized;
using DevProjex.Application.Models;
using DevProjex.Application.Services;
using DevProjex.Application.UseCases;
using DevProjex.Kernel;
using DevProjex.Kernel.Contracts;
using DevProjex.Kernel.Models;

namespace DevProjex.Avalonia.Coordinators;

public sealed class SelectionSyncCoordinator : IDisposable
{
    private enum PreparedSelectionMode
    {
        None = 0,
        Defaults = 1,
        Profile = 2
    }

    private readonly MainWindowViewModel _viewModel;

    // Store collection references for proper cleanup
    private ObservableCollection<SelectionOptionViewModel>? _hookedRootFolders;
    private ObservableCollection<SelectionOptionViewModel>? _hookedExtensions;
    private ObservableCollection<IgnoreOptionViewModel>? _hookedIgnoreOptions;

    // Named handlers for proper unsubscription
    private NotifyCollectionChangedEventHandler? _rootFoldersCollectionChangedHandler;
    private NotifyCollectionChangedEventHandler? _extensionsCollectionChangedHandler;
    private NotifyCollectionChangedEventHandler? _ignoreOptionsCollectionChangedHandler;

    private bool _disposed;
    private readonly ScanOptionsUseCase _scanOptions;
    private readonly FilterOptionSelectionService _filterSelectionService;
    private readonly IgnoreOptionsService _ignoreOptionsService;
    private readonly Func<string, IReadOnlyCollection<IgnoreOptionId>, IReadOnlyCollection<string>?, IgnoreRules> _buildIgnoreRules;
    private readonly Func<string, IReadOnlyCollection<string>, IgnoreOptionsAvailability> _getIgnoreOptionsAvailability;
    private readonly Func<string, bool> _tryElevateAndRestart;
    private readonly Func<string?> _currentPathProvider;
    private static readonly HashSet<string> EmptyStringSet = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<IgnoreOptionDescriptor> _ignoreOptions = Array.Empty<IgnoreOptionDescriptor>();
    private HashSet<IgnoreOptionId> _ignoreSelectionCache = new();
    private bool _ignoreSelectionInitialized;
    private HashSet<string> _rootSelectionCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _rootSelectionInitialized;
    private HashSet<string> _extensionsSelectionCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _extensionsSelectionInitialized;
    private bool _hasExtensionlessExtensionEntries;
    private string? _lastLoadedPath;
    private string? _preparedSelectionPath;
    private PreparedSelectionMode _preparedSelectionMode;

    private bool _suppressRootAllCheck;
    private bool _suppressRootItemCheck;
    private bool _suppressExtensionAllCheck;
    private bool _suppressExtensionItemCheck;
    private bool _suppressIgnoreAllCheck;
    private bool _suppressIgnoreItemCheck;
    private int _rootScanVersion;
    private int _extensionScanVersion;
    private int _ignoreOptionsVersion;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public SelectionSyncCoordinator(
        MainWindowViewModel viewModel,
        ScanOptionsUseCase scanOptions,
        FilterOptionSelectionService filterSelectionService,
        IgnoreOptionsService ignoreOptionsService,
        Func<string, IReadOnlyCollection<IgnoreOptionId>, IReadOnlyCollection<string>?, IgnoreRules> buildIgnoreRules,
        Func<string, IReadOnlyCollection<string>, IgnoreOptionsAvailability> getIgnoreOptionsAvailability,
        Func<string, bool> tryElevateAndRestart,
        Func<string?> currentPathProvider)
    {
        _viewModel = viewModel;
        _scanOptions = scanOptions;
        _filterSelectionService = filterSelectionService;
        _ignoreOptionsService = ignoreOptionsService;
        _buildIgnoreRules = buildIgnoreRules;
        _getIgnoreOptionsAvailability = getIgnoreOptionsAvailability;
        _tryElevateAndRestart = tryElevateAndRestart;
        _currentPathProvider = currentPathProvider;
    }

    public SelectionSyncCoordinator(
        MainWindowViewModel viewModel,
        ScanOptionsUseCase scanOptions,
        FilterOptionSelectionService filterSelectionService,
        IgnoreOptionsService ignoreOptionsService,
        Func<string, IgnoreRules> buildIgnoreRules,
        Func<string, bool> tryElevateAndRestart,
        Func<string?> currentPathProvider)
        : this(
            viewModel,
            scanOptions,
            filterSelectionService,
            ignoreOptionsService,
            (rootPath, _, _) => buildIgnoreRules(rootPath),
            (rootPath, _) => new IgnoreOptionsAvailability(
                IncludeGitIgnore: HasGitIgnore(rootPath),
                IncludeSmartIgnore: false),
            tryElevateAndRestart,
            currentPathProvider)
    {
    }

    public void HookOptionListeners(ObservableCollection<SelectionOptionViewModel> options)
    {
        // Track which collection this is for proper cleanup
        if (_hookedRootFolders is null)
        {
            _hookedRootFolders = options;
            _rootFoldersCollectionChangedHandler = CreateSelectionCollectionChangedHandler(options);
        }
        else if (_hookedExtensions is null)
        {
            _hookedExtensions = options;
            _extensionsCollectionChangedHandler = CreateSelectionCollectionChangedHandler(options);
        }

        // Subscribe to existing items
        foreach (var item in options)
            item.CheckedChanged += OnOptionCheckedChanged;

        // Get the appropriate handler
        var handler = ReferenceEquals(options, _hookedRootFolders)
            ? _rootFoldersCollectionChangedHandler
            : _extensionsCollectionChangedHandler;

        // Handle collection changes - properly unsubscribe old and subscribe new
        if (handler is not null)
            options.CollectionChanged += handler;
    }

    private NotifyCollectionChangedEventHandler CreateSelectionCollectionChangedHandler(
        ObservableCollection<SelectionOptionViewModel> options)
    {
        return (_, e) =>
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
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                // Re-subscribe to all current items after reset
                foreach (var item in options)
                    item.CheckedChanged += OnOptionCheckedChanged;
            }
        };
    }

    public void HookIgnoreListeners(ObservableCollection<IgnoreOptionViewModel> options)
    {
        _hookedIgnoreOptions = options;

        // Subscribe to existing items
        foreach (var item in options)
            item.CheckedChanged += OnIgnoreCheckedChanged;

        // Create named handler for proper cleanup
        _ignoreOptionsCollectionChangedHandler = (_, e) =>
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
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                // Re-subscribe to all current items after reset
                foreach (var item in options)
                    item.CheckedChanged += OnIgnoreCheckedChanged;
            }
        };

        // Handle collection changes - properly unsubscribe old and subscribe new
        options.CollectionChanged += _ignoreOptionsCollectionChangedHandler;
    }

    public void HandleRootAllChanged(bool isChecked, string? currentPath)
    {
        if (_suppressRootAllCheck) return;

        _rootSelectionInitialized = true;
        _suppressRootAllCheck = true;
        _viewModel.AllRootFoldersChecked = isChecked;
        _suppressRootAllCheck = false;

        SetAllChecked(_viewModel.RootFolders, isChecked, ref _suppressRootItemCheck);
        UpdateRootSelectionCache();
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

    public Task PopulateExtensionsForRootSelectionAsync(
        string path,
        IReadOnlyCollection<string> rootFolders,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path)) return Task.CompletedTask;
        if (IsStalePathRequest(path)) return Task.CompletedTask;
        var version = Interlocked.Increment(ref _extensionScanVersion);

        var prev = _extensionsSelectionInitialized
            ? new HashSet<string>(_extensionsSelectionCache, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Always scan extensions, even when rootFolders.Count == 0.
        // ScanOptionsUseCase.GetExtensionsForRootFolders will include root-level files.
        var selectedIgnoreOptions = GetSelectedIgnoreOptionIds();
        var ignoreRules = _buildIgnoreRules(path, selectedIgnoreOptions, rootFolders);
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsStalePathRequest(path)) return;

            // Scan extensions off the UI thread to avoid freezing on large folders.
            var scan = _scanOptions.GetExtensionsForRootFolders(path, rootFolders, ignoreRules, cancellationToken);
            if (scan.RootAccessDenied)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var elevated = await Dispatcher.UIThread.InvokeAsync(() => _tryElevateAndRestart(path));
                if (elevated) return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var visibleExtensions = new List<string>(scan.Value.Count);
            var hasExtensionlessEntries = SplitExtensions(scan.Value, visibleExtensions);
            var options = _filterSelectionService.BuildExtensionOptions(visibleExtensions, prev);
            options = ApplyMissingProfileSelectionsFallbackToExtensions(options);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (version != _extensionScanVersion) return;
                if (IsStalePathRequest(path)) return;
                ApplyExtensionOptions(options, hasExtensionlessEntries);
            });
        }, cancellationToken);
    }

    public Task PopulateRootFoldersAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path)) return Task.CompletedTask;
        if (IsStalePathRequest(path)) return Task.CompletedTask;
        var version = Interlocked.Increment(ref _rootScanVersion);

        var hasPreviousSelections = _rootSelectionInitialized;
        var prev = hasPreviousSelections
            ? new HashSet<string>(_rootSelectionCache, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var selectedIgnoreOptions = GetSelectedIgnoreOptionIds();
        var ignoreRules = _buildIgnoreRules(path, selectedIgnoreOptions, null);
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsStalePathRequest(path)) return;

            // Scan root folders off the UI thread to keep the window responsive.
            var scan = _scanOptions.Execute(new ScanOptionsRequest(path, ignoreRules), cancellationToken);
            if (scan.RootAccessDenied)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var elevated = await Dispatcher.UIThread.InvokeAsync(() => _tryElevateAndRestart(path));
                if (elevated) return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var options = _filterSelectionService.BuildRootFolderOptions(
                scan.RootFolders,
                prev,
                ignoreRules,
                hasPreviousSelections);
            options = ApplyMissingProfileSelectionsFallbackToRootFolders(options, scan.RootFolders, ignoreRules);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (version != _rootScanVersion) return;
                if (IsStalePathRequest(path)) return;
                _viewModel.RootFolders.Clear();

                _suppressRootItemCheck = true;
                foreach (var option in options)
                    _viewModel.RootFolders.Add(new SelectionOptionViewModel(option.Name, option.IsChecked));
                _suppressRootItemCheck = false;

                if (!ShouldSuppressAllTogglesOverride() && _viewModel.AllRootFoldersChecked)
                    SetAllChecked(_viewModel.RootFolders, true, ref _suppressRootItemCheck);

                SyncAllCheckbox(_viewModel.RootFolders, ref _suppressRootAllCheck,
                    value => _viewModel.AllRootFoldersChecked = value);
                UpdateRootSelectionCache();
                _rootSelectionInitialized = true;
            });
        }, cancellationToken);
    }

    public async Task PopulateIgnoreOptionsForRootSelectionAsync(
        IReadOnlyCollection<string> rootFolders,
        string? currentPath = null,
        CancellationToken cancellationToken = default)
    {
        var previousSelections = new HashSet<IgnoreOptionId>(_ignoreSelectionCache);
        var hasPreviousSelections = _ignoreSelectionInitialized;
        var path = string.IsNullOrWhiteSpace(currentPath) ? _currentPathProvider() : currentPath;
        if (!string.IsNullOrWhiteSpace(path) && IsStalePathRequest(path))
            return;
        var version = Interlocked.Increment(ref _ignoreOptionsVersion);

        var availability = await Task.Run(() => ResolveIgnoreOptionsAvailability(path, rootFolders), cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var options = _ignoreOptionsService.GetOptions(availability);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (version != _ignoreOptionsVersion)
                return;
            if (!string.IsNullOrWhiteSpace(path) && IsStalePathRequest(path))
                return;

            ApplyIgnoreOptions(options, previousSelections, hasPreviousSelections);
        });
    }

    public void PopulateIgnoreOptionsForRootSelection(
        IReadOnlyCollection<string> rootFolders,
        string? currentPath = null)
    {
        var previousSelections = new HashSet<IgnoreOptionId>(_ignoreSelectionCache);
        var hasPreviousSelections = _ignoreSelectionInitialized;
        var path = string.IsNullOrWhiteSpace(currentPath) ? _currentPathProvider() : currentPath;
        if (!string.IsNullOrWhiteSpace(path) && IsStalePathRequest(path))
            return;
        var availability = ResolveIgnoreOptionsAvailability(path, rootFolders);
        var options = _ignoreOptionsService.GetOptions(availability);

        ApplyIgnoreOptions(options, previousSelections, hasPreviousSelections);
    }

    public IReadOnlyCollection<string> GetSelectedRootFolders()
    {
        var selected = new List<string>(_viewModel.RootFolders.Count);
        foreach (var option in _viewModel.RootFolders)
        {
            if (option.IsChecked)
                selected.Add(option.Name);
        }

        return selected;
    }

    public void ApplyProjectProfileSelections(string projectPath, ProjectSelectionProfile profile)
    {
        _preparedSelectionPath = projectPath;
        _preparedSelectionMode = PreparedSelectionMode.Profile;

        _rootSelectionInitialized = true;
        _rootSelectionCache = new HashSet<string>(
            profile.SelectedRootFolders,
            StringComparer.OrdinalIgnoreCase);

        _extensionsSelectionInitialized = true;
        _extensionsSelectionCache = new HashSet<string>(
            profile.SelectedExtensions,
            StringComparer.OrdinalIgnoreCase);

        _ignoreSelectionInitialized = true;
        _ignoreSelectionCache = new HashSet<IgnoreOptionId>(profile.SelectedIgnoreOptions);
    }

    public void ResetProjectProfileSelections(string projectPath)
    {
        _preparedSelectionPath = projectPath;
        _preparedSelectionMode = PreparedSelectionMode.Defaults;

        // Restore defaults for projects without a saved profile.
        _viewModel.AllRootFoldersChecked = true;
        _viewModel.AllExtensionsChecked = true;
        _viewModel.AllIgnoreChecked = true;

        _rootSelectionInitialized = false;
        _rootSelectionCache.Clear();
        _rootSelectionCache.TrimExcess();

        _extensionsSelectionInitialized = false;
        _extensionsSelectionCache.Clear();
        _extensionsSelectionCache.TrimExcess();

        _ignoreSelectionInitialized = false;
        _ignoreSelectionCache.Clear();
        _ignoreSelectionCache.TrimExcess();
    }

    public async Task UpdateLiveOptionsFromRootSelectionAsync(
        string? currentPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(currentPath)) return;
        if (IsStalePathRequest(currentPath)) return;
        cancellationToken.ThrowIfCancellationRequested();

        var selectedRoots = GetSelectedRootFolders();
        await PopulateIgnoreOptionsForRootSelectionAsync(selectedRoots, currentPath, cancellationToken);
        await PopulateExtensionsForRootSelectionAsync(currentPath, selectedRoots, cancellationToken);
        await PopulateIgnoreOptionsForRootSelectionAsync(selectedRoots, currentPath, cancellationToken);
    }

    public async Task RefreshRootAndDependentsAsync(string currentPath, CancellationToken cancellationToken = default)
    {
        // Serialize refresh operations to prevent race conditions
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsStalePathRequest(currentPath) && !HasPreparedSelectionForPath(currentPath))
                return;

            // If another path is currently prepared (profile/default selections),
            // skip stale refresh requests for different paths. This prevents
            // unrelated background refreshes from clearing prepared selections.
            if (ShouldSkipRefreshForPreparedPath(currentPath))
                return;

            // Clear old caches when switching to another folder, unless the caller
            // explicitly prepared a profile for this exact target path.
            if (ShouldClearCachesForCurrentPath(currentPath))
            {
                ClearCachesForNewProject();
            }

            _lastLoadedPath = currentPath;

            // Warm ignore options first so root/extension scans use the latest ignore selection
            // without blocking UI on initial availability discovery.
            await PopulateIgnoreOptionsForRootSelectionAsync(Array.Empty<string>(), currentPath, cancellationToken);

            // Run in order so root folders are ready before extensions/ignore lists refresh.
            await PopulateRootFoldersAsync(currentPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var selectedRoots = GetSelectedRootFolders();
            await PopulateIgnoreOptionsForRootSelectionAsync(selectedRoots, currentPath, cancellationToken);
            await PopulateExtensionsForRootSelectionAsync(currentPath, selectedRoots, cancellationToken);
            await PopulateIgnoreOptionsForRootSelectionAsync(selectedRoots, currentPath, cancellationToken);

            // Consume prepared selection only after refresh for that exact path completes.
            if (HasPreparedSelectionForPath(currentPath))
            {
                _preparedSelectionPath = null;
                _preparedSelectionMode = PreparedSelectionMode.None;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task WaitForPendingRefreshesAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        _refreshLock.Release();
    }

    /// <summary>
    /// Clears internal caches when switching to a new project folder.
    /// This helps release memory from the previous project.
    /// </summary>
    private void ClearCachesForNewProject()
    {
        // Unsubscribe from old items before clearing to help GC
        UnsubscribeFromOptionItems();

        _rootSelectionCache.Clear();
        _rootSelectionCache.TrimExcess();
        _rootSelectionInitialized = false;

        // Clear extension selection cache
        _extensionsSelectionCache.Clear();
        _extensionsSelectionCache.TrimExcess();
        _extensionsSelectionInitialized = false;
        _hasExtensionlessExtensionEntries = false;

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

        var selected = CollectCheckedIgnoreIds(_viewModel.IgnoreOptions);

        _ignoreSelectionCache = selected;
        return selected;
    }

    private void EnsureIgnoreSelectionCache()
    {
        if (_ignoreSelectionInitialized || _ignoreSelectionCache.Count > 0)
            return;

        var path = _currentPathProvider() ?? _lastLoadedPath;
        var selectedRoots = GetSelectedRootFolders();
        var availability = ResolveIgnoreOptionsAvailability(path, selectedRoots);
        _ignoreOptions = _ignoreOptionsService.GetOptions(availability);
        var selected = new HashSet<IgnoreOptionId>();
        foreach (var option in _ignoreOptions)
        {
            if (option.DefaultChecked)
                selected.Add(option.Id);
        }

        _ignoreSelectionCache = selected;
    }

    private IgnoreOptionsAvailability ResolveIgnoreOptionsAvailability(
        string? path,
        IReadOnlyCollection<string> selectedRootFolders)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new IgnoreOptionsAvailability(IncludeGitIgnore: false, IncludeSmartIgnore: false);

        try
        {
            var availability = _getIgnoreOptionsAvailability(path, selectedRootFolders);
            if (_hasExtensionlessExtensionEntries)
                return availability with { IncludeExtensionlessFiles = true };

            return availability;
        }
        catch
        {
            return new IgnoreOptionsAvailability(IncludeGitIgnore: false, IncludeSmartIgnore: false);
        }
    }

    private void ApplyIgnoreOptions(
        IReadOnlyList<IgnoreOptionDescriptor> options,
        IReadOnlySet<IgnoreOptionId> previousSelections,
        bool hasPreviousSelections)
    {
        _suppressIgnoreItemCheck = true;
        try
        {
            _viewModel.IgnoreOptions.Clear();
            _ignoreOptions = options;

            var useDefaultCheckedFallback = ShouldUseIgnoreDefaultFallback(options, previousSelections);
            foreach (var option in _ignoreOptions)
            {
                var isChecked = useDefaultCheckedFallback
                    ? option.DefaultChecked
                    : previousSelections.Contains(option.Id) ||
                      (!hasPreviousSelections && option.DefaultChecked);
                _viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(option.Id, option.Label, isChecked));
            }
        }
        finally
        {
            _suppressIgnoreItemCheck = false;
        }

        if (!ShouldSuppressAllTogglesOverride() && _viewModel.AllIgnoreChecked)
            SetAllChecked(_viewModel.IgnoreOptions, true, ref _suppressIgnoreItemCheck);

        UpdateIgnoreSelectionCache();
        SyncIgnoreAllCheckbox();
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
        _extensionsSelectionCache = CollectCheckedSelectionNames(_viewModel.Extensions);
    }

    internal void ApplyExtensionScan(IReadOnlyCollection<string> extensions)
    {
        var visibleExtensions = new List<string>(extensions.Count);
        var hasExtensionlessEntries = SplitExtensions(extensions, visibleExtensions);
        var prev = _extensionsSelectionInitialized
            ? new HashSet<string>(_extensionsSelectionCache, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var options = _filterSelectionService.BuildExtensionOptions(visibleExtensions, prev);
        options = ApplyMissingProfileSelectionsFallbackToExtensions(options);
        ApplyExtensionOptions(options, hasExtensionlessEntries);
    }

    public void UpdateIgnoreSelectionCache()
    {
        if (_ignoreOptions.Count == 0 || _viewModel.IgnoreOptions.Count == 0)
            return;

        _ignoreSelectionCache = CollectCheckedIgnoreIds(_viewModel.IgnoreOptions);
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

            _rootSelectionInitialized = true;
            SyncAllCheckbox(_viewModel.RootFolders, ref _suppressRootAllCheck,
                value => _viewModel.AllRootFoldersChecked = value);
            UpdateRootSelectionCache();

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

    private void ApplyExtensionOptions(IReadOnlyList<SelectionOption> options, bool hasExtensionlessEntries)
    {
        _viewModel.Extensions.Clear();
        var keepExtensionlessAvailability = IsExtensionlessIgnoreEnabled();
        _hasExtensionlessExtensionEntries = keepExtensionlessAvailability || hasExtensionlessEntries;

        _suppressExtensionItemCheck = true;
        foreach (var option in options)
            _viewModel.Extensions.Add(new SelectionOptionViewModel(option.Name, option.IsChecked));
        _suppressExtensionItemCheck = false;

        if (!ShouldSuppressAllTogglesOverride() && _viewModel.AllExtensionsChecked)
            SetAllChecked(_viewModel.Extensions, true, ref _suppressExtensionItemCheck);

        SyncAllCheckbox(_viewModel.Extensions, ref _suppressExtensionAllCheck,
            value => _viewModel.AllExtensionsChecked = value);
        if (!_extensionsSelectionInitialized)
        {
            _extensionsSelectionInitialized = true;
            UpdateExtensionsSelectionCache();
        }
    }

    private static HashSet<string> CollectCheckedSelectionNames(IEnumerable<SelectionOptionViewModel> options)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options)
        {
            if (option.IsChecked)
                selected.Add(option.Name);
        }

        return selected;
    }

    private static HashSet<IgnoreOptionId> CollectCheckedIgnoreIds(IEnumerable<IgnoreOptionViewModel> options)
    {
        var selected = new HashSet<IgnoreOptionId>();
        foreach (var option in options)
        {
            if (option.IsChecked)
                selected.Add(option.Id);
        }

        return selected;
    }

    private static bool SplitExtensions(IReadOnlyCollection<string> source, ICollection<string> visibleExtensions)
    {
        var hasExtensionlessEntries = false;
        foreach (var entry in source)
        {
            if (IsExtensionlessEntry(entry))
            {
                hasExtensionlessEntries = true;
                continue;
            }

            visibleExtensions.Add(entry);
        }

        return hasExtensionlessEntries;
    }

    private bool IsExtensionlessIgnoreEnabled()
    {
        foreach (var option in _viewModel.IgnoreOptions)
        {
            if (option.Id == IgnoreOptionId.ExtensionlessFiles && option.IsChecked)
                return true;
        }

        return false;
    }

    private static bool IsExtensionlessEntry(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var extension = Path.GetExtension(value);
        return string.IsNullOrEmpty(extension) || extension == ".";
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

    /// <summary>
    /// Disposes all event subscriptions and releases resources to prevent memory leaks.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unsubscribe from collection change events
        if (_hookedRootFolders is not null && _rootFoldersCollectionChangedHandler is not null)
            _hookedRootFolders.CollectionChanged -= _rootFoldersCollectionChangedHandler;

        if (_hookedExtensions is not null && _extensionsCollectionChangedHandler is not null)
            _hookedExtensions.CollectionChanged -= _extensionsCollectionChangedHandler;

        if (_hookedIgnoreOptions is not null && _ignoreOptionsCollectionChangedHandler is not null)
            _hookedIgnoreOptions.CollectionChanged -= _ignoreOptionsCollectionChangedHandler;

        // Unsubscribe from all individual item events
        UnsubscribeFromOptionItems();

        // Clear caches
        _rootSelectionCache.Clear();
        _ignoreSelectionCache.Clear();
        _extensionsSelectionCache.Clear();
        _preparedSelectionPath = null;
        _ignoreOptions = Array.Empty<IgnoreOptionDescriptor>();

        // Dispose the semaphore
        _refreshLock.Dispose();
    }

    private bool ShouldClearCachesForCurrentPath(string currentPath)
    {
        var isPathSwitch = _lastLoadedPath is not null && !PathComparer.Default.Equals(_lastLoadedPath, currentPath);
        var hasPreparedSelectionForCurrentPath = HasPreparedSelectionForPath(currentPath);
        return isPathSwitch && !hasPreparedSelectionForCurrentPath;
    }

    private bool HasPreparedSelectionForPath(string path)
    {
        return _preparedSelectionPath is not null &&
               PathComparer.Default.Equals(_preparedSelectionPath, path);
    }

    private bool ShouldSkipRefreshForPreparedPath(string currentPath)
    {
        return _preparedSelectionPath is not null &&
               !PathComparer.Default.Equals(_preparedSelectionPath, currentPath);
    }

    private bool IsStalePathRequest(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var currentPath = _currentPathProvider();
        if (string.IsNullOrWhiteSpace(currentPath))
            return false;

        return !PathComparer.Default.Equals(currentPath, path);
    }

    private bool ShouldSuppressAllTogglesOverride()
    {
        return _preparedSelectionMode == PreparedSelectionMode.Profile;
    }

    private IReadOnlyList<SelectionOption> ApplyMissingProfileSelectionsFallbackToExtensions(
        IReadOnlyList<SelectionOption> options)
    {
        if (!ShouldSuppressAllTogglesOverride())
            return options;
        if (_extensionsSelectionCache.Count == 0 || options.Count == 0)
            return options;

        var hasAnyMatchedSelection = false;
        foreach (var option in options)
        {
            if (option.IsChecked)
            {
                hasAnyMatchedSelection = true;
                break;
            }
        }

        if (hasAnyMatchedSelection)
            return options;

        // Saved extension selections exist, but none are available in current scan.
        // Fall back to current defaults instead of forcing all current options unchecked.
        var fallback = new List<SelectionOption>(options.Count);
        foreach (var option in options)
            fallback.Add(option with { IsChecked = true });
        return fallback;
    }

    private IReadOnlyList<SelectionOption> ApplyMissingProfileSelectionsFallbackToRootFolders(
        IReadOnlyList<SelectionOption> options,
        IReadOnlyList<string> scannedRootFolders,
        IgnoreRules ignoreRules)
    {
        if (!ShouldSuppressAllTogglesOverride())
            return options;
        if (_rootSelectionCache.Count == 0 || options.Count == 0)
            return options;

        var hasAnyMatchedSelection = false;
        foreach (var option in options)
        {
            if (option.IsChecked)
            {
                hasAnyMatchedSelection = true;
                break;
            }
        }

        if (hasAnyMatchedSelection)
            return options;

        // Saved root folder selections exist, but all of them are absent now.
        // Recompute with default behavior for currently available roots.
        return _filterSelectionService.BuildRootFolderOptions(
            scannedRootFolders,
            EmptyStringSet,
            ignoreRules,
            hasPreviousSelections: false);
    }

    private bool ShouldUseIgnoreDefaultFallback(
        IReadOnlyList<IgnoreOptionDescriptor> options,
        IReadOnlySet<IgnoreOptionId> previousSelections)
    {
        if (!ShouldSuppressAllTogglesOverride())
            return false;
        if (previousSelections.Count == 0 || options.Count == 0)
            return false;

        foreach (var option in options)
        {
            if (previousSelections.Contains(option.Id))
                return false;
        }

        // Saved ignore selections exist, but none of those options are currently available.
        // Use current default states for visible ignore options.
        return true;
    }

    private void UpdateRootSelectionCache()
    {
        if (_viewModel.RootFolders.Count == 0)
        {
            _rootSelectionCache.Clear();
            _rootSelectionCache.TrimExcess();
            return;
        }

        _rootSelectionCache = CollectCheckedSelectionNames(_viewModel.RootFolders);
    }
}
