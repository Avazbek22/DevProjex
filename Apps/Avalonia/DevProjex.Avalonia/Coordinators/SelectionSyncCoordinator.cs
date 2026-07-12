using DevProjex.Application.Models;
using DevProjex.Avalonia.Collections;
using DevProjex.Avalonia.Services;
using DevProjex.Kernel;

namespace DevProjex.Avalonia.Coordinators;

public sealed partial class SelectionSyncCoordinator(
    MainWindowViewModel viewModel,
    ScanOptionsUseCase scanOptions,
    FilterOptionSelectionService filterSelectionService,
    IgnoreOptionsService ignoreOptionsService,
    Func<string, IReadOnlyCollection<IgnoreOptionId>, IReadOnlyCollection<string>?, IgnoreRules> buildIgnoreRules,
    Func<string, IReadOnlyCollection<string>, IgnoreOptionsAvailability> getIgnoreOptionsAvailability,
    Func<string, bool> tryElevateAndRestart,
    Func<string?> currentPathProvider,
    StatusOperationCoordinator? statusOperations = null)
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
    private readonly ProjectSelectionSessionState _session = new();
    private bool _hasExtensionlessExtensionEntries;
    private int _extensionlessExtensionEntriesCount;
    private bool _hasIgnoreOptionCounts;
    private IgnoreOptionCounts _ignoreOptionCounts;
    private IgnoreControllerImpactCounts _ignoreControllerImpactCounts;

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
    // Tracks whether UI selections changed after the last applied selection snapshot.
    // Apply uses it to avoid an unconditional second filesystem pass on large projects.
    private int _selectionRefreshDirty;
    private readonly object _ignoreRulesBuildCacheSync = new();
    private IgnoreRulesBuildCacheEntry? _ignoreRulesBuildCache;
    private static readonly TraceSource RefreshTraceSource = new("DevProjex.SelectionRefresh");
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

        _session.IgnoreOptions.IsInitialized = true;
        _session.IgnoreOptions.AllPreference = isChecked;
        _session.IgnoreOptions.ApplyAllPreferenceToKnownStates(isChecked);

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

        var prev = _session.Extensions.IsInitialized
            ? _session.Extensions.SnapshotSelectedNames()
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previousExtensionStates = SnapshotExtensionOptionStateCacheOrNull(_session.Extensions.IsInitialized);

        // Always scan extensions, even when rootFolders.Count == 0.
        // ScanOptionsUseCase.GetExtensionsForRootFolders will include root-level files.
        var selectedIgnoreOptions = GetSelectedIgnoreOptionIds();
        var ignoreRules = GetOrBuildIgnoreRules(path, selectedIgnoreOptions, rootFolders);
        var extensionScanRules = BuildExtensionAvailabilityScanRules(ignoreRules);
        var forceAllExtensionsChecked = !ShouldSuppressAllTogglesOverride() && viewModel.AllExtensionsChecked;
        var includeDirectoryToggleProbeRoots = !ShouldSuppressAllTogglesOverride() && viewModel.AllRootFoldersChecked;
        var includeControllerImpactProbeRoots = ShouldIncludeControllerImpactProbeRoots(selectedIgnoreOptions);
        var effectiveExtensionPolicy = BuildEffectiveExtensionPolicyForLiveCounts(forceAllExtensionsChecked);
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
                effectiveExtensionPolicy,
                includeDirectoryToggleProbeRoots,
                cancellationToken,
                includeControllerImpactProbeRoots);
            if (scan.RootAccessDenied)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var elevated = await Dispatcher.UIThread.InvokeAsync(() => tryElevateAndRestart(path));
                if (elevated) return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var visibleExtensions = new List<string>(scan.Value.Extensions.Count);
            var extensionlessEntriesCount = SplitExtensions(scan.Value.Extensions, visibleExtensions);
            extensionlessEntriesCount = Math.Max(
                extensionlessEntriesCount,
                scan.Value.EffectiveIgnoreOptionCounts.ExtensionlessFiles);
            var options = filterSelectionService.BuildExtensionOptions(visibleExtensions, prev, previousExtensionStates);
            var usedProfileFallback = SelectionRefreshPolicy.ShouldApplyMissingProfileSelectionsFallback(
                _session.PreparedMode,
                _session.Extensions.SelectedNames,
                options);
            options = ApplyMissingProfileSelectionsFallbackToExtensions(options);

            if (usedProfileFallback &&
                !ExtensionSnapshotReusePolicy.CanReuseSnapshot(effectiveExtensionPolicy, options))
            {
                scan = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
                    path,
                    rootFolders,
                    extensionScanRules,
                    ignoreRules,
                    BuildResolvedExtensionPolicy(options),
                    includeDirectoryToggleProbeRoots,
                    cancellationToken,
                    includeControllerImpactProbeRoots);

                visibleExtensions = new List<string>(scan.Value.Extensions.Count);
                extensionlessEntriesCount = SplitExtensions(scan.Value.Extensions, visibleExtensions);
                extensionlessEntriesCount = Math.Max(
                    extensionlessEntriesCount,
                    scan.Value.EffectiveIgnoreOptionCounts.ExtensionlessFiles);
                options = filterSelectionService.BuildExtensionOptions(visibleExtensions, prev, previousExtensionStates);
                options = ApplyMissingProfileSelectionsFallbackToExtensions(options);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (version != _extensionScanVersion) return;
                if (IsStalePathRequest(path)) return;
                ApplyExtensionOptions(
                    options,
                    extensionlessEntriesCount,
                    scan.Value.EffectiveIgnoreOptionCounts,
                    scan.Value.ControllerImpactCounts,
                    hasIgnoreOptionCounts: true);
            });
        }, cancellationToken);
    }

    public Task PopulateRootFoldersAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path)) return Task.CompletedTask;
        if (IsStalePathRequest(path)) return Task.CompletedTask;
        var version = Interlocked.Increment(ref _rootScanVersion);

        var hasPreviousSelections = _session.RootFolders.IsInitialized;
        var prev = hasPreviousSelections
            ? _session.RootFolders.SnapshotSelectedNames()
            : new HashSet<string>(PathComparer.Default);
        var previousRootStates = SnapshotRootOptionStateCacheOrNull(hasPreviousSelections);

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
                hasPreviousSelections,
                previousRootStates);
            options = ApplyMissingProfileSelectionsFallbackToRootFolders(options, scan.Value, ignoreRules);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (version != _rootScanVersion) return;
                if (IsStalePathRequest(path)) return;
                ApplyRootOptions(options);
            });
        }, cancellationToken);
    }

    public async Task PopulateIgnoreOptionsForRootSelectionAsync(
        IReadOnlyCollection<string> rootFolders,
        string? currentPath = null,
        CancellationToken cancellationToken = default)
    {
        var previousSelections = _session.IgnoreOptions.SnapshotSelectedOptions();
        var hasPreviousSelections = _session.IgnoreOptions.IsInitialized;
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
        var previousSelections = _session.IgnoreOptions.SnapshotSelectedOptions();
        var hasPreviousSelections = _session.IgnoreOptions.IsInitialized;
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
        var previousSelections = _session.IgnoreOptions.SnapshotSelectedOptions();
        var hasPreviousSelections = _session.IgnoreOptions.IsInitialized;
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
        _session.ApplyProfile(projectPath, profile);
    }

    public void ResetProjectProfileSelections(string projectPath)
    {
        _session.ResetToDefaultsForProject(projectPath);

        // Restore defaults for projects without a saved profile.
        viewModel.AllRootFoldersChecked = true;
        viewModel.AllExtensionsChecked = true;
        viewModel.AllIgnoreChecked = true;
    }

    public async Task UpdateLiveOptionsFromRootSelectionAsync(
        string? currentPath,
        CancellationToken cancellationToken = default)
    {
        await UpdateLiveOptionsFromRootSelectionCoreAsync(
            currentPath,
            expectedRequestVersion: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateLiveOptionsFromRootSelectionIfDirtyAsync(
        string? currentPath,
        CancellationToken cancellationToken = default)
    {
        if (!HasDirtySelectionRefresh())
            return;

        await UpdateLiveOptionsFromRootSelectionAsync(currentPath, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task UpdateLiveOptionsFromRootSelectionCoreAsync(
        string? currentPath,
        int? expectedRequestVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(currentPath)) return;
        if (IsStalePathRequest(currentPath)) return;
        if (IsSupersededLiveOptionsRequest(expectedRequestVersion)) return;
        cancellationToken.ThrowIfCancellationRequested();

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSupersededLiveOptionsRequest(expectedRequestVersion))
                return;
            if (IsStalePathRequest(currentPath))
                return;

            var liveInput = await RunOnUiThreadAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsSupersededLiveOptionsRequest(expectedRequestVersion))
                    return null;
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
            if (IsSupersededLiveOptionsRequest(expectedRequestVersion))
                return;
            if (snapshot.RootAccessDenied)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsSupersededLiveOptionsRequest(expectedRequestVersion))
                    return;
                var elevated = await Dispatcher.UIThread.InvokeAsync(() => tryElevateAndRestart(currentPath));
                if (elevated)
                    return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsSupersededLiveOptionsRequest(expectedRequestVersion))
                    return;
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
        await RefreshRootAndDependentsCoreAsync(
            currentPath,
            expectedRequestVersion: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SelectionRefreshSnapshot?> BuildRootAndDependentsSnapshotAsync(
        string currentPath,
        CancellationToken cancellationToken = default)
    {
        return await BuildRootAndDependentsSnapshotCoreAsync(
            currentPath,
            expectedRequestVersion: null,
            cancellationToken).ConfigureAwait(false);
    }

    public bool ApplyRootAndDependentsSnapshot(string currentPath, SelectionRefreshSnapshot snapshot)
    {
        if (IsStalePathRequest(currentPath) && !HasPreparedSelectionForPath(currentPath))
            return false;
        if (ShouldSkipRefreshForPreparedPath(currentPath))
            return false;

        ApplySelectionRefreshSnapshot(snapshot);

        // Project-load snapshots apply selection and tree together. Prepared profile/default
        // state must still be consumed only after the matching selection snapshot wins.
        _session.ConsumePreparedSelectionForPath(currentPath);
        return true;
    }

    private async Task RefreshRootAndDependentsCoreAsync(
        string currentPath,
        int? expectedRequestVersion,
        CancellationToken cancellationToken)
    {
        // Serialize refresh operations to prevent race conditions
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSupersededFullRefreshRequest(expectedRequestVersion))
                return;

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
                if (IsSupersededFullRefreshRequest(expectedRequestVersion))
                    return null;
                if (IsStalePathRequest(currentPath) && !HasPreparedSelectionForPath(currentPath))
                    return null;
                if (ShouldSkipRefreshForPreparedPath(currentPath))
                    return null;

                // UI collections and selection caches are Avalonia-owned state. Capture and
                // cache reset must happen on the UI thread; the expensive scan runs after this.
                if (ShouldClearCachesForCurrentPath(currentPath))
                    ClearCachesForNewProject();

                _session.LastLoadedPath = currentPath;
                MarkSelectionRefreshDirty();
                return CreateSelectionRefreshContext(currentPath);
            });
            if (context is null)
                return;

            var snapshot = await Task.Run(
                () => _selectionRefreshEngine.ComputeFullRefreshSnapshot(context, cancellationToken),
                cancellationToken);
            if (IsSupersededFullRefreshRequest(expectedRequestVersion))
                return;
            if (snapshot.RootAccessDenied)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsSupersededFullRefreshRequest(expectedRequestVersion))
                    return;
                var elevated = await Dispatcher.UIThread.InvokeAsync(() => tryElevateAndRestart(currentPath));
                if (elevated)
                    return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsSupersededFullRefreshRequest(expectedRequestVersion))
                    return;
                if (IsStalePathRequest(currentPath) && !HasPreparedSelectionForPath(currentPath))
                    return;
                if (ShouldSkipRefreshForPreparedPath(currentPath))
                    return;

                ApplySelectionRefreshSnapshot(snapshot);

                // Consume prepared selection only after the matching snapshot is applied.
                // Keeping this with the UI mutation prevents stale background refreshes from
                // clearing the prepared state between capture and apply.
                _session.ConsumePreparedSelectionForPath(currentPath);
            });
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<SelectionRefreshSnapshot?> BuildRootAndDependentsSnapshotCoreAsync(
        string currentPath,
        int? expectedRequestVersion,
        CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSupersededFullRefreshRequest(expectedRequestVersion))
                return null;

            if (IsStalePathRequest(currentPath) && !HasPreparedSelectionForPath(currentPath))
                return null;

            if (ShouldSkipRefreshForPreparedPath(currentPath))
                return null;

            var context = await RunOnUiThreadAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsSupersededFullRefreshRequest(expectedRequestVersion))
                    return null;
                if (IsStalePathRequest(currentPath) && !HasPreparedSelectionForPath(currentPath))
                    return null;
                if (ShouldSkipRefreshForPreparedPath(currentPath))
                    return null;

                if (ShouldClearCachesForCurrentPath(currentPath))
                    ClearCachesForNewProject();

                _session.LastLoadedPath = currentPath;
                MarkSelectionRefreshDirty();
                return CreateSelectionRefreshContext(currentPath, captureTreeInventory: true);
            });
            if (context is null)
                return null;

            var snapshot = await Task.Run(
                    () => _selectionRefreshEngine.ComputeFullRefreshSnapshot(context, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

            if (IsSupersededFullRefreshRequest(expectedRequestVersion))
                return null;

            return snapshot;
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

    public void CancelPendingRefreshes()
    {
        lock (_backgroundRefreshSync)
        {
            _liveOptionsRefreshCts?.Cancel();
            _fullRefreshRequestCts?.Cancel();
            _liveOptionsRequestVersion = unchecked(_liveOptionsRequestVersion + 1);
            _fullRefreshRequestVersion = unchecked(_fullRefreshRequestVersion + 1);
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

        _session.ClearProjectCaches(trimExcess: true);
        _hasExtensionlessExtensionEntries = false;
        _extensionlessExtensionEntriesCount = 0;
        _hasIgnoreOptionCounts = false;
        _ignoreOptionCounts = IgnoreOptionCounts.Empty;
        _ignoreControllerImpactCounts = IgnoreControllerImpactCounts.Empty;

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
        return SnapshotRuntimeSelectedIgnoreOptions();
    }

    public IReadOnlyDictionary<string, bool>? SnapshotRootOptionStatesForPersistence() =>
        SnapshotRootOptionStateCacheOrNull(_session.RootFolders.IsInitialized);

    public IReadOnlyDictionary<string, bool>? SnapshotExtensionOptionStatesForPersistence() =>
        SnapshotExtensionOptionStateCacheOrNull(_session.Extensions.IsInitialized);

    public IReadOnlyDictionary<IgnoreOptionId, bool>? SnapshotIgnoreOptionStatesForPersistence()
    {
        if (!_session.IgnoreOptions.IsInitialized &&
            _session.IgnoreOptions.SelectedOptions.Count == 0 &&
            _session.IgnoreOptions.OptionStateCache.Count == 0)
        {
            return null;
        }

        return _session.IgnoreOptions.SnapshotStateCache();
    }

    private void EnsureIgnoreSelectionCache()
    {
        if (_session.IgnoreOptions.IsInitialized || _session.IgnoreOptions.SelectedOptions.Count > 0)
            return;

        var path = currentPathProvider() ?? _session.LastLoadedPath;
        var selectedRoots = GetSelectedRootFolders();
        var availability = ResolveIgnoreOptionsAvailability(path, selectedRoots);
        _ignoreOptions = ignoreOptionsService.GetOptions(availability);
        _session.IgnoreOptions.EnsureDefaults(_ignoreOptions);
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
                    IncludeGitIgnore = availability.IncludeGitIgnore &&
                                       ShouldKeepControllerVisible(
                                           IgnoreOptionId.UseGitIgnore,
                                           _ignoreControllerImpactCounts.GitIgnore),
                    IncludeSmartIgnore = availability.IncludeSmartIgnore &&
                                         ShouldKeepControllerVisible(
                                             IgnoreOptionId.SmartIgnore,
                                             _ignoreControllerImpactCounts.SmartIgnore),
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

    private bool ShouldKeepControllerVisible(
        IgnoreOptionId optionId,
        int controllerImpactCount)
    {
        if (controllerImpactCount > 0)
            return true;

        // Controller toggles must stay reversible after an explicit user choice. Only the
        // unchecked zero-impact state is forced visible; checked zero-impact profile state
        // stays hidden so it cannot unexpectedly become an active runtime selection.
        return _session.IgnoreOptionStateCacheIsComplete &&
               _session.IgnoreOptions.OptionStateCache.TryGetValue(optionId, out var isChecked) &&
               !isChecked;
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

        UpdateIgnoreSelectionCache(
            hasPreviousSelections ? previousSelections : null,
            markStateCacheComplete: false);
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
        _session.Extensions.UpdateFromVisibleOptions(
            viewModel.Extensions.Select(static option => new SelectionOption(option.Name, option.IsChecked)));
    }

    internal void ApplyExtensionScan(IReadOnlyCollection<string> extensions)
    {
        var visibleExtensions = new List<string>(extensions.Count);
        var extensionlessEntriesCount = SplitExtensions(extensions, visibleExtensions);
        var prev = _session.Extensions.IsInitialized
            ? _session.Extensions.SnapshotSelectedNames()
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previousExtensionStates = SnapshotExtensionOptionStateCacheOrNull(_session.Extensions.IsInitialized);

        var options = filterSelectionService.BuildExtensionOptions(visibleExtensions, prev, previousExtensionStates);
        options = ApplyMissingProfileSelectionsFallbackToExtensions(options);
        ApplyExtensionOptions(
            options,
            extensionlessEntriesCount,
            IgnoreOptionCounts.Empty,
            IgnoreControllerImpactCounts.Empty,
            hasIgnoreOptionCounts: false);
    }

    public void UpdateIgnoreSelectionCache(
        IReadOnlySet<IgnoreOptionId>? preserveMissingFrom = null,
        bool markStateCacheComplete = true)
    {
        _session.IgnoreOptions.UpdateFromVisibleOptions(
            viewModel.IgnoreOptions.Select(static option => (option.Id, option.IsChecked)),
            preserveMissingFrom,
            _ignoreOptions.Select(static option => option.Id));
        if (markStateCacheComplete)
            _session.IgnoreOptionStateCacheIsComplete = true;
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

            SyncAllCheckbox(viewModel.RootFolders, ref _suppressRootAllCheck,
                value => viewModel.AllRootFoldersChecked = value,
                emptyValue: true);
            UpdateRootSelectionCache();

            QueueLiveOptionsRefresh(currentPathProvider());
        }
        else if (viewModel.Extensions.Contains(option))
        {
            if (_suppressExtensionItemCheck) return;

            SyncAllCheckbox(viewModel.Extensions, ref _suppressExtensionAllCheck,
                value => viewModel.AllExtensionsChecked = value);

            UpdateExtensionsSelectionCache();
            QueueLiveOptionsRefresh(currentPathProvider());
        }
    }

    private void OnIgnoreCheckedChanged(object? sender, EventArgs e)
    {
        if (_suppressIgnoreItemCheck) return;

        var changedOption = sender as IgnoreOptionViewModel;
        _session.IgnoreOptions.IsInitialized = true;
        _session.IgnoreOptions.AllPreference = null;

        SyncAllCheckbox(viewModel.IgnoreOptions, ref _suppressIgnoreAllCheck,
            value => viewModel.AllIgnoreChecked = value);

        UpdateIgnoreSelectionCache();

        var currentPath = currentPathProvider();
        if (!string.IsNullOrEmpty(currentPath))
        {
            QueueRefreshForIgnoreOptionChange(currentPath, changedOption?.Id);
        }
    }

    private void QueueRefreshForIgnoreOptionChange(string currentPath, IgnoreOptionId? changedOptionId)
    {
        if (SelectionRefreshRoutingPolicy.CanUseLiveOptionsRefresh(changedOptionId))
        {
            QueueLiveOptionsRefresh(currentPath);
            return;
        }

        QueueFullRefresh(currentPath);
    }

    /// <summary>
    /// Coalesces rapid root-selection changes and keeps only the latest live-options refresh.
    /// </summary>
    private void QueueLiveOptionsRefresh(string? currentPath)
    {
        if (string.IsNullOrWhiteSpace(currentPath))
            return;

        CancellationTokenSource? previousCts;
        Task previousTask;
        Task queuedTask;
        CancellationToken token;
        Action cancelAction;
        int version;
        lock (_backgroundRefreshSync)
        {
            if (_disposed)
                return;

            MarkSelectionRefreshDirty();
            previousCts = _liveOptionsRefreshCts;
            previousTask = _latestLiveOptionsRefreshTask;
            previousCts?.Cancel();

            _liveOptionsRefreshCts = new CancellationTokenSource();
            token = _liveOptionsRefreshCts.Token;
            cancelAction = _liveOptionsRefreshCts.Cancel;
            version = unchecked(++_liveOptionsRequestVersion);
            queuedTask = RunQueuedLiveOptionsRefreshAsync(currentPath, version, token, cancelAction);
            _latestLiveOptionsRefreshTask = queuedTask;
        }

        DisposeCancellationSourceWhenTaskCompletes(previousCts, previousTask);
        FireAndForgetSafe(queuedTask, "live-options refresh");
    }

    /// <summary>
    /// Coalesces rapid ignore-option changes and keeps only the latest full refresh request.
    /// </summary>
    private void QueueFullRefresh(string? currentPath)
    {
        if (string.IsNullOrWhiteSpace(currentPath))
            return;

        CancellationTokenSource? previousCts;
        Task previousTask;
        CancellationTokenSource? invalidatedLiveCts;
        Task invalidatedLiveTask;
        Task queuedTask;
        CancellationToken token;
        Action cancelAction;
        int version;
        lock (_backgroundRefreshSync)
        {
            if (_disposed)
                return;

            MarkSelectionRefreshDirty();
            invalidatedLiveCts = _liveOptionsRefreshCts;
            invalidatedLiveTask = _latestLiveOptionsRefreshTask;
            if (invalidatedLiveCts is not null)
            {
                invalidatedLiveCts.Cancel();
                _liveOptionsRefreshCts = null;
                _liveOptionsRequestVersion = unchecked(_liveOptionsRequestVersion + 1);
            }

            previousCts = _fullRefreshRequestCts;
            previousTask = _latestFullRefreshTask;
            previousCts?.Cancel();

            _fullRefreshRequestCts = new CancellationTokenSource();
            token = _fullRefreshRequestCts.Token;
            cancelAction = _fullRefreshRequestCts.Cancel;
            version = unchecked(++_fullRefreshRequestVersion);
            queuedTask = RunQueuedFullRefreshAsync(currentPath, version, token, cancelAction);
            _latestFullRefreshTask = queuedTask;
        }

        DisposeCancellationSourceWhenTaskCompletes(invalidatedLiveCts, invalidatedLiveTask);
        DisposeCancellationSourceWhenTaskCompletes(previousCts, previousTask);
        FireAndForgetSafe(queuedTask, "full selection refresh");
    }

    private async Task RunQueuedLiveOptionsRefreshAsync(
        string currentPath,
        int version,
        CancellationToken cancellationToken,
        Action cancelAction)
    {
        if (cancellationToken.IsCancellationRequested)
            return;
        if (version != Volatile.Read(ref _liveOptionsRequestVersion))
            return;

        using var _ = PerformanceMetrics.Measure("SelectionRefresh.LiveOptions");
        await using var statusLease = SelectionRefreshStatusLease.Start(
            viewModel,
            statusOperations,
            cancelAction,
            cancellationToken);
        await UpdateLiveOptionsFromRootSelectionCoreAsync(
            currentPath,
            version,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RunQueuedFullRefreshAsync(
        string currentPath,
        int version,
        CancellationToken cancellationToken,
        Action cancelAction)
    {
        if (cancellationToken.IsCancellationRequested)
            return;
        if (version != Volatile.Read(ref _fullRefreshRequestVersion))
            return;

        using var _ = PerformanceMetrics.Measure("SelectionRefresh.Full");
        await using var statusLease = SelectionRefreshStatusLease.Start(
            viewModel,
            statusOperations,
            cancelAction,
            cancellationToken);
        await RefreshRootAndDependentsCoreAsync(
            currentPath,
            version,
            cancellationToken).ConfigureAwait(false);
    }

    private static void SyncAllCheckbox<T>(
        IEnumerable<T> options,
        ref bool suppressFlag,
        Action<bool> setValue,
        bool emptyValue = false)
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
            setValue(hasItems ? allChecked : emptyValue);
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
        IgnoreControllerImpactCounts controllerImpactCounts,
        bool hasIgnoreOptionCounts)
    {
        var effectiveExtensionlessCount = hasIgnoreOptionCounts
            ? ignoreOptionCounts.ExtensionlessFiles
            : extensionlessEntriesCount;

        _extensionlessExtensionEntriesCount = effectiveExtensionlessCount;
        _hasExtensionlessExtensionEntries = effectiveExtensionlessCount > 0;
        _ignoreOptionCounts = ignoreOptionCounts;
        _ignoreControllerImpactCounts = hasIgnoreOptionCounts
            ? controllerImpactCounts
            : IgnoreControllerImpactCounts.Empty;
        _hasIgnoreOptionCounts = hasIgnoreOptionCounts;

        if (!SelectionOptionsMatch(viewModel.Extensions, options))
        {
            var optionViewModels = new List<SelectionOptionViewModel>(options.Count);
            foreach (var option in options)
                optionViewModels.Add(new SelectionOptionViewModel(option.Name, option.IsChecked));

            _suppressExtensionItemCheck = true;
            ReplaceCollectionItems(viewModel.Extensions, optionViewModels);
            _suppressExtensionItemCheck = false;
        }

        if (!ShouldSuppressAllTogglesOverride() && viewModel.AllExtensionsChecked)
            SetAllChecked(viewModel.Extensions, true, ref _suppressExtensionItemCheck);

        SyncAllCheckbox(viewModel.Extensions, ref _suppressExtensionAllCheck,
            value => viewModel.AllExtensionsChecked = value);
        if (!_session.Extensions.IsInitialized)
            UpdateExtensionsSelectionCache();
    }

    private void ApplyRootOptions(IReadOnlyList<SelectionOption> options)
    {
        if (!SelectionOptionsMatch(viewModel.RootFolders, options))
        {
            var optionViewModels = new List<SelectionOptionViewModel>(options.Count);
            foreach (var option in options)
                optionViewModels.Add(new SelectionOptionViewModel(option.Name, option.IsChecked));

            _suppressRootItemCheck = true;
            ReplaceCollectionItems(viewModel.RootFolders, optionViewModels);
            _suppressRootItemCheck = false;
        }

        if (!ShouldSuppressAllTogglesOverride() && viewModel.AllRootFoldersChecked)
            SetAllChecked(viewModel.RootFolders, true, ref _suppressRootItemCheck);

        SyncAllCheckbox(viewModel.RootFolders, ref _suppressRootAllCheck,
            value => viewModel.AllRootFoldersChecked = value,
            emptyValue: true);
        UpdateRootSelectionCache();
    }

    private void ApplyResolvedIgnoreOptions(
        IReadOnlyList<ResolvedIgnoreOptionState> options,
        IReadOnlyDictionary<IgnoreOptionId, bool> stateCache)
    {
        var descriptors = new List<IgnoreOptionDescriptor>(options.Count);
        foreach (var option in options)
            descriptors.Add(new IgnoreOptionDescriptor(option.Id, option.Label, option.DefaultChecked));

        if (!IgnoreOptionsMatch(viewModel.IgnoreOptions, options))
        {
            var optionViewModels = new List<IgnoreOptionViewModel>(options.Count);
            foreach (var option in options)
                optionViewModels.Add(new IgnoreOptionViewModel(option.Id, option.Label, option.IsChecked));

            _suppressIgnoreItemCheck = true;
            try
            {
                ReplaceCollectionItems(viewModel.IgnoreOptions, optionViewModels);
            }
            finally
            {
                _suppressIgnoreItemCheck = false;
            }
        }

        _ignoreOptions = descriptors;
        _session.IgnoreOptions.ReplaceStateCache(stateCache);
        _session.IgnoreOptionStateCacheIsComplete = true;
        SyncIgnoreAllCheckbox();
    }

    private void ApplySelectionRefreshSnapshot(SelectionRefreshSnapshot snapshot)
    {
        // A full/live selection snapshot is the authoritative count-driven ignore state.
        // Invalidate older standalone availability refreshes so they cannot overwrite it.
        Interlocked.Increment(ref _ignoreOptionsVersion);

        if (snapshot.RootOptions is not null)
            ApplyRootOptions(snapshot.RootOptions);

        ApplyExtensionOptions(
            snapshot.ExtensionOptions,
            snapshot.ExtensionlessEntriesCount,
            snapshot.IgnoreOptionCounts,
            snapshot.ControllerImpactCounts,
            snapshot.HasIgnoreOptionCounts);

        ApplyResolvedIgnoreOptions(snapshot.IgnoreOptions, snapshot.IgnoreOptionStateCache);
        MarkSelectionRefreshClean();
    }

    private bool HasDirtySelectionRefresh() =>
        Volatile.Read(ref _selectionRefreshDirty) != 0;

    private void MarkSelectionRefreshDirty() =>
        Volatile.Write(ref _selectionRefreshDirty, 1);

    private void MarkSelectionRefreshClean() =>
        Volatile.Write(ref _selectionRefreshDirty, 0);

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

    private static bool SelectionOptionsMatch(
        IReadOnlyList<SelectionOptionViewModel> current,
        IReadOnlyList<SelectionOption> next)
    {
        if (current.Count != next.Count)
            return false;

        for (var index = 0; index < next.Count; index++)
        {
            if (!string.Equals(current[index].Name, next[index].Name, StringComparison.Ordinal) ||
                current[index].IsChecked != next[index].IsChecked)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IgnoreOptionsMatch(
        IReadOnlyList<IgnoreOptionViewModel> current,
        IReadOnlyList<ResolvedIgnoreOptionState> next)
    {
        if (current.Count != next.Count)
            return false;

        for (var index = 0; index < next.Count; index++)
        {
            var currentOption = current[index];
            var nextOption = next[index];
            if (currentOption.Id != nextOption.Id ||
                !string.Equals(currentOption.Label, nextOption.Label, StringComparison.Ordinal) ||
                currentOption.IsChecked != nextOption.IsChecked)
            {
                return false;
            }
        }

        return true;
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

    private SelectionRefreshContext CreateSelectionRefreshContext(string path, bool captureTreeInventory = false) =>
        new(
            Path: path,
            PreparedSelectionMode: _session.PreparedMode,
            AllRootFoldersChecked: viewModel.AllRootFoldersChecked,
            AllExtensionsChecked: viewModel.AllExtensionsChecked,
            RootSelectionInitialized: _session.RootFolders.IsInitialized,
            RootSelectionCache: _session.RootFolders.SnapshotSelectedNames(),
            ExtensionsSelectionInitialized: _session.Extensions.IsInitialized,
            ExtensionsSelectionCache: _session.Extensions.SnapshotSelectedNames(),
            IgnoreSelectionInitialized: _session.IgnoreOptions.IsInitialized,
            IgnoreSelectionCache: SnapshotRuntimeSelectedIgnoreOptions(),
            IgnoreOptionStateCache: _session.IgnoreOptions.SnapshotStateCache(),
            IgnoreAllPreference: _session.IgnoreOptions.AllPreference,
            CurrentSnapshotState: CaptureIgnoreSectionSnapshotState(),
            RootOptionStateCache: SnapshotRootOptionStateCacheOrNull(isInitialized: true),
            ExtensionOptionStateCache: SnapshotExtensionOptionStateCacheOrNull(isInitialized: true),
            IgnoreOptionStateCacheIsComplete: _session.IgnoreOptionStateCacheIsComplete,
            CaptureTreeInventory: captureTreeInventory);

    private HashSet<IgnoreOptionId> SnapshotRuntimeSelectedIgnoreOptions()
    {
        var selected = _session.IgnoreOptions.SnapshotSelectedOptions();
        if (selected.Count == 0)
            return selected;

        var visibleIds = new HashSet<IgnoreOptionId>();
        foreach (var option in _ignoreOptions)
            visibleIds.Add(option.Id);

        // The full cache may contain hidden options from profiles or transient refreshes.
        // Runtime ignore rules must follow only visible UI controls, otherwise an invisible
        // stale checkbox can keep filtering project content after the user turns everything off.
        selected.IntersectWith(visibleIds);
        return selected;
    }

    private static async Task<T> RunOnUiThreadAsync<T>(Func<T> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return action();

        return await Dispatcher.UIThread.InvokeAsync(action);
    }

    private IExtensionInclusionPolicy? BuildEffectiveExtensionPolicyForLiveCounts(
        bool forceAllExtensionsChecked)
    {
        if (forceAllExtensionsChecked)
            return null;

        if (!_session.Extensions.IsInitialized && viewModel.Extensions.Count == 0)
            return null;

		var previousSelections = _session.Extensions.IsInitialized
			? _session.Extensions.SnapshotSelectedNames()
			: CollectCheckedSelectionNames(viewModel.Extensions, StringComparer.OrdinalIgnoreCase);
		var stateCache = SnapshotExtensionOptionStateCacheOrNull(_session.Extensions.IsInitialized);

		return new ExtensionSelectionInclusionPolicy(
            new SelectionStateResolver(previousSelections, stateCache),
            defaultForNewExtension: stateCache is not null);
    }

    private static IExtensionInclusionPolicy BuildResolvedExtensionPolicy(
        IReadOnlyList<SelectionOption> extensionOptions)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in extensionOptions)
        {
            if (option.IsChecked)
                selected.Add(option.Name);
        }

        return new ExtensionSetInclusionPolicy(selected);
    }

    private IReadOnlyDictionary<string, bool>? SnapshotRootOptionStateCacheOrNull(bool isInitialized)
    {
        if (!isInitialized)
            return null;

        return _session.RootFolders.SnapshotOptionStatesOrNull(
            suppressLegacySelectedOnlyState: ShouldSuppressAllTogglesOverride());
    }

    private IReadOnlyDictionary<string, bool>? SnapshotExtensionOptionStateCacheOrNull(bool isInitialized)
    {
        if (!isInitialized)
            return null;

        return _session.Extensions.SnapshotOptionStatesOrNull(
            suppressLegacySelectedOnlyState: ShouldSuppressAllTogglesOverride());
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
            _ignoreControllerImpactCounts,
            _hasExtensionlessExtensionEntries,
            _extensionlessExtensionEntriesCount);

    private IgnoreRules GetOrBuildIgnoreRules(
        string path,
        IReadOnlyCollection<IgnoreOptionId> selectedIgnoreOptions,
        IReadOnlyCollection<string>? selectedRootFolders)
    {
        var cacheKey = IgnoreRulesBuildCacheKeyBuilder.Build(path, selectedIgnoreOptions, selectedRootFolders);

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
    /// Fire-and-forget wrapper for coalesced background refreshes.
    /// Cancellation is expected; real failures are traced so silent UI refresh loss is diagnosable.
    /// </summary>
    private static async void FireAndForgetSafe(Task task, string operationName)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when operation is superseded
        }
        catch (Exception ex)
        {
            RefreshTraceSource.TraceEvent(
                TraceEventType.Warning,
                0,
                "Selection refresh task '{0}' failed: {1}",
                operationName,
                ex);
            RefreshTraceSource.Flush();
            Debug.WriteLine($"[WARN] Selection refresh task '{operationName}' failed: {ex}");
        }
    }

    private static void DisposeCancellationSourceWhenTaskCompletes(
        CancellationTokenSource? source,
        Task task)
    {
        if (source is null)
            return;

        _ = task.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            source,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
            var liveOptionsRefreshCts = _liveOptionsRefreshCts;
            var fullRefreshRequestCts = _fullRefreshRequestCts;
            var latestLiveOptionsRefreshTask = _latestLiveOptionsRefreshTask;
            var latestFullRefreshTask = _latestFullRefreshTask;

            _liveOptionsRefreshCts?.Cancel();
            _liveOptionsRefreshCts = null;

            _fullRefreshRequestCts?.Cancel();
            _fullRefreshRequestCts = null;

            DisposeCancellationSourceWhenTaskCompletes(liveOptionsRefreshCts, latestLiveOptionsRefreshTask);
            DisposeCancellationSourceWhenTaskCompletes(fullRefreshRequestCts, latestFullRefreshTask);
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
        _session.ClearProjectCaches(trimExcess: true);
        _session.ClearPreparedSelection();
        _ignoreOptions = [];

        // Dispose the semaphore
        _refreshLock.Dispose();
    }

    private bool ShouldClearCachesForCurrentPath(string currentPath)
        => _session.ShouldClearCachesForCurrentPath(currentPath);

    private bool HasPreparedSelectionForPath(string path)
    {
        return _session.HasPreparedSelectionForPath(path);
    }

    private bool ShouldSkipRefreshForPreparedPath(string currentPath)
        => _session.ShouldSkipRefreshForPreparedPath(currentPath);

    private bool IsSupersededLiveOptionsRequest(int? expectedRequestVersion) =>
        expectedRequestVersion.HasValue &&
        expectedRequestVersion.Value != Volatile.Read(ref _liveOptionsRequestVersion);

    private bool IsSupersededFullRefreshRequest(int? expectedRequestVersion) =>
        expectedRequestVersion.HasValue &&
        expectedRequestVersion.Value != Volatile.Read(ref _fullRefreshRequestVersion);

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
        return _session.IsPreparedProfile;
    }

    private bool ShouldIncludeControllerImpactProbeRoots(IReadOnlyCollection<IgnoreOptionId> selectedIgnoreOptions)
    {
        if (selectedIgnoreOptions.Contains(IgnoreOptionId.UseGitIgnore) ||
            selectedIgnoreOptions.Contains(IgnoreOptionId.SmartIgnore))
        {
            // Root-level controller impact keeps .gitignore/Smart Ignore visible when the
            // active controller hides root folders outside the currently selected subset.
            return true;
        }

        return !ShouldSuppressAllTogglesOverride() || viewModel.AllRootFoldersChecked;
    }

    private bool ResolveIgnoreOptionCheckedState(
        IgnoreOptionDescriptor option,
        IReadOnlySet<IgnoreOptionId> previousSelections,
        bool hasPreviousSelections,
        bool useDefaultCheckedFallback)
    {
        // Resolution order keeps explicit runtime state first, then applies the last
        // "All ignore" intent, then profile selections, and only then falls back to defaults.
        if (_session.IgnoreOptions.TryGetCachedState(option.Id, out var cachedState))
            return cachedState;

        if (_session.IgnoreOptions.AllPreference.HasValue)
            return _session.IgnoreOptions.AllPreference.Value;

        if (_session.IgnoreOptionStateCacheIsComplete)
            return option.DefaultChecked;

        if (useDefaultCheckedFallback && SelectionRefreshPolicy.CanUseIgnoreDefaultFallback(option.Id))
            return option.DefaultChecked;

        if (_session.PreparedMode == PreparedSelectionMode.Profile && hasPreviousSelections)
            return previousSelections.Contains(option.Id);

        if (previousSelections.Contains(option.Id))
            return true;

        return option.DefaultChecked;
    }

    private IReadOnlyList<SelectionOption> ApplyMissingProfileSelectionsFallbackToExtensions(
        IReadOnlyList<SelectionOption> options) =>
        SelectionRefreshPolicy.ApplyMissingProfileSelectionsFallbackToExtensions(
            _session.PreparedMode,
            _session.Extensions.SelectedNames,
            options);

    private IReadOnlyList<SelectionOption> ApplyMissingProfileSelectionsFallbackToRootFolders(
        IReadOnlyList<SelectionOption> options,
        IReadOnlyList<string> scannedRootFolders,
        IgnoreRules ignoreRules) =>
        SelectionRefreshPolicy.ApplyMissingProfileSelectionsFallbackToRootFolders(
            _session.PreparedMode,
            _session.RootFolders.SelectedNames,
            options,
            scannedRootFolders,
            ignoreRules,
            filterSelectionService,
            EmptyStringSet);

    private bool ShouldUseIgnoreDefaultFallback(
        IReadOnlyList<IgnoreOptionDescriptor> options,
        IReadOnlySet<IgnoreOptionId> previousSelections) =>
        SelectionRefreshPolicy.ShouldUseIgnoreDefaultFallback(
            _session.PreparedMode,
            options,
            previousSelections);

    private void UpdateRootSelectionCache()
    {
        _session.RootFolders.UpdateFromVisibleOptions(
            viewModel.RootFolders.Select(static option => new SelectionOption(option.Name, option.IsChecked)));
    }

}
