using Avalonia.Threading;
using DevProjex.Application.Models;
using DevProjex.Avalonia.Collections;
using DevProjex.Kernel;

namespace DevProjex.Avalonia.Coordinators;

public sealed class SelectionSyncCoordinator(
    MainWindowViewModel viewModel,
    ScanOptionsUseCase scanOptions,
    FilterOptionSelectionService filterSelectionService,
    IgnoreOptionsService ignoreOptionsService,
    Func<string, IReadOnlyCollection<IgnoreOptionId>, IReadOnlyCollection<string>?, IgnoreRules> buildIgnoreRules,
    Func<string, IReadOnlyCollection<string>, IgnoreOptionsAvailability> getIgnoreOptionsAvailability,
    Func<string, bool> tryElevateAndRestart,
    Func<string?> currentPathProvider)
    : IDisposable
{
    // Store collection references for proper cleanup
    private ObservableCollection<SelectionOptionViewModel>? _hookedRootFolders;
    private ObservableCollection<SelectionOptionViewModel>? _hookedExtensions;
    private ObservableCollection<IgnoreOptionViewModel>? _hookedIgnoreOptions;

    // Named handlers for proper unsubscription
    private NotifyCollectionChangedEventHandler? _rootFoldersCollectionChangedHandler;
    private NotifyCollectionChangedEventHandler? _extensionsCollectionChangedHandler;
    private NotifyCollectionChangedEventHandler? _ignoreOptionsCollectionChangedHandler;

    private bool _disposed;
    private static readonly HashSet<string> EmptyStringSet = new(PathComparer.Default);

    private IReadOnlyList<IgnoreOptionDescriptor> _ignoreOptions = [];
    private HashSet<IgnoreOptionId> _ignoreSelectionCache = [];
    // Persist dynamic ignore-option state independently from the current visible list.
    private Dictionary<IgnoreOptionId, bool> _ignoreOptionStateCache = [];
    private bool _ignoreSelectionInitialized;
    private bool? _ignoreAllPreference;
    private HashSet<string> _rootSelectionCache = new(PathComparer.Default);
    private bool _rootSelectionInitialized;
    private HashSet<string> _extensionsSelectionCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _extensionsSelectionInitialized;
    private bool _hasExtensionlessExtensionEntries;
    private int _extensionlessExtensionEntriesCount;
    private bool _hasIgnoreOptionCounts;
    private IgnoreOptionCounts _ignoreOptionCounts;
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
    private readonly object _backgroundRefreshSync = new();
    private CancellationTokenSource? _liveOptionsRefreshCts;
    private CancellationTokenSource? _fullRefreshRequestCts;
    private Task _latestLiveOptionsRefreshTask = Task.CompletedTask;
    private Task _latestFullRefreshTask = Task.CompletedTask;
    private int _liveOptionsRequestVersion;
    private int _fullRefreshRequestVersion;
    private readonly object _ignoreRulesBuildCacheSync = new();
    private IgnoreRulesBuildCacheEntry? _ignoreRulesBuildCache;
    private readonly SelectionRefreshEngine _selectionRefreshEngine = new(
        scanOptions,
        filterSelectionService,
        ignoreOptionsService,
        buildIgnoreRules,
        getIgnoreOptionsAvailability);

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
        viewModel.AllRootFoldersChecked = isChecked;
        _suppressRootAllCheck = false;

        SetAllChecked(viewModel.RootFolders, isChecked, ref _suppressRootItemCheck);
        UpdateRootSelectionCache();
        QueueLiveOptionsRefresh(currentPath);
    }

    public void HandleExtensionsAllChanged(bool isChecked)
    {
        if (_suppressExtensionAllCheck) return;

        _extensionsSelectionInitialized = true;
        _suppressExtensionAllCheck = true;
        viewModel.AllExtensionsChecked = isChecked;
        _suppressExtensionAllCheck = false;

        SetAllChecked(viewModel.Extensions, isChecked, ref _suppressExtensionItemCheck);
        UpdateExtensionsSelectionCache();

        // Bulk extension toggles suppress individual item events, so refresh live
        // ignore counts explicitly to keep EmptyFolders aligned with tree semantics.
        QueueLiveOptionsRefresh(currentPathProvider());
    }

    public void HandleIgnoreAllChanged(bool isChecked, string? currentPath)
    {
        if (_suppressIgnoreAllCheck) return;

        _ignoreSelectionInitialized = true;
        _ignoreAllPreference = isChecked;
        ApplyIgnoreAllPreferenceToKnownStates(isChecked);

        _suppressIgnoreAllCheck = true;
        viewModel.AllIgnoreChecked = isChecked;
        _suppressIgnoreAllCheck = false;

        SetAllChecked(viewModel.IgnoreOptions, isChecked, ref _suppressIgnoreItemCheck);
        UpdateIgnoreSelectionCache();
        if (!string.IsNullOrEmpty(currentPath))
        {
            QueueFullRefresh(currentPath);
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
        var ignoreRules = GetOrBuildIgnoreRules(path, selectedIgnoreOptions, rootFolders);
        var extensionScanRules = BuildExtensionAvailabilityScanRules(ignoreRules);
        var forceAllExtensionsChecked = !ShouldSuppressAllTogglesOverride() && viewModel.AllExtensionsChecked;
        var effectiveAllowedExtensions = BuildEffectiveAllowedExtensionsForLiveCounts(forceAllExtensionsChecked);
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsStalePathRequest(path)) return;

            // The live ignore section needs extension availability and effective counts to come
            // from the same snapshot. Keeping them coupled removes a whole extra filesystem pass
            // and prevents the coordinator from stitching together mismatched intermediate states.
            var scan = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
                path,
                rootFolders,
                extensionScanRules,
                ignoreRules,
                effectiveAllowedExtensions,
                includeDirectoryToggleProbeRoots: !ShouldSuppressAllTogglesOverride() && viewModel.AllRootFoldersChecked,
                cancellationToken);
            if (scan.RootAccessDenied)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var elevated = await Dispatcher.UIThread.InvokeAsync(() => tryElevateAndRestart(path));
                if (elevated) return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var visibleExtensions = new List<string>(scan.Value.Extensions.Count);
            var extensionlessEntriesCount = SplitExtensions(scan.Value.Extensions, visibleExtensions);
            var options = filterSelectionService.BuildExtensionOptions(visibleExtensions, prev);
            options = ApplyMissingProfileSelectionsFallbackToExtensions(options);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (version != _extensionScanVersion) return;
                if (IsStalePathRequest(path)) return;
                ApplyExtensionOptions(
                    options,
                    extensionlessEntriesCount,
                    scan.Value.EffectiveIgnoreOptionCounts,
                    hasIgnoreOptionCounts: true);
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
            ? new HashSet<string>(_rootSelectionCache, PathComparer.Default)
            : new HashSet<string>(PathComparer.Default);

        var selectedIgnoreOptions = GetSelectedIgnoreOptionIds();
        var ignoreRules = GetOrBuildIgnoreRules(path, selectedIgnoreOptions, null);
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsStalePathRequest(path)) return;

            // Root folder list does not require full extension scan.
            var scan = scanOptions.GetRootFolders(path, ignoreRules, cancellationToken);
            if (scan.RootAccessDenied)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var elevated = await Dispatcher.UIThread.InvokeAsync(() => tryElevateAndRestart(path));
                if (elevated) return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var options = filterSelectionService.BuildRootFolderOptions(
                scan.Value,
                prev,
                ignoreRules,
                hasPreviousSelections);
            options = ApplyMissingProfileSelectionsFallbackToRootFolders(options, scan.Value, ignoreRules);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (version != _rootScanVersion) return;
                if (IsStalePathRequest(path)) return;
                var optionViewModels = new List<SelectionOptionViewModel>(options.Count);
                foreach (var option in options)
                    optionViewModels.Add(new SelectionOptionViewModel(option.Name, option.IsChecked));

                _suppressRootItemCheck = true;
                ReplaceCollectionItems(viewModel.RootFolders, optionViewModels);
                _suppressRootItemCheck = false;

                if (!ShouldSuppressAllTogglesOverride() && viewModel.AllRootFoldersChecked)
                    SetAllChecked(viewModel.RootFolders, true, ref _suppressRootItemCheck);

                SyncAllCheckbox(viewModel.RootFolders, ref _suppressRootAllCheck,
                    value => viewModel.AllRootFoldersChecked = value);
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
        var path = string.IsNullOrWhiteSpace(currentPath) ? currentPathProvider() : currentPath;
        if (!string.IsNullOrWhiteSpace(path) && IsStalePathRequest(path))
            return;
        var version = Interlocked.Increment(ref _ignoreOptionsVersion);

        var availability = await Task.Run(() => ResolveIgnoreOptionsAvailability(path, rootFolders), cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var options = ignoreOptionsService.GetOptions(availability);

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
        var path = string.IsNullOrWhiteSpace(currentPath) ? currentPathProvider() : currentPath;
        if (!string.IsNullOrWhiteSpace(path) && IsStalePathRequest(path))
            return;
        var availability = ResolveIgnoreOptionsAvailability(path, rootFolders);
        var options = ignoreOptionsService.GetOptions(availability);

        ApplyIgnoreOptions(options, previousSelections, hasPreviousSelections);
    }

    public void RefreshIgnoreOptionsForCurrentSelection(string? currentPath = null)
    {
        var path = string.IsNullOrWhiteSpace(currentPath) ? currentPathProvider() : currentPath;
        var selectedRoots = GetSelectedRootFolders();
        var previousSelections = new HashSet<IgnoreOptionId>(_ignoreSelectionCache);
        var hasPreviousSelections = _ignoreSelectionInitialized;
        var availability = ResolveIgnoreOptionsAvailability(path, selectedRoots);
        var options = ignoreOptionsService.GetOptions(availability);
        ApplyIgnoreOptions(options, previousSelections, hasPreviousSelections);
    }

    public IReadOnlyCollection<string> GetSelectedRootFolders()
    {
        var selected = new List<string>(viewModel.RootFolders.Count);
        foreach (var option in viewModel.RootFolders)
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
            PathComparer.Default);

        _extensionsSelectionInitialized = true;
        _extensionsSelectionCache = new HashSet<string>(
            profile.SelectedExtensions,
            StringComparer.OrdinalIgnoreCase);

        _ignoreAllPreference = null;
        _ignoreSelectionInitialized = true;
        _ignoreSelectionCache = new HashSet<IgnoreOptionId>(profile.SelectedIgnoreOptions);
        _ignoreOptionStateCache = [];
        foreach (var id in _ignoreSelectionCache)
            _ignoreOptionStateCache[id] = true;
    }

    public void ResetProjectProfileSelections(string projectPath)
    {
        _preparedSelectionPath = projectPath;
        _preparedSelectionMode = PreparedSelectionMode.Defaults;

        // Restore defaults for projects without a saved profile.
        viewModel.AllRootFoldersChecked = true;
        viewModel.AllExtensionsChecked = true;
        viewModel.AllIgnoreChecked = true;

        _rootSelectionInitialized = false;
        _rootSelectionCache.Clear();
        _rootSelectionCache.TrimExcess();

        _extensionsSelectionInitialized = false;
        _extensionsSelectionCache.Clear();
        _extensionsSelectionCache.TrimExcess();

        _ignoreAllPreference = null;
        _ignoreSelectionInitialized = false;
        _ignoreSelectionCache.Clear();
        _ignoreSelectionCache.TrimExcess();
        _ignoreOptionStateCache.Clear();
    }

    public async Task UpdateLiveOptionsFromRootSelectionAsync(
        string? currentPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(currentPath)) return;
        if (IsStalePathRequest(currentPath)) return;
        cancellationToken.ThrowIfCancellationRequested();

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsStalePathRequest(currentPath))
                return;

            var liveInput = await RunOnUiThreadAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsStalePathRequest(currentPath))
                    return null;

                return new LiveRefreshInput(
                    CreateSelectionRefreshContext(currentPath),
                    GetSelectedRootFolders());
            });
            if (liveInput is null)
                return;

            var snapshot = await Task.Run(
                () => _selectionRefreshEngine.ComputeLiveRefreshSnapshot(
                    liveInput.Context,
                    liveInput.SelectedRoots,
                    cancellationToken),
                cancellationToken);
            if (snapshot.RootAccessDenied)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var elevated = await Dispatcher.UIThread.InvokeAsync(() => tryElevateAndRestart(currentPath));
                if (elevated)
                    return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsStalePathRequest(currentPath))
                    return;

                ApplySelectionRefreshSnapshot(snapshot);
            });
        }
        finally
        {
            _refreshLock.Release();
        }
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

            var context = await RunOnUiThreadAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsStalePathRequest(currentPath) && !HasPreparedSelectionForPath(currentPath))
                    return null;
                if (ShouldSkipRefreshForPreparedPath(currentPath))
                    return null;

                // UI collections and selection caches are Avalonia-owned state. Capture and
                // cache reset must happen on the UI thread; the expensive scan runs after this.
                if (ShouldClearCachesForCurrentPath(currentPath))
                    ClearCachesForNewProject();

                _lastLoadedPath = currentPath;
                return CreateSelectionRefreshContext(currentPath);
            });
            if (context is null)
                return;

            var snapshot = await Task.Run(
                () => _selectionRefreshEngine.ComputeFullRefreshSnapshot(context, cancellationToken),
                cancellationToken);
            if (snapshot.RootAccessDenied)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var elevated = await Dispatcher.UIThread.InvokeAsync(() => tryElevateAndRestart(currentPath));
                if (elevated)
                    return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsStalePathRequest(currentPath) && !HasPreparedSelectionForPath(currentPath))
                    return;
                if (ShouldSkipRefreshForPreparedPath(currentPath))
                    return;

                ApplySelectionRefreshSnapshot(snapshot);
            });

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
        while (true)
        {
            await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            _refreshLock.Release();

            Task liveTask;
            Task fullTask;
            lock (_backgroundRefreshSync)
            {
                liveTask = _latestLiveOptionsRefreshTask;
                fullTask = _latestFullRefreshTask;
            }

            // The UI can queue a live-options refresh and a full refresh back-to-back.
            // Waiting only on the refresh lock is not sufficient because the coalesced
            // background tasks run outside that lock. Tests and Apply must observe the
            // fully converged snapshot, not an intermediate state between these phases.
            await AwaitBackgroundRefreshTaskAsync(liveTask, cancellationToken).ConfigureAwait(false);
            await AwaitBackgroundRefreshTaskAsync(fullTask, cancellationToken).ConfigureAwait(false);

            lock (_backgroundRefreshSync)
            {
                if (ReferenceEquals(liveTask, _latestLiveOptionsRefreshTask) &&
                    ReferenceEquals(fullTask, _latestFullRefreshTask) &&
                    liveTask.IsCompleted &&
                    fullTask.IsCompleted)
                {
                    return;
                }
            }
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

        _rootSelectionCache.Clear();
        _rootSelectionCache.TrimExcess();
        _rootSelectionInitialized = false;

        // Clear extension selection cache
        _extensionsSelectionCache.Clear();
        _extensionsSelectionCache.TrimExcess();
        _extensionsSelectionInitialized = false;
        _hasExtensionlessExtensionEntries = false;
        _extensionlessExtensionEntriesCount = 0;
        _hasIgnoreOptionCounts = false;
        _ignoreOptionCounts = IgnoreOptionCounts.Empty;

        // Clear ignore selection cache
        _ignoreAllPreference = null;
        _ignoreSelectionCache.Clear();
        _ignoreSelectionCache.TrimExcess();
        _ignoreOptionStateCache.Clear();
        _ignoreSelectionInitialized = false;

        // Clear ignore options
        _ignoreOptions = [];
        lock (_ignoreRulesBuildCacheSync)
            _ignoreRulesBuildCache = null;
    }

    /// <summary>
    /// Unsubscribes from CheckedChanged events on all option items.
    /// </summary>
    private void UnsubscribeFromOptionItems()
    {
        foreach (var item in viewModel.RootFolders)
            item.CheckedChanged -= OnOptionCheckedChanged;

        foreach (var item in viewModel.Extensions)
            item.CheckedChanged -= OnOptionCheckedChanged;

        foreach (var item in viewModel.IgnoreOptions)
            item.CheckedChanged -= OnIgnoreCheckedChanged;
    }

    public IReadOnlyCollection<IgnoreOptionId> GetSelectedIgnoreOptionIds()
    {
        EnsureIgnoreSelectionCache();
        UpdateIgnoreSelectionCache();
        return _ignoreSelectionCache;
    }

    private void EnsureIgnoreSelectionCache()
    {
        if (_ignoreSelectionInitialized || _ignoreSelectionCache.Count > 0)
            return;

        var path = currentPathProvider() ?? _lastLoadedPath;
        var selectedRoots = GetSelectedRootFolders();
        var availability = ResolveIgnoreOptionsAvailability(path, selectedRoots);
        _ignoreOptions = ignoreOptionsService.GetOptions(availability);
        _ignoreOptionStateCache = [];
        foreach (var option in _ignoreOptions)
            _ignoreOptionStateCache[option.Id] = option.DefaultChecked;

        _ignoreSelectionCache = BuildSelectedIgnoreOptionSet();
    }

    private IgnoreOptionsAvailability ResolveIgnoreOptionsAvailability(
        string? path,
        IReadOnlyCollection<string> selectedRootFolders)
    {
        if (string.IsNullOrWhiteSpace(path))
            return CreateCountDrivenIgnoreAvailability(includeGitIgnore: false, includeSmartIgnore: false);

        try
        {
            var availability = CreateCountDrivenIgnoreAvailability(getIgnoreOptionsAvailability(path, selectedRootFolders));
            if (_hasIgnoreOptionCounts)
            {
                return availability with
                {
                    IncludeHiddenFolders = _ignoreOptionCounts.HiddenFolders > 0,
                    HiddenFoldersCount = _ignoreOptionCounts.HiddenFolders,
                    IncludeHiddenFiles = _ignoreOptionCounts.HiddenFiles > 0,
                    HiddenFilesCount = _ignoreOptionCounts.HiddenFiles,
                    IncludeDotFolders = _ignoreOptionCounts.DotFolders > 0,
                    DotFoldersCount = _ignoreOptionCounts.DotFolders,
                    IncludeDotFiles = _ignoreOptionCounts.DotFiles > 0,
                    DotFilesCount = _ignoreOptionCounts.DotFiles,
                    IncludeEmptyFolders = _ignoreOptionCounts.EmptyFolders > 0,
                    EmptyFoldersCount = _ignoreOptionCounts.EmptyFolders,
                    IncludeEmptyFiles = _ignoreOptionCounts.EmptyFiles > 0,
                    EmptyFilesCount = _ignoreOptionCounts.EmptyFiles,
                    IncludeExtensionlessFiles = _ignoreOptionCounts.ExtensionlessFiles > 0,
                    ExtensionlessFilesCount = _ignoreOptionCounts.ExtensionlessFiles
                };
            }

            if (_hasExtensionlessExtensionEntries)
            {
                return availability with
                {
                    IncludeExtensionlessFiles = true,
                    ExtensionlessFilesCount = _extensionlessExtensionEntriesCount
                };
            }

            return availability;
        }
        catch
        {
            return CreateCountDrivenIgnoreAvailability(includeGitIgnore: false, includeSmartIgnore: false);
        }
    }

    private static IgnoreOptionsAvailability CreateCountDrivenIgnoreAvailability(
        bool includeGitIgnore,
        bool includeSmartIgnore)
    {
        return new IgnoreOptionsAvailability(
            IncludeGitIgnore: includeGitIgnore,
            IncludeSmartIgnore: includeSmartIgnore);
    }

    private static IgnoreOptionsAvailability CreateCountDrivenIgnoreAvailability(
        IgnoreOptionsAvailability availability)
    {
        // Advanced ignore options are driven by live counts coming from the scan layer.
        // Keep them hidden until the coordinator has computed those values.
        return availability with
        {
            IncludeHiddenFolders = false,
            HiddenFoldersCount = 0,
            IncludeHiddenFiles = false,
            HiddenFilesCount = 0,
            IncludeDotFolders = false,
            DotFoldersCount = 0,
            IncludeDotFiles = false,
            DotFilesCount = 0,
            IncludeEmptyFolders = false,
            EmptyFoldersCount = 0,
            IncludeEmptyFiles = false,
            EmptyFilesCount = 0,
            IncludeExtensionlessFiles = false,
            ExtensionlessFilesCount = 0
        };
    }

    private void ApplyIgnoreOptions(
        IReadOnlyList<IgnoreOptionDescriptor> options,
        IReadOnlySet<IgnoreOptionId> previousSelections,
        bool hasPreviousSelections)
    {
        var useDefaultCheckedFallback = ShouldUseIgnoreDefaultFallback(options, previousSelections);
        var optionViewModels = new List<IgnoreOptionViewModel>(options.Count);
        foreach (var option in options)
        {
            var isChecked = ResolveIgnoreOptionCheckedState(
                option,
                previousSelections,
                hasPreviousSelections,
                useDefaultCheckedFallback);
            optionViewModels.Add(new IgnoreOptionViewModel(option.Id, option.Label, isChecked));
        }

        _suppressIgnoreItemCheck = true;
        try
        {
            _ignoreOptions = options;
            ReplaceCollectionItems(viewModel.IgnoreOptions, optionViewModels);
        }
        finally
        {
            _suppressIgnoreItemCheck = false;
        }

        UpdateIgnoreSelectionCache(hasPreviousSelections ? previousSelections : null);
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
        if (viewModel.Extensions.Count == 0)
            return;

        _extensionsSelectionInitialized = true;
        _extensionsSelectionCache = CollectCheckedSelectionNames(viewModel.Extensions, StringComparer.OrdinalIgnoreCase);
    }

    internal void ApplyExtensionScan(IReadOnlyCollection<string> extensions)
    {
        var visibleExtensions = new List<string>(extensions.Count);
        var extensionlessEntriesCount = SplitExtensions(extensions, visibleExtensions);
        var prev = _extensionsSelectionInitialized
            ? new HashSet<string>(_extensionsSelectionCache, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var options = filterSelectionService.BuildExtensionOptions(visibleExtensions, prev);
        options = ApplyMissingProfileSelectionsFallbackToExtensions(options);
        ApplyExtensionOptions(
            options,
            extensionlessEntriesCount,
            IgnoreOptionCounts.Empty,
            hasIgnoreOptionCounts: false);
    }

    public void UpdateIgnoreSelectionCache(IReadOnlySet<IgnoreOptionId>? preserveMissingFrom = null)
    {
        if (preserveMissingFrom is not null && preserveMissingFrom.Count > 0)
            PreserveMissingIgnoreSelections(preserveMissingFrom);

        foreach (var option in viewModel.IgnoreOptions)
            _ignoreOptionStateCache[option.Id] = option.IsChecked;

        _ignoreSelectionCache = BuildSelectedIgnoreOptionSet();
    }

    public void SyncIgnoreAllCheckbox()
    {
        SyncAllCheckbox(viewModel.IgnoreOptions, ref _suppressIgnoreAllCheck,
            value => viewModel.AllIgnoreChecked = value);
    }

    private void OnOptionCheckedChanged(object? sender, EventArgs e)
    {
        if (sender is not SelectionOptionViewModel option)
            return;

        if (viewModel.RootFolders.Contains(option))
        {
            if (_suppressRootItemCheck) return;

            _rootSelectionInitialized = true;
            SyncAllCheckbox(viewModel.RootFolders, ref _suppressRootAllCheck,
                value => viewModel.AllRootFoldersChecked = value);
            UpdateRootSelectionCache();

            QueueLiveOptionsRefresh(currentPathProvider());
        }
        else if (viewModel.Extensions.Contains(option))
        {
            if (_suppressExtensionItemCheck) return;

            _extensionsSelectionInitialized = true;
            SyncAllCheckbox(viewModel.Extensions, ref _suppressExtensionAllCheck,
                value => viewModel.AllExtensionsChecked = value);

            UpdateExtensionsSelectionCache();
            QueueLiveOptionsRefresh(currentPathProvider());
        }
    }

    private void OnIgnoreCheckedChanged(object? sender, EventArgs e)
    {
        if (_suppressIgnoreItemCheck) return;

        _ignoreSelectionInitialized = true;
        _ignoreAllPreference = null;

        SyncAllCheckbox(viewModel.IgnoreOptions, ref _suppressIgnoreAllCheck,
            value => viewModel.AllIgnoreChecked = value);

        UpdateIgnoreSelectionCache();

        var currentPath = currentPathProvider();
        if (!string.IsNullOrEmpty(currentPath))
        {
            QueueFullRefresh(currentPath);
        }
    }

    /// <summary>
    /// Coalesces rapid root-selection changes and keeps only the latest live-options refresh.
    /// </summary>
    private void QueueLiveOptionsRefresh(string? currentPath)
    {
        if (string.IsNullOrWhiteSpace(currentPath))
            return;

        CancellationToken token;
        int version;
        lock (_backgroundRefreshSync)
        {
            _liveOptionsRefreshCts?.Cancel();
            _liveOptionsRefreshCts?.Dispose();
              _liveOptionsRefreshCts = new CancellationTokenSource();
              token = _liveOptionsRefreshCts.Token;
              version = unchecked(++_liveOptionsRequestVersion);
              _latestLiveOptionsRefreshTask = RunQueuedLiveOptionsRefreshAsync(currentPath, version, token);
          }

        FireAndForgetSafe(_latestLiveOptionsRefreshTask);
    }

    /// <summary>
    /// Coalesces rapid ignore-option changes and keeps only the latest full refresh request.
    /// </summary>
    private void QueueFullRefresh(string? currentPath)
    {
        if (string.IsNullOrWhiteSpace(currentPath))
            return;

        CancellationToken token;
        int version;
        lock (_backgroundRefreshSync)
        {
            _fullRefreshRequestCts?.Cancel();
            _fullRefreshRequestCts?.Dispose();
              _fullRefreshRequestCts = new CancellationTokenSource();
              token = _fullRefreshRequestCts.Token;
              version = unchecked(++_fullRefreshRequestVersion);
              _latestFullRefreshTask = RunQueuedFullRefreshAsync(currentPath, version, token);
          }

        FireAndForgetSafe(_latestFullRefreshTask);
    }

    private async Task RunQueuedLiveOptionsRefreshAsync(
        string currentPath,
        int version,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;
        if (version != Volatile.Read(ref _liveOptionsRequestVersion))
            return;

        await UpdateLiveOptionsFromRootSelectionAsync(currentPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunQueuedFullRefreshAsync(
        string currentPath,
        int version,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;
        if (version != Volatile.Read(ref _fullRefreshRequestVersion))
            return;

        await RefreshRootAndDependentsAsync(currentPath, cancellationToken).ConfigureAwait(false);
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

    private void ApplyExtensionOptions(
        IReadOnlyList<SelectionOption> options,
        int extensionlessEntriesCount,
        IgnoreOptionCounts ignoreOptionCounts,
        bool hasIgnoreOptionCounts)
    {
        var optionViewModels = new List<SelectionOptionViewModel>(options.Count);
        foreach (var option in options)
            optionViewModels.Add(new SelectionOptionViewModel(option.Name, option.IsChecked));

        var effectiveExtensionlessCount = hasIgnoreOptionCounts
            ? ignoreOptionCounts.ExtensionlessFiles
            : extensionlessEntriesCount;

        _extensionlessExtensionEntriesCount = effectiveExtensionlessCount;
        _hasExtensionlessExtensionEntries = effectiveExtensionlessCount > 0;
        _ignoreOptionCounts = ignoreOptionCounts;
        _hasIgnoreOptionCounts = hasIgnoreOptionCounts;

        _suppressExtensionItemCheck = true;
        ReplaceCollectionItems(viewModel.Extensions, optionViewModels);
        _suppressExtensionItemCheck = false;

        if (!ShouldSuppressAllTogglesOverride() && viewModel.AllExtensionsChecked)
            SetAllChecked(viewModel.Extensions, true, ref _suppressExtensionItemCheck);

        SyncAllCheckbox(viewModel.Extensions, ref _suppressExtensionAllCheck,
            value => viewModel.AllExtensionsChecked = value);
        if (!_extensionsSelectionInitialized)
        {
            _extensionsSelectionInitialized = true;
            UpdateExtensionsSelectionCache();
        }
    }

    private void ApplyRootOptions(IReadOnlyList<SelectionOption> options)
    {
        var optionViewModels = new List<SelectionOptionViewModel>(options.Count);
        foreach (var option in options)
            optionViewModels.Add(new SelectionOptionViewModel(option.Name, option.IsChecked));

        _suppressRootItemCheck = true;
        ReplaceCollectionItems(viewModel.RootFolders, optionViewModels);
        _suppressRootItemCheck = false;

        if (!ShouldSuppressAllTogglesOverride() && viewModel.AllRootFoldersChecked)
            SetAllChecked(viewModel.RootFolders, true, ref _suppressRootItemCheck);

        SyncAllCheckbox(viewModel.RootFolders, ref _suppressRootAllCheck,
            value => viewModel.AllRootFoldersChecked = value);
        UpdateRootSelectionCache();
        _rootSelectionInitialized = true;
    }

    private void ApplyResolvedIgnoreOptions(
        IReadOnlyList<ResolvedIgnoreOptionState> options,
        IReadOnlyDictionary<IgnoreOptionId, bool> stateCache)
    {
        var descriptors = new List<IgnoreOptionDescriptor>(options.Count);
        var optionViewModels = new List<IgnoreOptionViewModel>(options.Count);
        foreach (var option in options)
        {
            descriptors.Add(new IgnoreOptionDescriptor(option.Id, option.Label, option.DefaultChecked));
            optionViewModels.Add(new IgnoreOptionViewModel(option.Id, option.Label, option.IsChecked));
        }

        _suppressIgnoreItemCheck = true;
        try
        {
            ReplaceCollectionItems(viewModel.IgnoreOptions, optionViewModels);
            _ignoreOptions = descriptors;
        }
        finally
        {
            _suppressIgnoreItemCheck = false;
        }

        _ignoreOptionStateCache = new Dictionary<IgnoreOptionId, bool>(stateCache);
        _ignoreSelectionCache = BuildSelectedIgnoreOptionSet();
        _ignoreSelectionInitialized = true;
        SyncIgnoreAllCheckbox();
    }

    private void ApplySelectionRefreshSnapshot(SelectionRefreshSnapshot snapshot)
    {
        if (snapshot.RootOptions is not null)
            ApplyRootOptions(snapshot.RootOptions);

        ApplyExtensionOptions(
            snapshot.ExtensionOptions,
            snapshot.ExtensionlessEntriesCount,
            snapshot.IgnoreOptionCounts,
            snapshot.HasIgnoreOptionCounts);

        ApplyResolvedIgnoreOptions(snapshot.IgnoreOptions, snapshot.IgnoreOptionStateCache);
    }

    private static void ReplaceCollectionItems<T>(
        ObservableCollection<T> collection,
        IReadOnlyList<T> items)
    {
        if (collection is ResettableObservableCollection<T> resettableCollection)
        {
            resettableCollection.ReplaceAll(items);
            return;
        }

        collection.Clear();
        foreach (var item in items)
            collection.Add(item);
    }

    private static HashSet<string> CollectCheckedSelectionNames(
        IEnumerable<SelectionOptionViewModel> options,
        StringComparer comparer)
    {
        var selected = new HashSet<string>(comparer);
        foreach (var option in options)
        {
            if (option.IsChecked)
                selected.Add(option.Name);
        }

        return selected;
    }

    private static int SplitExtensions(IReadOnlyCollection<string> source, ICollection<string> visibleExtensions)
    {
        var extensionlessEntriesCount = 0;
        foreach (var entry in source)
        {
            if (IsExtensionlessEntry(entry))
            {
                extensionlessEntriesCount++;
                continue;
            }

            visibleExtensions.Add(entry);
        }

        return extensionlessEntriesCount;
    }

    private SelectionRefreshContext CreateSelectionRefreshContext(string path) =>
        new(
            Path: path,
            PreparedSelectionMode: _preparedSelectionMode,
            AllRootFoldersChecked: viewModel.AllRootFoldersChecked,
            AllExtensionsChecked: viewModel.AllExtensionsChecked,
            RootSelectionInitialized: _rootSelectionInitialized,
            RootSelectionCache: new HashSet<string>(_rootSelectionCache, PathComparer.Default),
            ExtensionsSelectionInitialized: _extensionsSelectionInitialized,
            ExtensionsSelectionCache: new HashSet<string>(_extensionsSelectionCache, StringComparer.OrdinalIgnoreCase),
            IgnoreSelectionInitialized: _ignoreSelectionInitialized,
            IgnoreSelectionCache: new HashSet<IgnoreOptionId>(_ignoreSelectionCache),
            IgnoreOptionStateCache: new Dictionary<IgnoreOptionId, bool>(_ignoreOptionStateCache),
            IgnoreAllPreference: _ignoreAllPreference,
            CurrentSnapshotState: CaptureIgnoreSectionSnapshotState());

    private static async Task<T> RunOnUiThreadAsync<T>(Func<T> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return action();

        return await Dispatcher.UIThread.InvokeAsync(action);
    }

    private HashSet<string>? BuildEffectiveAllowedExtensionsForLiveCounts(
        bool forceAllExtensionsChecked)
    {
        if (forceAllExtensionsChecked)
            return null;

        if (_extensionsSelectionInitialized)
            return new HashSet<string>(_extensionsSelectionCache, StringComparer.OrdinalIgnoreCase);

        if (viewModel.Extensions.Count == 0)
            return null;

        return CollectCheckedSelectionNames(viewModel.Extensions, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsExtensionlessEntry(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var extension = Path.GetExtension(value);
        return string.IsNullOrEmpty(extension) || extension == ".";
    }

    private IgnoreSectionSnapshotState CaptureIgnoreSectionSnapshotState() =>
        new(
            _hasIgnoreOptionCounts,
            _ignoreOptionCounts,
            _hasExtensionlessExtensionEntries,
            _extensionlessExtensionEntriesCount);

    private IgnoreRules GetOrBuildIgnoreRules(
        string path,
        IReadOnlyCollection<IgnoreOptionId> selectedIgnoreOptions,
        IReadOnlyCollection<string>? selectedRootFolders)
    {
        var cacheKey = BuildIgnoreRulesCacheKey(path, selectedIgnoreOptions, selectedRootFolders);

        lock (_ignoreRulesBuildCacheSync)
        {
            if (_ignoreRulesBuildCache is not null &&
                string.Equals(_ignoreRulesBuildCache.Key, cacheKey, StringComparison.Ordinal))
            {
                return _ignoreRulesBuildCache.Rules;
            }
        }

        var rules = buildIgnoreRules(path, selectedIgnoreOptions, selectedRootFolders);
        lock (_ignoreRulesBuildCacheSync)
            _ignoreRulesBuildCache = new IgnoreRulesBuildCacheEntry(cacheKey, rules);

        return rules;
    }

    private static string BuildIgnoreRulesCacheKey(
        string path,
        IReadOnlyCollection<IgnoreOptionId> selectedIgnoreOptions,
        IReadOnlyCollection<string>? selectedRootFolders)
    {
        var normalizedPath = NormalizePathForCache(path);
        var ignoreOptionsKey = BuildIgnoreOptionSelectionKey(selectedIgnoreOptions);
        var rootSelectionKey = BuildRootSelectionKey(selectedRootFolders);
        return $"{normalizedPath}|{ignoreOptionsKey}|{rootSelectionKey}";
    }

    private static string NormalizePathForCache(string path)
    {
        string normalized;
        try
        {
            normalized = Path.GetFullPath(path);
        }
        catch
        {
            normalized = path;
        }

        return PathUtility.NormalizeForCacheKey(normalized);
    }

    private static string BuildIgnoreOptionSelectionKey(IReadOnlyCollection<IgnoreOptionId> selectedIgnoreOptions)
    {
        if (selectedIgnoreOptions.Count == 0)
            return "<none>";

        var ordered = new List<int>(selectedIgnoreOptions.Count);
        foreach (var option in selectedIgnoreOptions)
            ordered.Add((int)option);
        ordered.Sort();

        var sb = new StringBuilder(capacity: ordered.Count * 3);
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append(ordered[i]);
        }

        return sb.ToString();
    }

    private static string BuildRootSelectionKey(IReadOnlyCollection<string>? selectedRootFolders)
    {
        if (selectedRootFolders is null)
            return "<null>";
        if (selectedRootFolders.Count == 0)
            return "<empty>";

        var unique = new HashSet<string>(PathComparer.Default);
        foreach (var root in selectedRootFolders)
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                var normalizedRoot = root.Trim();
                if (OperatingSystem.IsWindows())
                    normalizedRoot = normalizedRoot.ToUpperInvariant();

                unique.Add(normalizedRoot);
            }
        }

        if (unique.Count == 0)
            return "<empty>";

        var ordered = new List<string>(unique);
        ordered.Sort(PathComparer.Default);

        var estimatedLength = ordered.Count * 8;
        foreach (var entry in ordered)
            estimatedLength += entry.Length;

        var sb = new StringBuilder(estimatedLength);
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i > 0)
                sb.Append('|');
            sb.Append(ordered[i]);
        }

        return sb.ToString();
    }

    private static IgnoreRules BuildExtensionAvailabilityScanRules(IgnoreRules rules)
    {
        // Extension availability must not depend on file-level toggles that can hide the
        // very extensions required to keep those toggles visible. Otherwise options such as
        // EmptyFiles or ExtensionlessFiles can disappear immediately after becoming checked.
        if (!rules.IgnoreHiddenFiles &&
            !rules.IgnoreDotFiles &&
            !rules.IgnoreEmptyFiles &&
            !rules.IgnoreExtensionlessFiles)
        {
            return rules;
        }

        return rules with
        {
            IgnoreHiddenFiles = false,
            IgnoreDotFiles = false,
            IgnoreEmptyFiles = false,
            IgnoreExtensionlessFiles = false
        };
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

    private static async Task AwaitBackgroundRefreshTaskAsync(Task task, CancellationToken cancellationToken)
    {
        try
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Coalesced refreshes cancel superseded tasks on purpose. Waiting for idleness
            // should ignore those expected cancellations and continue with the latest task.
        }
    }

    /// <summary>
    /// Disposes all event subscriptions and releases resources to prevent memory leaks.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_backgroundRefreshSync)
        {
            _liveOptionsRefreshCts?.Cancel();
            _liveOptionsRefreshCts?.Dispose();
            _liveOptionsRefreshCts = null;

            _fullRefreshRequestCts?.Cancel();
            _fullRefreshRequestCts?.Dispose();
            _fullRefreshRequestCts = null;
        }
        lock (_ignoreRulesBuildCacheSync)
            _ignoreRulesBuildCache = null;

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
        _ignoreOptionStateCache.Clear();
        _extensionsSelectionCache.Clear();
        _ignoreAllPreference = null;
        _preparedSelectionPath = null;
        _ignoreOptions = [];

        // Dispose the semaphore
        _refreshLock.Dispose();
    }

    private bool ShouldClearCachesForCurrentPath(string currentPath)
        => SelectionSyncCoordinatorPolicy.ShouldClearCachesForCurrentPath(
            _lastLoadedPath,
            _preparedSelectionPath,
            currentPath);

    private bool HasPreparedSelectionForPath(string path)
    {
        return _preparedSelectionPath is not null &&
               PathComparer.Default.Equals(_preparedSelectionPath, path);
    }

    private bool ShouldSkipRefreshForPreparedPath(string currentPath)
        => SelectionSyncCoordinatorPolicy.ShouldSkipRefreshForPreparedPath(_preparedSelectionPath, currentPath);

    private bool IsStalePathRequest(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var currentPath = currentPathProvider();
        if (string.IsNullOrWhiteSpace(currentPath))
            return false;

        return !PathComparer.Default.Equals(currentPath, path);
    }

    private bool ShouldSuppressAllTogglesOverride()
    {
        return _preparedSelectionMode == PreparedSelectionMode.Profile;
    }

    private bool ResolveIgnoreOptionCheckedState(
        IgnoreOptionDescriptor option,
        IReadOnlySet<IgnoreOptionId> previousSelections,
        bool hasPreviousSelections,
        bool useDefaultCheckedFallback)
    {
        // Resolution order keeps explicit runtime state first, then applies the last
        // "All ignore" intent, then profile selections, and only then falls back to defaults.
        if (_ignoreOptionStateCache.TryGetValue(option.Id, out var cachedState))
            return cachedState;

        if (useDefaultCheckedFallback)
            return option.DefaultChecked;

        if (!ShouldSuppressAllTogglesOverride() && _ignoreAllPreference.HasValue)
            return _ignoreAllPreference.Value;

        if (_preparedSelectionMode == PreparedSelectionMode.Profile && hasPreviousSelections)
            return previousSelections.Contains(option.Id);

        if (previousSelections.Contains(option.Id))
            return true;

        return option.DefaultChecked;
    }

    private IReadOnlyList<SelectionOption> ApplyMissingProfileSelectionsFallbackToExtensions(
        IReadOnlyList<SelectionOption> options) =>
        SelectionSyncCoordinatorPolicy.ApplyMissingProfileSelectionsFallbackToExtensions(
            _preparedSelectionMode,
            _extensionsSelectionCache,
            options);

    private IReadOnlyList<SelectionOption> ApplyMissingProfileSelectionsFallbackToRootFolders(
        IReadOnlyList<SelectionOption> options,
        IReadOnlyList<string> scannedRootFolders,
        IgnoreRules ignoreRules) =>
        SelectionSyncCoordinatorPolicy.ApplyMissingProfileSelectionsFallbackToRootFolders(
            _preparedSelectionMode,
            _rootSelectionCache,
            options,
            scannedRootFolders,
            ignoreRules,
            filterSelectionService,
            EmptyStringSet);

    private bool ShouldUseIgnoreDefaultFallback(
        IReadOnlyList<IgnoreOptionDescriptor> options,
        IReadOnlySet<IgnoreOptionId> previousSelections) =>
        SelectionSyncCoordinatorPolicy.ShouldUseIgnoreDefaultFallback(
            _preparedSelectionMode,
            options,
            previousSelections);

    private void ApplyIgnoreAllPreferenceToKnownStates(bool isChecked)
    {
        if (_ignoreOptionStateCache.Count == 0)
            return;

        var knownIds = new List<IgnoreOptionId>(_ignoreOptionStateCache.Count);
        foreach (var id in _ignoreOptionStateCache.Keys)
            knownIds.Add(id);

        foreach (var id in knownIds)
            _ignoreOptionStateCache[id] = isChecked;
    }

    private void PreserveMissingIgnoreSelections(IReadOnlySet<IgnoreOptionId> preserveMissingFrom)
    {
        // Hidden options must keep their last known state even while they are temporarily absent
        // from the visible list, otherwise dynamic availability would silently erase choices.
        var visibleIds = new HashSet<IgnoreOptionId>();
        foreach (var option in _ignoreOptions)
            visibleIds.Add(option.Id);

        foreach (var id in preserveMissingFrom)
        {
            if (!visibleIds.Contains(id) && !_ignoreOptionStateCache.ContainsKey(id))
                _ignoreOptionStateCache[id] = true;
        }
    }

    private HashSet<IgnoreOptionId> BuildSelectedIgnoreOptionSet()
    {
        var selected = new HashSet<IgnoreOptionId>();
        foreach (var (id, isChecked) in _ignoreOptionStateCache)
        {
            if (isChecked)
                selected.Add(id);
        }

        return selected;
    }

    private void UpdateRootSelectionCache()
    {
        if (viewModel.RootFolders.Count == 0)
        {
            _rootSelectionCache.Clear();
            _rootSelectionCache.TrimExcess();
            return;
        }

        _rootSelectionCache = CollectCheckedSelectionNames(viewModel.RootFolders, PathComparer.Default);
    }

    private sealed record IgnoreRulesBuildCacheEntry(string Key, IgnoreRules Rules);

    private sealed record LiveRefreshInput(
        SelectionRefreshContext Context,
        IReadOnlyCollection<string> SelectedRoots);
}
