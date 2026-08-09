using DevProjex.Application.Models;
using DevProjex.Application.Context;
using DevProjex.Application.Secrets;
using DevProjex.Avalonia.Collections;
using DevProjex.Avalonia.Services;

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
    StatusOperationCoordinator? statusOperations = null,
    Action<IgnoreOptionId?>? contentTransformationChanged = null,
    Action? selectionContentChanged = null)
    : IDisposable
{
    // Store collection references for proper cleanup
    private ObservableCollection<SelectionOptionViewModel>? _hookedExtensions;
    private ObservableCollection<IgnoreOptionViewModel>? _hookedIgnoreOptions;
    private readonly HashSet<SelectionOptionViewModel> _subscribedExtensionItems =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IgnoreOptionViewModel> _subscribedIgnoreItems =
        new(ReferenceEqualityComparer.Instance);

    // Named handlers for proper unsubscription
    private NotifyCollectionChangedEventHandler? _extensionsCollectionChangedHandler;
    private NotifyCollectionChangedEventHandler? _ignoreOptionsCollectionChangedHandler;

    private bool _disposed;

    private IReadOnlyList<IgnoreOptionDescriptor> _ignoreOptions = [];
    private readonly ProjectSelectionSessionState _session = new();
    private readonly List<string> _scanRoots = [];
    private bool _hasExtensionlessExtensionEntries;
    private int _extensionlessExtensionEntriesCount;
    private bool _hasIgnoreOptionCounts;
    private IgnoreOptionCounts _ignoreOptionCounts;
    private IgnoreControllerImpactCounts _ignoreControllerImpactCounts;
    private GitWorkspaceEvidence _gitWorkspaceEvidence;

    private bool _suppressExtensionAllCheck;
    private bool _suppressExtensionItemCheck;
    private bool _suppressIgnoreAllCheck;
    private bool _suppressIgnoreItemCheck;
    private int _extensionScanVersion;
    private int _ignoreOptionsVersion;
    private SemaphoreSlim _refreshLock = new(1, 1);
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
    // Retain only the last stable option presentation. Never keep the tree inventory here:
    // cancellation must be cheap and independent of workspace size.
    private SelectionRefreshRollbackSnapshot? _stableSelectionSnapshot;
    private SelectionRefreshRollbackSnapshot? _reversibleSelectionSnapshot;
    private AppliedSelectionState? _appliedSelectionState;
    private ProjectContextGitReadiness _appliedGitReadiness =
        ProjectContextGitReadiness.Evaluate(GitFilteringMode.None, 0, 0);
    private int _pendingApplyEvaluationDeferral;
    private bool _pendingApplyEvaluationRequested;
    private readonly IgnoreRulesBuildCache _ignoreRulesBuildCache = new(buildIgnoreRules);
    private static readonly TraceSource RefreshTraceSource = new("DevProjex.SelectionRefresh");
    private readonly SelectionRefreshEngine _selectionRefreshEngine = new(
        scanOptions,
        filterSelectionService,
        ignoreOptionsService,
        buildIgnoreRules,
        getIgnoreOptionsAvailability);

    public long CurrentSelectionRevision => _session.Revision;
    public ProjectContextGitReadiness AppliedGitReadiness => _appliedGitReadiness;

    public void AcceptCurrentSelectionsAsApplied(
        string projectPath,
        ProjectTreeInventorySnapshot? inventory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        _appliedSelectionState = AppliedSelectionState.Capture(projectPath, viewModel);
        _appliedGitReadiness = ProjectContextGitReadiness.Evaluate(
            GitFilteringModeResolver.Resolve(_session.IgnoreOptions.OptionStateCache),
            inventory);
        viewModel.SetPendingFilterSettingsChanges(false);
    }

    public ContextDiagnostic? GetAppliedGitReadinessDiagnostic(
        string projectPath,
        GitFilteringMode? requiredMode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        if (requiredMode == GitFilteringMode.TrackedFilesOnly &&
            _appliedGitReadiness.Mode != GitFilteringMode.TrackedFilesOnly)
        {
            return ProjectContextGitReadiness
                .Evaluate(GitFilteringMode.TrackedFilesOnly, 0, 0)
                .CreateDiagnostic(projectPath);
        }

        return _appliedGitReadiness.CreateDiagnostic(projectPath);
    }

    public void ReevaluatePendingApplyChanges() =>
        RequestPendingApplyEvaluation();

    public void ClearAppliedSelectionState()
    {
        _appliedSelectionState = null;
        _appliedGitReadiness = ProjectContextGitReadiness.Evaluate(
            GitFilteringMode.None,
            0,
            0);
        _pendingApplyEvaluationRequested = false;
        viewModel.SetPendingFilterSettingsChanges(false);
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
        if (_hookedExtensions is null)
        {
            _hookedExtensions = options;
            _extensionsCollectionChangedHandler = CreateSelectionCollectionChangedHandler(
                options,
                _subscribedExtensionItems);
        }
        else if (ReferenceEquals(options, _hookedExtensions))
        {
            SynchronizeSelectionItemSubscriptions(options, _subscribedExtensionItems);
            return;
        }
        else
        {
            throw new InvalidOperationException("Only the extension option collection can be hooked.");
        }

        foreach (var item in options)
            SubscribeSelectionItem(item, _subscribedExtensionItems);

        options.CollectionChanged += _extensionsCollectionChangedHandler;
    }

    private NotifyCollectionChangedEventHandler CreateSelectionCollectionChangedHandler(
        ObservableCollection<SelectionOptionViewModel> options,
        HashSet<SelectionOptionViewModel> subscribedItems)
    {
        return (_, e) =>
        {
            if (e.OldItems is not null)
            {
                foreach (SelectionOptionViewModel item in e.OldItems)
                    UnsubscribeSelectionItem(item, subscribedItems);
            }

            if (e.NewItems is not null)
            {
                foreach (SelectionOptionViewModel item in e.NewItems)
                    SubscribeSelectionItem(item, subscribedItems);
            }

            if (e.Action == NotifyCollectionChangedAction.Reset)
                SynchronizeSelectionItemSubscriptions(options, subscribedItems);
        };
    }

    public void HookIgnoreListeners(ObservableCollection<IgnoreOptionViewModel> options)
    {
        if (ReferenceEquals(options, _hookedIgnoreOptions))
        {
            SynchronizeIgnoreItemSubscriptions(options);
            return;
        }

        if (_hookedIgnoreOptions is not null)
            throw new InvalidOperationException("Only one ignore option collection can be hooked.");

        _hookedIgnoreOptions = options;

        foreach (var item in options)
            SubscribeIgnoreItem(item);

        _ignoreOptionsCollectionChangedHandler = (_, e) =>
        {
            if (e.OldItems is not null)
            {
                foreach (IgnoreOptionViewModel item in e.OldItems)
                    UnsubscribeIgnoreItem(item);
            }

            if (e.NewItems is not null)
            {
                foreach (IgnoreOptionViewModel item in e.NewItems)
                    SubscribeIgnoreItem(item);
            }

            if (e.Action == NotifyCollectionChangedAction.Reset)
                SynchronizeIgnoreItemSubscriptions(options);
        };

        options.CollectionChanged += _ignoreOptionsCollectionChangedHandler;
    }

    private void SynchronizeSelectionItemSubscriptions(
        ObservableCollection<SelectionOptionViewModel> options,
        HashSet<SelectionOptionViewModel> subscribedItems)
    {
        var currentItems = options.ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var item in subscribedItems.ToArray())
        {
            if (!currentItems.Contains(item))
                UnsubscribeSelectionItem(item, subscribedItems);
        }

        foreach (var item in options)
            SubscribeSelectionItem(item, subscribedItems);
    }

    private void SubscribeSelectionItem(
        SelectionOptionViewModel item,
        HashSet<SelectionOptionViewModel> subscribedItems)
    {
        if (subscribedItems.Add(item))
            item.CheckedChanged += OnOptionCheckedChanged;
    }

    private void UnsubscribeSelectionItem(
        SelectionOptionViewModel item,
        HashSet<SelectionOptionViewModel> subscribedItems)
    {
        if (subscribedItems.Remove(item))
            item.CheckedChanged -= OnOptionCheckedChanged;
    }

    private void SynchronizeIgnoreItemSubscriptions(
        ObservableCollection<IgnoreOptionViewModel> options)
    {
        var currentItems = options.ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var item in _subscribedIgnoreItems.ToArray())
        {
            if (!currentItems.Contains(item))
                UnsubscribeIgnoreItem(item);
        }

        foreach (var item in options)
            SubscribeIgnoreItem(item);
    }

    private void SubscribeIgnoreItem(IgnoreOptionViewModel item)
    {
        if (_subscribedIgnoreItems.Add(item))
            item.CheckedChanged += OnIgnoreCheckedChanged;
    }

    private void UnsubscribeIgnoreItem(IgnoreOptionViewModel item)
    {
        if (_subscribedIgnoreItems.Remove(item))
            item.CheckedChanged -= OnIgnoreCheckedChanged;
    }

    public void HandleExtensionsAllChanged(bool isChecked)
    {
        if (_suppressExtensionAllCheck) return;

        _suppressExtensionAllCheck = true;
        viewModel.AllExtensionsChecked = isChecked;
        _suppressExtensionAllCheck = false;

        SetAllChecked(viewModel.Extensions, isChecked, ref _suppressExtensionItemCheck);
        UpdateExtensionsSelectionCache();
        _session.AdvanceRevision();
        RequestPendingApplyEvaluation();

        // Bulk extension toggles suppress individual item events, so refresh live
        // ignore counts explicitly to keep EmptyFolders aligned with tree semantics.
        QueueLiveOptionsRefresh(currentPathProvider(), SelectionRefreshOrigin.ExtensionSelection);
    }

    public void HandleIgnoreAllChanged(bool isChecked, string? currentPath)
    {
        if (_suppressIgnoreAllCheck) return;

        _session.IgnoreOptions.IsInitialized = true;
        _session.IgnoreOptions.AllPreference = isChecked;
		// "All" governs path filters only: a content transformation is not something the user
		// asked for by ticking every ignore row.
		_session.IgnoreOptions.ApplyAllPreferenceToKnownStates(
			isChecked,
			ProjectPresentationCatalog.ContentTransformationOptionIds);

        _suppressIgnoreAllCheck = true;
        viewModel.AllIgnoreChecked = isChecked;
        _suppressIgnoreAllCheck = false;

        SetAllIgnoreOptionsChecked(isChecked);
        UpdateIgnoreSelectionCache();
        _session.AdvanceRevision();
        RequestPendingApplyEvaluation();
        if (!string.IsNullOrEmpty(currentPath))
        {
            QueueFullRefresh(currentPath, changedIgnoreOptionId: null);
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
        var extensionScanRules = IgnoreRulesProjection.ForExtensionAvailability(ignoreRules);
        var forceAllExtensionsChecked =
            !ShouldSuppressAllTogglesOverride() && ResolveAllExtensionsCheckedForRefresh();
        const bool includeDirectoryToggleProbeRoots = true;
        const bool includeControllerImpactProbeRoots = true;
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
            var visibleExtensions = new List<string>(scan.Value.VisibleExtensions.Count);
            var extensionlessEntriesCount =
                ExtensionOptionProjection.SplitAvailableEntries(scan.Value.VisibleExtensions, visibleExtensions);
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
                    ExtensionOptionProjection.BuildResolvedPolicy(options),
                    includeDirectoryToggleProbeRoots,
                    cancellationToken,
                    includeControllerImpactProbeRoots);

                visibleExtensions = new List<string>(scan.Value.VisibleExtensions.Count);
                extensionlessEntriesCount =
                    ExtensionOptionProjection.SplitAvailableEntries(scan.Value.VisibleExtensions, visibleExtensions);
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
        var scanRoots = GetProjectScanRoots();
        var previousSelections = _session.IgnoreOptions.SnapshotSelectedOptions();
        var hasPreviousSelections = _session.IgnoreOptions.IsInitialized;
        var availability = ResolveIgnoreOptionsAvailability(path, scanRoots);
        var options = ignoreOptionsService.GetOptions(availability);
        ApplyIgnoreOptions(options, previousSelections, hasPreviousSelections);
    }

    public void RelabelIgnoreOptions(
	    bool showAdvancedCounts,
	    int? secretRedactionsCount = null,
	    SecretScanState secretScanState = SecretScanState.Disabled,
	    int? secretMatchesCount = null,
	    int? compressedFilesCount = null,
	    int? uncompressedFilesCount = null)
    {
        if (viewModel.IgnoreOptions.Count == 0)
            return;

		var visibleIds = viewModel.IgnoreOptions
			.Select(static option => option.Id)
			.ToHashSet();
		// Specifically Hide Secrets, not "any transformation": the secret counters must never be
		// attached to a row that did not produce them.
		var hideSecretsIsChecked = viewModel.IgnoreOptions.Any(static option =>
			option.Id == IgnoreOptionId.HideSecrets && option.IsChecked);
		var compressCodeIsChecked = viewModel.IgnoreOptions.Any(static option =>
			option.Id == IgnoreOptionId.CompressCode && option.IsChecked);
        var counts = _ignoreOptionCounts;
        var availability = new IgnoreOptionsAvailability(
            IncludeGitIgnore: visibleIds.Contains(IgnoreOptionId.UseGitIgnore),
            IncludeSmartIgnore: visibleIds.Contains(IgnoreOptionId.SmartIgnore),
            IncludeHiddenFolders: visibleIds.Contains(IgnoreOptionId.HiddenFolders),
            HiddenFoldersCount: counts.HiddenFolders,
            IncludeHiddenFiles: visibleIds.Contains(IgnoreOptionId.HiddenFiles),
            HiddenFilesCount: counts.HiddenFiles,
            IncludeDotFolders: visibleIds.Contains(IgnoreOptionId.DotFolders),
            DotFoldersCount: counts.DotFolders,
            IncludeDotFiles: visibleIds.Contains(IgnoreOptionId.DotFiles),
            DotFilesCount: counts.DotFiles,
            IncludeEmptyFolders: visibleIds.Contains(IgnoreOptionId.EmptyFolders),
            EmptyFoldersCount: counts.EmptyFolders,
            IncludeExtensionlessFiles: visibleIds.Contains(IgnoreOptionId.ExtensionlessFiles),
            ExtensionlessFilesCount: counts.ExtensionlessFiles,
            IncludeEmptyFiles: visibleIds.Contains(IgnoreOptionId.EmptyFiles),
            EmptyFilesCount: counts.EmptyFiles,
			IncludeTrackedGitFilesOnly: visibleIds.Contains(IgnoreOptionId.TrackedGitFilesOnly),
			SecretRedactionsCount: hideSecretsIsChecked ? secretRedactionsCount : null,
			SecretMatchesCount: hideSecretsIsChecked ? secretMatchesCount : null,
			CompressedFilesCount: compressCodeIsChecked ? compressedFilesCount : null,
			UncompressedFilesCount: compressCodeIsChecked ? uncompressedFilesCount : null,
            ShowAdvancedCounts: showAdvancedCounts);
        var localizedDescriptors = ignoreOptionsService.GetOptions(availability);
        var descriptorsById = localizedDescriptors.ToDictionary(static descriptor => descriptor.Id);

        foreach (var option in viewModel.IgnoreOptions)
        {
			// Hide Secrets carries a live scan state that the availability snapshot cannot express,
			// so it is formatted here. Every other transformation takes the label the catalog
			// dispatched for it - otherwise a second row silently inherits this one's name.
			if (option.Id == IgnoreOptionId.HideSecrets)
			{
				option.Label = ignoreOptionsService.FormatHideSecretsLabel(
					hideSecretsIsChecked ? secretScanState : SecretScanState.Disabled,
					secretMatchesCount,
					secretRedactionsCount);
				continue;
			}
            if (descriptorsById.TryGetValue(option.Id, out var descriptor))
                option.Label = descriptor.Label;
        }
		_ignoreOptions = localizedDescriptors;
        SynchronizeStableIgnoreOptionLabels();
    }

    public IReadOnlyCollection<string> GetProjectScanRoots()
    {
        return _scanRoots.ToArray();
    }

    public void ApplyProjectProfileSelections(string projectPath, ProjectSelectionProfile profile)
    {
        _session.ApplyProfile(projectPath, profile);
		_session.AdvanceRevision();
    }

    public void ResetProjectProfileSelections(string projectPath)
    {
        _session.ResetToDefaultsForProject(projectPath);
        _session.AdvanceRevision();

        // Restore defaults for projects without a saved profile.
        viewModel.AllExtensionsChecked = true;
        viewModel.AllIgnoreChecked = true;
    }

    public async Task UpdateLiveOptionsForProjectScopeAsync(
        string? currentPath,
        CancellationToken cancellationToken = default)
    {
        await UpdateLiveOptionsForProjectScopeCoreAsync(
            currentPath,
            expectedRequestVersion: null,
            SelectionRefreshOrigin.Unknown,
            changedIgnoreOptionId: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateLiveOptionsForProjectScopeIfDirtyAsync(
        string? currentPath,
        CancellationToken cancellationToken = default)
    {
        if (!HasDirtySelectionRefresh())
            return;

        await UpdateLiveOptionsForProjectScopeAsync(currentPath, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task UpdateLiveOptionsForProjectScopeCoreAsync(
        string? currentPath,
        int? expectedRequestVersion,
        SelectionRefreshOrigin origin,
        IgnoreOptionId? changedIgnoreOptionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(currentPath)) return;
        if (IsStalePathRequest(currentPath)) return;
        if (IsSupersededLiveOptionsRequest(expectedRequestVersion)) return;
        cancellationToken.ThrowIfCancellationRequested();

        var refreshLock = Volatile.Read(ref _refreshLock);
        await refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
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

                if (TryRestoreKnownSelectionSnapshot(currentPath, origin, changedIgnoreOptionId))
                    return null;

                return new LiveRefreshInput(CreateSelectionRefreshContext(currentPath));
            });
            if (liveInput is null)
                return;

            var snapshot = await Task.Run(
                () => _selectionRefreshEngine.ComputeLiveRefreshSnapshot(
                    liveInput.Context,
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

                ApplyLiveSelectionRefreshSnapshot(snapshot);
            });
        }
        finally
        {
            refreshLock.Release();
        }
    }

    public async Task RefreshProjectSelectionAsync(string currentPath, CancellationToken cancellationToken = default)
    {
        await RefreshProjectSelectionCoreAsync(
            currentPath,
            expectedRequestVersion: null,
            changedIgnoreOptionId: null,
            cancellationToken).ConfigureAwait(false);
    }

    public void InvalidateFileSystemCaches()
    {
        _selectionRefreshEngine.InvalidateCaches();
        _ignoreRulesBuildCache.Invalidate();
    }

    public async Task<SelectionRefreshSnapshot?> BuildProjectSelectionSnapshotAsync(
        string currentPath,
        CancellationToken cancellationToken = default)
    {
        return await BuildProjectSelectionSnapshotCoreAsync(
            currentPath,
            expectedRequestVersion: null,
            cancellationToken).ConfigureAwait(false);
    }

    public bool ApplyProjectSelectionSnapshot(string currentPath, SelectionRefreshSnapshot snapshot)
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

    private async Task RefreshProjectSelectionCoreAsync(
        string currentPath,
        int? expectedRequestVersion,
        IgnoreOptionId? changedIgnoreOptionId,
        CancellationToken cancellationToken)
    {
        // Serialize refresh operations to prevent race conditions
        var refreshLock = Volatile.Read(ref _refreshLock);
        await refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
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

                // Queued full refreshes originate from ignore-option changes. A direct
                // return to either of the two known states does not require another scan.
                if (expectedRequestVersion.HasValue &&
                    TryRestoreKnownSelectionSnapshot(
                        currentPath,
                        SelectionRefreshOrigin.IgnoreOption,
                        changedIgnoreOptionId))
                    return null;

                // UI collections and selection caches are Avalonia-owned state. Capture and
                // cache reset must happen on the UI thread; the expensive scan runs after this.
                if (ShouldClearCachesForCurrentPath(currentPath))
                    ClearCachesForNewProject();

                _session.LastLoadedPath = currentPath;
                MarkSelectionRefreshDirty();
                return CreateSelectionRefreshContext(currentPath, captureTreeInventory: true);
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

                ApplySelectionRefreshSnapshot(
                    snapshot,
                    retainPreviousSnapshot: expectedRequestVersion.HasValue);

                // Consume prepared selection only after the matching snapshot is applied.
                // Keeping this with the UI mutation prevents stale background refreshes from
                // clearing the prepared state between capture and apply.
                _session.ConsumePreparedSelectionForPath(currentPath);
            });
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private async Task<SelectionRefreshSnapshot?> BuildProjectSelectionSnapshotCoreAsync(
        string currentPath,
        int? expectedRequestVersion,
        CancellationToken cancellationToken)
    {
        var refreshLock = Volatile.Read(ref _refreshLock);
        await refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            refreshLock.Release();
        }
    }

    public async Task WaitForPendingRefreshesAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var refreshLock = Volatile.Read(ref _refreshLock);
            await refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            refreshLock.Release();

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

    public bool CancelPendingRefreshes()
    {
        var shouldRestoreStableSelection = HasDirtySelectionRefresh();
		var transformationsWereChecked = viewModel.IgnoreOptions
			.Where(static option => ProjectPresentationCatalog.ContentTransformationOptionIds.Contains(option.Id))
			.Where(static option => option.IsChecked)
			.Select(static option => option.Id)
			.ToHashSet();
        lock (_backgroundRefreshSync)
        {
            _liveOptionsRefreshCts?.Cancel();
            _fullRefreshRequestCts?.Cancel();
            _liveOptionsRequestVersion = unchecked(_liveOptionsRequestVersion + 1);
            _fullRefreshRequestVersion = unchecked(_fullRefreshRequestVersion + 1);
        }

        var snapshot = _stableSelectionSnapshot;
        if (!shouldRestoreStableSelection || snapshot is null)
            return false;

		RestoreStableSelectionSnapshot(snapshot);
		var transformationsAreChecked = viewModel.IgnoreOptions
			.Where(static option => ProjectPresentationCatalog.ContentTransformationOptionIds.Contains(option.Id))
			.Where(static option => option.IsChecked)
			.Select(static option => option.Id)
			.ToHashSet();
		if (!transformationsWereChecked.SetEquals(transformationsAreChecked))
		{
			// Rollback is a real content-state transition. Notify the output pipeline just as
			// an ordinary checkbox change would, otherwise Preview and the measured count lag
			// behind the selection visibly restored to the user.
			contentTransformationChanged?.Invoke(
				ResolveChangedTransformation(
					transformationsWereChecked,
					transformationsAreChecked));
		}
        return true;
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
        _gitWorkspaceEvidence = GitWorkspaceEvidence.Empty;
        _stableSelectionSnapshot = null;
        _reversibleSelectionSnapshot = null;

        // Clear ignore options
        _ignoreOptions = [];
        _ignoreRulesBuildCache.Invalidate();
    }

    /// <summary>
    /// Unsubscribes from CheckedChanged events on all option items.
    /// </summary>
    private void UnsubscribeFromOptionItems()
    {
        foreach (var item in _subscribedExtensionItems)
            item.CheckedChanged -= OnOptionCheckedChanged;
        _subscribedExtensionItems.Clear();

        foreach (var item in _subscribedIgnoreItems)
            item.CheckedChanged -= OnIgnoreCheckedChanged;
        _subscribedIgnoreItems.Clear();
    }

    public IReadOnlyCollection<IgnoreOptionId> GetSelectedIgnoreOptionIds()
    {
        EnsureIgnoreSelectionCache();
        UpdateIgnoreSelectionCache();
        return SnapshotRuntimeSelectedIgnoreOptions();
    }

    public void ApplyIgnoreSelectionOverride(
        IReadOnlySet<IgnoreOptionId> selectedOptions)
    {
        ArgumentNullException.ThrowIfNull(selectedOptions);

        var stateCache = _session.IgnoreOptions.SnapshotStateCache();
        foreach (var option in _ignoreOptions)
            stateCache[option.Id] = selectedOptions.Contains(option.Id);
        foreach (var option in viewModel.IgnoreOptions)
            stateCache[option.Id] = selectedOptions.Contains(option.Id);

        // Preserve the established public method contract. The batched Desktop path uses
        // ApplySelectionOverrides instead, so this compatibility surface stays independent.
        stateCache[IgnoreOptionId.UseGitIgnore] =
            selectedOptions.Contains(IgnoreOptionId.UseGitIgnore);
        stateCache[IgnoreOptionId.TrackedGitFilesOnly] =
            selectedOptions.Contains(IgnoreOptionId.TrackedGitFilesOnly);

        _session.IgnoreOptions.ReplaceStateCache(stateCache);
        _session.IgnoreOptions.AllPreference = null;
        _session.AdvanceRevision();
        RequestPendingApplyEvaluation();
    }

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
        var scanRoots = GetProjectScanRoots();
        var availability = ResolveIgnoreOptionsAvailability(path, scanRoots);
        _ignoreOptions = ignoreOptionsService.GetOptions(availability);
        _session.IgnoreOptions.EnsureDefaults(_ignoreOptions);
    }

    private IgnoreOptionsAvailability ResolveIgnoreOptionsAvailability(
        string? path,
        IReadOnlyCollection<string> selectedRootFolders)
    {
        if (string.IsNullOrWhiteSpace(path))
            return IgnoreOptionsAvailabilityResolver.CreateUnmeasured(
                includeGitIgnore: false,
                includeSmartIgnore: false);

        try
        {
            var snapshotState = new IgnoreSectionSnapshotState(
                _hasIgnoreOptionCounts,
                _ignoreOptionCounts,
                _ignoreControllerImpactCounts,
                _hasExtensionlessExtensionEntries,
                _extensionlessExtensionEntriesCount,
                _gitWorkspaceEvidence);
            return IgnoreOptionsAvailabilityResolver.Resolve(
                getIgnoreOptionsAvailability(path, selectedRootFolders),
                snapshotState,
                _session.IgnoreOptions.OptionStateCache,
                _session.IgnoreOptionStateCacheIsComplete);
        }
        catch
        {
            return IgnoreOptionsAvailabilityResolver.CreateUnmeasured(
                includeGitIgnore: false,
                includeSmartIgnore: false);
        }
    }

    private void ApplyIgnoreOptions(
        IReadOnlyList<IgnoreOptionDescriptor> options,
        IReadOnlySet<IgnoreOptionId> previousSelections,
        bool hasPreviousSelections)
    {
        var useDefaultCheckedFallback = ShouldUseIgnoreDefaultFallback(options, previousSelections);
        var controllerGroupEndIndex = FindLastControllerOptionIndex(
            options,
            static option => option.Id);
        var optionViewModels = new List<IgnoreOptionViewModel>(options.Count);
        for (var index = 0; index < options.Count; index++)
        {
            var option = options[index];
            var isChecked = ResolveIgnoreOptionCheckedState(
                option,
                previousSelections,
                hasPreviousSelections,
                useDefaultCheckedFallback);
            optionViewModels.Add(new IgnoreOptionViewModel(
                option.Id,
                option.Label,
                isChecked,
                isControllerGroupEnd: index == controllerGroupEndIndex));
        }
        NormalizeGitFilteringOptions(
            optionViewModels,
            ResolvePreferredGitFilteringMode(previousSelections));

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
        SynchronizeStableIgnoreOptionLabels();
        RequestPendingApplyEvaluation();
    }

    private static int FindLastControllerOptionIndex<T>(
        IReadOnlyList<T> options,
        Func<T, IgnoreOptionId> idSelector)
    {
        for (var index = options.Count - 1; index >= 0; index--)
        {
            if (idSelector(options[index]) is IgnoreOptionId.UseGitIgnore
                or IgnoreOptionId.TrackedGitFilesOnly
                or IgnoreOptionId.SmartIgnore)
            {
                return index;
            }
        }

        return -1;
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
        var extensionlessEntriesCount =
            ExtensionOptionProjection.SplitAvailableEntries(extensions, visibleExtensions);
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
        _suppressIgnoreAllCheck = true;
        try
        {
            var hasItems = false;
            var hasGitFilteringOptions = false;
            var hasSelectedGitFilteringMode = false;
            var allOrdinaryOptionsChecked = true;
            foreach (var option in viewModel.IgnoreOptions)
            {
				if (ProjectPresentationCatalog.ContentTransformationOptionIds.Contains(option.Id))
					continue;
                hasItems = true;
                if (GitFilteringModeResolver.IsGitFilteringOption(option.Id))
                {
                    hasGitFilteringOptions = true;
                    hasSelectedGitFilteringMode |= option.IsChecked;
                    continue;
                }

                if (!option.IsChecked)
                    allOrdinaryOptionsChecked = false;
            }

            viewModel.AllIgnoreChecked =
                hasItems &&
                allOrdinaryOptionsChecked &&
                (!hasGitFilteringOptions || hasSelectedGitFilteringMode);
        }
        finally
        {
            _suppressIgnoreAllCheck = false;
        }
    }

    private void OnOptionCheckedChanged(object? sender, EventArgs e)
    {
        if (sender is not SelectionOptionViewModel option)
            return;

        if (viewModel.Extensions.Contains(option))
        {
            if (_suppressExtensionItemCheck) return;

            SyncAllCheckbox(viewModel.Extensions, ref _suppressExtensionAllCheck,
                value => viewModel.AllExtensionsChecked = value);

            UpdateExtensionsSelectionCache();
            _session.AdvanceRevision();
            RequestPendingApplyEvaluation();
			selectionContentChanged?.Invoke();
            QueueLiveOptionsRefresh(currentPathProvider(), SelectionRefreshOrigin.ExtensionSelection);
        }
    }

    private void OnIgnoreCheckedChanged(object? sender, EventArgs e)
    {
        if (_suppressIgnoreItemCheck) return;

        var changedOption = sender as IgnoreOptionViewModel;
        _session.IgnoreOptions.IsInitialized = true;
        _session.IgnoreOptions.AllPreference = null;

        if (changedOption is not null &&
            GitFilteringModeResolver.IsGitFilteringOption(changedOption.Id))
        {
            ApplyGitFilteringCheckboxTransition(changedOption);
        }

        SyncIgnoreAllCheckbox();

        UpdateIgnoreSelectionCache();
		var changedTransformation = changedOption is not null &&
			ProjectPresentationCatalog.ContentTransformationOptionIds.Contains(changedOption.Id);
		if (!changedTransformation)
			_session.AdvanceRevision();
        RequestPendingApplyEvaluation();

        var currentPath = currentPathProvider();
		if (changedTransformation)
		{
			// A content transformation changes produced content, never filesystem visibility.
			// Rebuild the preview from the current selection without an unrelated tree scan.
			contentTransformationChanged?.Invoke(changedOption!.Id);
			return;
		}
		selectionContentChanged?.Invoke();
        if (!string.IsNullOrEmpty(currentPath))
        {
            QueueRefreshForIgnoreOptionChange(currentPath, changedOption?.Id);
        }
    }

	private static IgnoreOptionId? ResolveChangedTransformation(
		IReadOnlySet<IgnoreOptionId> before,
		IReadOnlySet<IgnoreOptionId> after)
	{
		var changed = before
			.Where(optionId => !after.Contains(optionId))
			.Concat(after.Where(optionId => !before.Contains(optionId)))
			.Take(2)
			.ToArray();
		return changed.Length == 1 ? changed[0] : null;
	}

    private void QueueRefreshForIgnoreOptionChange(string currentPath, IgnoreOptionId? changedOptionId)
    {
        if (SelectionRefreshRoutingPolicy.CanUseLiveOptionsRefresh(changedOptionId))
        {
            QueueLiveOptionsRefresh(
                currentPath,
                SelectionRefreshOrigin.IgnoreOption,
                changedOptionId);
            return;
        }

        QueueFullRefresh(currentPath, changedOptionId);
    }

    /// <summary>
    /// Coalesces rapid root-selection changes and keeps only the latest live-options refresh.
    /// </summary>
    private void QueueLiveOptionsRefresh(
        string? currentPath,
        SelectionRefreshOrigin origin,
        IgnoreOptionId? changedIgnoreOptionId = null)
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
            queuedTask = RunQueuedLiveOptionsRefreshAsync(
                currentPath,
                version,
                origin,
                changedIgnoreOptionId,
                token,
                cancelAction);
            _latestLiveOptionsRefreshTask = queuedTask;
        }

        DisposeCancellationSourceWhenTaskCompletes(previousCts, previousTask);
        FireAndForgetSafe(queuedTask, "live-options refresh");
    }

    /// <summary>
    /// Coalesces rapid ignore-option changes and keeps only the latest full refresh request.
    /// </summary>
    private void QueueFullRefresh(string? currentPath, IgnoreOptionId? changedIgnoreOptionId)
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
            queuedTask = RunQueuedFullRefreshAsync(
                currentPath,
                version,
                changedIgnoreOptionId,
                token,
                cancelAction);
            _latestFullRefreshTask = queuedTask;
        }

        DisposeCancellationSourceWhenTaskCompletes(invalidatedLiveCts, invalidatedLiveTask);
        DisposeCancellationSourceWhenTaskCompletes(previousCts, previousTask);
        FireAndForgetSafe(queuedTask, "full selection refresh");
    }

    private async Task RunQueuedLiveOptionsRefreshAsync(
        string currentPath,
        int version,
        SelectionRefreshOrigin origin,
        IgnoreOptionId? changedIgnoreOptionId,
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
        try
        {
            await UpdateLiveOptionsForProjectScopeCoreAsync(
                currentPath,
                version,
                origin,
                changedIgnoreOptionId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await RestoreStableSelectionAfterFailedRefreshAsync(
                currentPath,
                version,
                isFullRefresh: false).ConfigureAwait(false);
            throw;
        }
    }

    private async Task RunQueuedFullRefreshAsync(
        string currentPath,
        int version,
        IgnoreOptionId? changedIgnoreOptionId,
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
        try
        {
            await RefreshProjectSelectionCoreAsync(
                currentPath,
                version,
                changedIgnoreOptionId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await RestoreStableSelectionAfterFailedRefreshAsync(
                currentPath,
                version,
                isFullRefresh: true).ConfigureAwait(false);
            throw;
        }
    }

    private async Task RestoreStableSelectionAfterFailedRefreshAsync(
        string currentPath,
        int version,
        bool isFullRefresh)
    {
        await RunOnUiThreadAsync(() =>
        {
            var isCurrentRequest = isFullRefresh
                ? version == Volatile.Read(ref _fullRefreshRequestVersion)
                : version == Volatile.Read(ref _liveOptionsRequestVersion);
            if (!isCurrentRequest || IsStalePathRequest(currentPath))
                return false;

            if (_stableSelectionSnapshot is { } snapshot &&
                PathComparer.Default.Equals(snapshot.Path, currentPath))
            {
                _stableSelectionSnapshot = RestoreStableSelectionSnapshot(snapshot);
            }
            else
            {
                MarkSelectionRefreshClean();
            }

            return true;
        }).ConfigureAwait(false);
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

        if (SelectionOptionIdentitiesMatch(viewModel.Extensions, options))
        {
            _suppressExtensionItemCheck = true;
            try
            {
                UpdateSelectionOptionStates(viewModel.Extensions, options);
            }
            finally
            {
                _suppressExtensionItemCheck = false;
            }
        }
        else
        {
            var optionViewModels = new List<SelectionOptionViewModel>(options.Count);
            foreach (var option in options)
                optionViewModels.Add(new SelectionOptionViewModel(option.Name, option.IsChecked));

            _suppressExtensionItemCheck = true;
            ReplaceCollectionItems(viewModel.Extensions, optionViewModels);
            _suppressExtensionItemCheck = false;
        }

        if (!ShouldSuppressAllTogglesOverride() && ResolveAllExtensionsCheckedForRefresh())
            SetAllChecked(viewModel.Extensions, true, ref _suppressExtensionItemCheck);

        SyncAllCheckbox(viewModel.Extensions, ref _suppressExtensionAllCheck,
            value => viewModel.AllExtensionsChecked = value);
        if (!_session.Extensions.IsInitialized)
            UpdateExtensionsSelectionCache();
        RequestPendingApplyEvaluation();
    }

    private void ApplyScanRootOptions(IReadOnlyList<SelectionOption> options)
    {
        var nextRoots = options
            .Where(static option => option.IsChecked)
            .Select(static option => option.Name)
            .ToArray();
        if (_scanRoots.SequenceEqual(nextRoots, StringComparer.Ordinal))
            return;

        _scanRoots.Clear();
        _scanRoots.AddRange(nextRoots);
    }

    private void ApplyResolvedIgnoreOptions(
        IReadOnlyList<ResolvedIgnoreOptionState> options,
        IReadOnlyDictionary<IgnoreOptionId, bool> stateCache)
    {
        if (IgnoreOptionIdentitiesMatch(viewModel.IgnoreOptions, options))
        {
            _suppressIgnoreItemCheck = true;
            try
            {
                UpdateIgnoreOptionStates(viewModel.IgnoreOptions, options);
            }
            finally
            {
                _suppressIgnoreItemCheck = false;
            }
        }
        else
        {
            var controllerGroupEndIndex = FindLastControllerOptionIndex(
                options,
                static option => option.Id);
            var optionViewModels = new List<IgnoreOptionViewModel>(options.Count);
            for (var index = 0; index < options.Count; index++)
            {
                var option = options[index];
                optionViewModels.Add(new IgnoreOptionViewModel(
                    option.Id,
                    option.Label,
                    option.IsChecked,
                    isControllerGroupEnd: index == controllerGroupEndIndex));
            }

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

        if (!IgnoreOptionDescriptorsMatch(_ignoreOptions, options))
        {
            var descriptors = new List<IgnoreOptionDescriptor>(options.Count);
            foreach (var option in options)
                descriptors.Add(new IgnoreOptionDescriptor(option.Id, option.Label, option.DefaultChecked));

            _ignoreOptions = descriptors;
        }
        _session.IgnoreOptions.ReplaceStateCache(stateCache);
        _session.IgnoreOptionStateCacheIsComplete = true;
        SyncIgnoreAllCheckbox();
        RequestPendingApplyEvaluation();
    }

    private void ApplySelectionRefreshSnapshot(
        SelectionRefreshSnapshot snapshot,
        bool retainPreviousSnapshot = false) =>
        ApplySelectionRefreshSnapshotCore(
            snapshot,
            retainPreviousSnapshot,
            scanRootsAreAuthoritative: true);

    private void ApplyLiveSelectionRefreshSnapshot(SelectionRefreshSnapshot snapshot) =>
        ApplySelectionRefreshSnapshotCore(
            snapshot,
            retainPreviousSnapshot: true,
            scanRootsAreAuthoritative: false);

    private void ApplySelectionRefreshSnapshotCore(
        SelectionRefreshSnapshot snapshot,
        bool retainPreviousSnapshot,
        bool scanRootsAreAuthoritative)
    {
        // A full/live selection snapshot is the authoritative count-driven ignore state.
        // Invalidate older standalone availability refreshes so they cannot overwrite it.
        Interlocked.Increment(ref _ignoreOptionsVersion);

        BeginPendingApplyEvaluationDeferral();
        try
        {
            _gitWorkspaceEvidence = snapshot.GitEvidence;
            if (snapshot.RootOptions is not null)
                ApplyScanRootOptions(snapshot.RootOptions);

            ApplyExtensionOptions(
                snapshot.EffectiveExtensionOptions,
                snapshot.ExtensionlessEntriesCount,
                snapshot.IgnoreOptionCounts,
                snapshot.ControllerImpactCounts,
                snapshot.HasIgnoreOptionCounts);

            ApplyResolvedIgnoreOptions(snapshot.IgnoreOptions, snapshot.IgnoreOptionStateCache);
        }
        finally
        {
            EndPendingApplyEvaluationDeferral();
        }
        _session.AdvanceRevision();
        MarkSelectionRefreshClean();
        var previousSnapshot = retainPreviousSnapshot ? _stableSelectionSnapshot : null;
        var appliedSnapshot = CaptureStableSelectionSnapshot(snapshot, scanRootsAreAuthoritative);
        _stableSelectionSnapshot = appliedSnapshot;
        _reversibleSelectionSnapshot = previousSnapshot is not null &&
                                       PathComparer.Default.Equals(previousSnapshot.Path, appliedSnapshot.Path)
            ? previousSnapshot
            : null;
    }

    private SelectionRefreshRollbackSnapshot CaptureStableSelectionSnapshot(
        SelectionRefreshSnapshot snapshot,
        bool scanRootsAreAuthoritative)
    {
        var rootOptions = ResolveStableScanRootOptions(
            snapshot.RootOptions,
            _stableSelectionSnapshot?.ScanRootOptions);
        var extensionOptions = ResolveStableSelectionOptions(
            viewModel.Extensions,
            snapshot.EffectiveExtensionOptions,
            _stableSelectionSnapshot?.ExtensionOptions);

        return new SelectionRefreshRollbackSnapshot(
            _session.LastLoadedPath ?? currentPathProvider() ?? string.Empty,
            rootOptions,
            extensionOptions,
            ResolveStableIgnoreOptions(snapshot.IgnoreOptions),
            snapshot.ExtensionlessEntriesCount,
            snapshot.HasIgnoreOptionCounts,
            snapshot.IgnoreOptionCounts,
            snapshot.ControllerImpactCounts,
            snapshot.IgnoreOptionStateCache,
            _session.Extensions.SnapshotSelectedNames(),
            new Dictionary<string, bool>(
                _session.Extensions.OptionStates,
                StringComparer.OrdinalIgnoreCase),
            _session.Extensions.IsInitialized,
            _session.Extensions.HasFullState,
            viewModel.AllExtensionsChecked,
            viewModel.AllIgnoreChecked,
            _session.IgnoreOptions.IsInitialized,
            _session.IgnoreOptions.AllPreference,
            _session.IgnoreOptionStateCacheIsComplete,
            // A live refresh can project known roots but cannot discover roots hidden by
            // its input filters. Structural rollback therefore requires a full snapshot.
            scanRootsAreAuthoritative && snapshot.RootOptions is not null,
            GitEvidence: snapshot.GitEvidence);
    }

    private bool TryRestoreKnownSelectionSnapshot(
        string currentPath,
        SelectionRefreshOrigin origin,
        IgnoreOptionId? changedIgnoreOptionId)
    {
        if (_stableSelectionSnapshot is { } stableSnapshot &&
            CurrentSelectionMatchesSnapshot(currentPath, stableSnapshot))
        {
            // A superseded refresh may already have changed count labels while the
            // checkbox state returned to stable. Reapply the authoritative presentation.
            _stableSelectionSnapshot = RestoreStableSelectionSnapshot(stableSnapshot);
            return true;
        }

        var reversibleSnapshot = _reversibleSelectionSnapshot;
        var currentStableSnapshot = _stableSelectionSnapshot;
        if (reversibleSnapshot is null ||
            currentStableSnapshot is null ||
            !CurrentSelectionCanRestoreReversibleSnapshot(
                currentPath,
                currentStableSnapshot,
                reversibleSnapshot,
                origin,
                changedIgnoreOptionId))
        {
            return false;
        }

        var previousStableSnapshot = _stableSelectionSnapshot;
        _stableSelectionSnapshot = RestoreStableSelectionSnapshot(reversibleSnapshot);
        _reversibleSelectionSnapshot = previousStableSnapshot;
        return true;
    }

    private bool CurrentSelectionMatchesSnapshot(
        string currentPath,
        SelectionRefreshRollbackSnapshot snapshot)
    {
        return PathComparer.Default.Equals(currentPath, snapshot.Path) &&
               CurrentScanRootScopeMatches(snapshot) &&
               CurrentExtensionSelectionMatches(snapshot) &&
               CurrentIgnoreSelectionMatches(snapshot);
    }

    private bool CurrentSelectionCanRestoreReversibleSnapshot(
        string currentPath,
        SelectionRefreshRollbackSnapshot stableSnapshot,
        SelectionRefreshRollbackSnapshot reversibleSnapshot,
        SelectionRefreshOrigin origin,
        IgnoreOptionId? changedIgnoreOptionId)
    {
        if (!PathComparer.Default.Equals(currentPath, stableSnapshot.Path) ||
            !PathComparer.Default.Equals(currentPath, reversibleSnapshot.Path))
        {
            return false;
        }

        if (origin == SelectionRefreshOrigin.IgnoreOption &&
            !SelectionRefreshRoutingPolicy.CanUseLiveOptionsRefresh(changedIgnoreOptionId) &&
            !reversibleSnapshot.HasAuthoritativeScanRoots)
        {
            // A live snapshot has no root inventory. Reusing it after a structural
            // ignore reversal could restore roots discovered under different filters.
            return false;
        }

        var rootMatchesKnownState = CurrentScanRootScopeMatches(stableSnapshot) ||
                                    CurrentScanRootScopeMatches(reversibleSnapshot);
        var extensionMatchesKnownState = CurrentExtensionSelectionMatches(stableSnapshot) ||
                                         CurrentExtensionSelectionMatches(reversibleSnapshot);
        var ignoreMatchesKnownState = CurrentIgnoreSelectionMatches(stableSnapshot) ||
                                      CurrentIgnoreSelectionMatches(reversibleSnapshot);

        // Restoring a whole snapshot is safe only when sections outside the initiating
        // toggle have no conflicting explicit preferences in the two known states.
        return origin switch
        {
            SelectionRefreshOrigin.ExtensionSelection =>
                rootMatchesKnownState &&
                CurrentExtensionSelectionMatches(reversibleSnapshot) &&
                ignoreMatchesKnownState &&
                IgnorePreferencesAreCompatible(stableSnapshot, reversibleSnapshot),
            SelectionRefreshOrigin.IgnoreOption =>
                rootMatchesKnownState &&
                extensionMatchesKnownState &&
                ExtensionPreferencesAreCompatible(stableSnapshot, reversibleSnapshot) &&
                CurrentIgnoreSelectionMatchesReversal(
                    stableSnapshot,
                    reversibleSnapshot,
                    changedIgnoreOptionId),
            _ => false
        };
    }

    private bool CurrentScanRootScopeMatches(SelectionRefreshRollbackSnapshot snapshot)
    {
        return ScanRootScopeMatches(snapshot.ScanRootOptions);
    }

    private bool CurrentExtensionSelectionMatches(SelectionRefreshRollbackSnapshot snapshot)
    {
        return viewModel.AllExtensionsChecked == snapshot.AllExtensionsChecked &&
               SelectionOptionStatesMatch(viewModel.Extensions, snapshot.ExtensionOptions) &&
               SetStatesMatch(_session.Extensions.SelectedNames, snapshot.SelectedExtensions) &&
               DictionaryStatesMatch(
                   _session.Extensions.OptionStates,
                   snapshot.ExtensionOptionStateCache) &&
               _session.Extensions.IsInitialized == snapshot.ExtensionSelectionInitialized &&
               _session.Extensions.HasFullState == snapshot.ExtensionOptionStateCacheIsComplete;
    }

    private bool CurrentIgnoreSelectionMatches(SelectionRefreshRollbackSnapshot snapshot)
    {
        return viewModel.AllIgnoreChecked == snapshot.AllIgnoreChecked &&
               IgnoreOptionCheckStatesMatch(viewModel.IgnoreOptions, snapshot.IgnoreOptions) &&
               DictionaryStatesMatch(
                   _session.IgnoreOptions.OptionStateCache,
                   snapshot.IgnoreOptionStateCache) &&
               _session.IgnoreOptions.IsInitialized == snapshot.IgnoreOptionsInitialized &&
               _session.IgnoreOptions.AllPreference == snapshot.IgnoreAllPreference &&
               _session.IgnoreOptionStateCacheIsComplete == snapshot.IgnoreOptionStateCacheIsComplete;
    }

    private bool CurrentIgnoreSelectionMatchesReversal(
        SelectionRefreshRollbackSnapshot stableSnapshot,
        SelectionRefreshRollbackSnapshot reversibleSnapshot,
        IgnoreOptionId? changedIgnoreOptionId)
    {
        if (!changedIgnoreOptionId.HasValue)
            return CurrentIgnoreSelectionMatches(reversibleSnapshot);

        var changedId = changedIgnoreOptionId.Value;
        if (!IgnoreOptionIdentitiesMatch(viewModel.IgnoreOptions, stableSnapshot.IgnoreOptions) ||
            !reversibleSnapshot.IgnoreOptionStateCache.TryGetValue(changedId, out var reversedState) ||
            !stableSnapshot.IgnoreOptionStateCache.ContainsKey(changedId) ||
            viewModel.AllIgnoreChecked != reversibleSnapshot.AllIgnoreChecked ||
            _session.IgnoreOptions.IsInitialized != reversibleSnapshot.IgnoreOptionsInitialized ||
            _session.IgnoreOptions.AllPreference != reversibleSnapshot.IgnoreAllPreference ||
            _session.IgnoreOptionStateCacheIsComplete != reversibleSnapshot.IgnoreOptionStateCacheIsComplete ||
            _session.IgnoreOptions.OptionStateCache.Count != stableSnapshot.IgnoreOptionStateCache.Count)
        {
            return false;
        }

        foreach (var option in viewModel.IgnoreOptions)
        {
            var stableState = stableSnapshot.IgnoreOptionStateCache.GetValueOrDefault(option.Id);
            if (!reversibleSnapshot.IgnoreOptionStateCache.TryGetValue(
                    option.Id,
                    out var reversibleState) ||
                (option.Id != changedId && reversibleState != stableState))
            {
                // Restoring a cached snapshot is valid only when that snapshot differs in
                // the initiating row alone. Git modes are a coupled checkbox pair, so a
                // transition can change both rows and must converge through a real refresh.
                return false;
            }

            var expectedState = option.Id == changedId
                ? reversedState
                : stableState;
            if (!stableSnapshot.IgnoreOptionStateCache.ContainsKey(option.Id) ||
                option.IsChecked != expectedState)
            {
                return false;
            }
        }

        foreach (var (optionId, isChecked) in _session.IgnoreOptions.OptionStateCache)
        {
            var stableState = stableSnapshot.IgnoreOptionStateCache.GetValueOrDefault(optionId);
            if (!reversibleSnapshot.IgnoreOptionStateCache.TryGetValue(
                    optionId,
                    out var reversibleState) ||
                (optionId != changedId && reversibleState != stableState))
            {
                return false;
            }

            var expectedState = optionId == changedId
                ? reversedState
                : stableState;
            if (!stableSnapshot.IgnoreOptionStateCache.ContainsKey(optionId) ||
                isChecked != expectedState)
            {
                return false;
            }
        }

        return true;
    }

    private SelectionRefreshRollbackSnapshot RestoreStableSelectionSnapshot(
        SelectionRefreshRollbackSnapshot snapshot)
    {
        snapshot = RetainUnknownSelectionStates(snapshot);

        _suppressExtensionAllCheck = true;
        _suppressIgnoreAllCheck = true;
        try
        {
            viewModel.AllExtensionsChecked = snapshot.AllExtensionsChecked;
            viewModel.AllIgnoreChecked = snapshot.AllIgnoreChecked;
        }
        finally
        {
            _suppressExtensionAllCheck = false;
            _suppressIgnoreAllCheck = false;
        }

        Interlocked.Increment(ref _ignoreOptionsVersion);
        BeginPendingApplyEvaluationDeferral();
        try
        {
            _gitWorkspaceEvidence = snapshot.GitEvidence;
            ApplyScanRootOptions(snapshot.ScanRootOptions);
            ApplyExtensionOptions(
                snapshot.ExtensionOptions,
                snapshot.ExtensionlessEntriesCount,
                snapshot.IgnoreOptionCounts,
                snapshot.ControllerImpactCounts,
                snapshot.HasIgnoreOptionCounts);
            ApplyResolvedIgnoreOptions(snapshot.IgnoreOptions, snapshot.IgnoreOptionStateCache);
        }
        finally
        {
            EndPendingApplyEvaluationDeferral();
        }

        RestoreSelectionOptionCache(
            _session.Extensions,
            snapshot.SelectedExtensions,
            snapshot.ExtensionOptionStateCache,
            snapshot.ExtensionSelectionInitialized,
            snapshot.ExtensionOptionStateCacheIsComplete);
        _session.IgnoreOptions.IsInitialized = snapshot.IgnoreOptionsInitialized;
        _session.IgnoreOptions.AllPreference = snapshot.IgnoreAllPreference;
        _session.IgnoreOptionStateCacheIsComplete = snapshot.IgnoreOptionStateCacheIsComplete;
        MarkSelectionRefreshClean();

        _ignoreRulesBuildCache.Invalidate();

        return snapshot;
    }

    private SelectionRefreshRollbackSnapshot RetainUnknownSelectionStates(
        SelectionRefreshRollbackSnapshot snapshot)
    {
        // Visible selections belong to the snapshot, while hidden option preferences
        // must survive until another section makes those options visible again.
        var extensionStates = new Dictionary<string, bool>(
            snapshot.ExtensionOptionStateCache,
            StringComparer.OrdinalIgnoreCase);
        RetainUnknownSelectionStates(
            _session.Extensions.OptionStates,
            extensionStates);

        return snapshot with
        {
            ExtensionOptionStateCache = extensionStates
        };
    }

    private static bool ExtensionPreferencesAreCompatible(
        SelectionRefreshRollbackSnapshot stableSnapshot,
        SelectionRefreshRollbackSnapshot reversibleSnapshot) =>
        stableSnapshot.ExtensionSelectionInitialized == reversibleSnapshot.ExtensionSelectionInitialized &&
        stableSnapshot.ExtensionOptionStateCacheIsComplete == reversibleSnapshot.ExtensionOptionStateCacheIsComplete &&
        DictionaryStatesAreCompatible(
            stableSnapshot.ExtensionOptionStateCache,
            reversibleSnapshot.ExtensionOptionStateCache);

    private static bool IgnorePreferencesAreCompatible(
        SelectionRefreshRollbackSnapshot stableSnapshot,
        SelectionRefreshRollbackSnapshot reversibleSnapshot) =>
        stableSnapshot.IgnoreOptionsInitialized == reversibleSnapshot.IgnoreOptionsInitialized &&
        stableSnapshot.IgnoreAllPreference == reversibleSnapshot.IgnoreAllPreference &&
        stableSnapshot.IgnoreOptionStateCacheIsComplete == reversibleSnapshot.IgnoreOptionStateCacheIsComplete &&
        DictionaryStatesAreCompatible(
            stableSnapshot.IgnoreOptionStateCache,
            reversibleSnapshot.IgnoreOptionStateCache);

    private static void RetainUnknownSelectionStates(
        IReadOnlyDictionary<string, bool> currentStates,
        IDictionary<string, bool> targetStates)
    {
        foreach (var (name, isChecked) in currentStates)
        {
            if (targetStates.ContainsKey(name))
                continue;

            targetStates[name] = isChecked;
        }
    }

    private static IReadOnlyList<SelectionOption> ResolveStableSelectionOptions(
        IReadOnlyList<SelectionOptionViewModel> current,
        IReadOnlyList<SelectionOption>? primaryCandidate,
        IReadOnlyList<SelectionOption>? fallbackCandidate)
    {
        if (primaryCandidate is not null && SelectionOptionStatesMatch(current, primaryCandidate))
            return primaryCandidate;
        if (fallbackCandidate is not null && SelectionOptionStatesMatch(current, fallbackCandidate))
            return fallbackCandidate;

        var captured = new SelectionOption[current.Count];
        for (var index = 0; index < current.Count; index++)
            captured[index] = new SelectionOption(current[index].Name, current[index].IsChecked);

        return captured;
    }

    private IReadOnlyList<SelectionOption> ResolveStableScanRootOptions(
        IReadOnlyList<SelectionOption>? primaryCandidate,
        IReadOnlyList<SelectionOption>? fallbackCandidate)
    {
        if (primaryCandidate is not null && ScanRootScopeMatches(primaryCandidate))
            return primaryCandidate;
        if (fallbackCandidate is not null && ScanRootScopeMatches(fallbackCandidate))
            return fallbackCandidate;

        return SnapshotScanRootOptions();
    }

    private bool ScanRootScopeMatches(IReadOnlyList<SelectionOption> candidate)
    {
        var rootIndex = 0;
        foreach (var option in candidate)
        {
            if (!option.IsChecked)
                continue;
            if (rootIndex >= _scanRoots.Count ||
                !string.Equals(_scanRoots[rootIndex], option.Name, StringComparison.Ordinal))
            {
                return false;
            }

            rootIndex++;
        }

        return rootIndex == _scanRoots.Count;
    }

    private IReadOnlyList<ResolvedIgnoreOptionState> ResolveStableIgnoreOptions(
        IReadOnlyList<ResolvedIgnoreOptionState> candidate)
    {
        if (IgnoreOptionStatesMatch(viewModel.IgnoreOptions, candidate))
            return candidate;

        var captured = new ResolvedIgnoreOptionState[viewModel.IgnoreOptions.Count];
        for (var index = 0; index < viewModel.IgnoreOptions.Count; index++)
        {
            var option = viewModel.IgnoreOptions[index];
            var descriptor = _ignoreOptions.First(current => current.Id == option.Id);
            captured[index] = new ResolvedIgnoreOptionState(
                option.Id,
                option.Label,
                descriptor.DefaultChecked,
                option.IsChecked);
        }

        return captured;
    }

    private void SynchronizeStableIgnoreOptionLabels()
    {
        var stableSnapshot = _stableSelectionSnapshot;
        if (stableSnapshot is null)
            return;

        ResolvedIgnoreOptionState[]? localizedOptions = null;
        for (var index = 0; index < stableSnapshot.IgnoreOptions.Count; index++)
        {
            var stableOption = stableSnapshot.IgnoreOptions[index];
            var currentOption = viewModel.IgnoreOptions.FirstOrDefault(option => option.Id == stableOption.Id);
            if (currentOption is null ||
                string.Equals(currentOption.Label, stableOption.Label, StringComparison.Ordinal))
            {
                continue;
            }

            localizedOptions ??= stableSnapshot.IgnoreOptions.ToArray();
            localizedOptions[index] = stableOption with { Label = currentOption.Label };
        }

        if (localizedOptions is not null)
        {
            _stableSelectionSnapshot = stableSnapshot with { IgnoreOptions = localizedOptions };
            _reversibleSelectionSnapshot = null;
        }
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

    private static bool SelectionOptionIdentitiesMatch(
        IReadOnlyList<SelectionOptionViewModel> current,
        IReadOnlyList<SelectionOption> next)
    {
        if (current.Count != next.Count)
            return false;

        for (var index = 0; index < next.Count; index++)
        {
            if (!string.Equals(current[index].Name, next[index].Name, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool SelectionOptionStatesMatch(
        IReadOnlyList<SelectionOptionViewModel> current,
        IReadOnlyList<SelectionOption> candidate)
    {
        if (!SelectionOptionIdentitiesMatch(current, candidate))
            return false;

        for (var index = 0; index < candidate.Count; index++)
        {
            if (current[index].IsChecked != candidate[index].IsChecked)
                return false;
        }

        return true;
    }

    private static void UpdateSelectionOptionStates(
        IReadOnlyList<SelectionOptionViewModel> current,
        IReadOnlyList<SelectionOption> next)
    {
        for (var index = 0; index < next.Count; index++)
            current[index].IsChecked = next[index].IsChecked;
    }

    private static bool IgnoreOptionIdentitiesMatch(
        IReadOnlyList<IgnoreOptionViewModel> current,
        IReadOnlyList<ResolvedIgnoreOptionState> next)
    {
        if (current.Count != next.Count)
            return false;

        for (var index = 0; index < next.Count; index++)
        {
            if (current[index].Id != next[index].Id)
                return false;
        }

        return true;
    }

    private static bool IgnoreOptionStatesMatch(
        IReadOnlyList<IgnoreOptionViewModel> current,
        IReadOnlyList<ResolvedIgnoreOptionState> candidate)
    {
        if (!IgnoreOptionIdentitiesMatch(current, candidate))
            return false;

        for (var index = 0; index < candidate.Count; index++)
        {
            if (current[index].IsChecked != candidate[index].IsChecked ||
                !string.Equals(current[index].Label, candidate[index].Label, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IgnoreOptionCheckStatesMatch(
        IReadOnlyList<IgnoreOptionViewModel> current,
        IReadOnlyList<ResolvedIgnoreOptionState> candidate)
    {
        if (!IgnoreOptionIdentitiesMatch(current, candidate))
            return false;

        for (var index = 0; index < candidate.Count; index++)
        {
            if (current[index].IsChecked != candidate[index].IsChecked)
                return false;
        }

        return true;
    }

    private static bool DictionaryStatesMatch<TKey>(
        IReadOnlyDictionary<TKey, bool> current,
        IReadOnlyDictionary<TKey, bool> candidate)
        where TKey : notnull
    {
        if (current.Count != candidate.Count)
            return false;

        foreach (var (key, isChecked) in candidate)
        {
            if (!current.TryGetValue(key, out var currentState) || currentState != isChecked)
                return false;
        }

        return true;
    }

    private static bool DictionaryStatesAreCompatible<TKey>(
        IReadOnlyDictionary<TKey, bool> left,
        IReadOnlyDictionary<TKey, bool> right)
        where TKey : notnull
    {
        foreach (var (key, leftState) in left)
        {
            if (right.TryGetValue(key, out var rightState) && leftState != rightState)
                return false;
        }

        return true;
    }

    private static bool SetStatesMatch<T>(IReadOnlySet<T> current, IReadOnlySet<T> candidate)
    {
        return current.Count == candidate.Count && current.SetEquals(candidate);
    }

    private static void RestoreSelectionOptionCache(
        SelectionOptionStateCache cache,
        IReadOnlySet<string> selectedNames,
        IReadOnlyDictionary<string, bool> optionStates,
        bool isInitialized,
        bool hasFullState)
    {
        if (!isInitialized)
        {
            cache.RestoreDefaults(trimExcess: false);
            return;
        }

        cache.RestoreProfile(
            selectedNames,
            hasFullState ? optionStates : null);
    }

    private static void UpdateIgnoreOptionStates(
        IReadOnlyList<IgnoreOptionViewModel> current,
        IReadOnlyList<ResolvedIgnoreOptionState> next)
    {
        for (var index = 0; index < next.Count; index++)
        {
            current[index].Label = next[index].Label;
            current[index].IsChecked = next[index].IsChecked;
        }
    }

    private static bool IgnoreOptionDescriptorsMatch(
        IReadOnlyList<IgnoreOptionDescriptor> current,
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
                currentOption.DefaultChecked != nextOption.DefaultChecked)
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

    private SelectionRefreshContext CreateSelectionRefreshContext(string path, bool captureTreeInventory = false) =>
        SelectionRefreshContext.ForDesktop(
            path: path,
            preparedSelectionMode: _session.PreparedMode,
            allExtensionsChecked: ResolveAllExtensionsCheckedForRefresh(),
            extensionsSelectionInitialized: _session.Extensions.IsInitialized,
            extensionsSelectionCache: _session.Extensions.SnapshotSelectedNames(),
            ignoreSelectionInitialized: _session.IgnoreOptions.IsInitialized,
            ignoreSelectionCache: SnapshotRuntimeSelectedIgnoreOptions(),
            ignoreOptionStateCache: _session.IgnoreOptions.SnapshotStateCache(),
            ignoreAllPreference: _session.IgnoreOptions.AllPreference,
            currentSnapshotState: CaptureIgnoreSectionSnapshotState(),
            extensionOptionStateCache: SnapshotExtensionOptionStateCacheOrNull(isInitialized: true),
            ignoreOptionStateCacheIsComplete: _session.IgnoreOptionStateCacheIsComplete,
            captureTreeInventory: captureTreeInventory,
            currentScanRootOptions: _scanRoots.Count == 0
                ? null
                : SnapshotScanRootOptions(),
            extensionSelectionIsExplicit: _session.ExtensionSelectionIsExplicit);

    private IReadOnlyList<SelectionOption> SnapshotScanRootOptions()
    {
        var options = new SelectionOption[_scanRoots.Count];
        for (var index = 0; index < _scanRoots.Count; index++)
            options[index] = new SelectionOption(_scanRoots[index], true);

        return options;
    }

    private bool ResolveAllExtensionsCheckedForRefresh() =>
        viewModel.AllExtensionsChecked &&
        !_session.Extensions.OptionStates.Values.Contains(false);

    private HashSet<IgnoreOptionId> SnapshotRuntimeSelectedIgnoreOptions()
    {
        var selected = _session.IgnoreOptions.SnapshotSelectedOptions();
        if (selected.Count == 0)
            return selected;

        var visibleIds = new HashSet<IgnoreOptionId>();
        foreach (var option in _ignoreOptions)
            visibleIds.Add(option.Id);

        var preserveTrackedOnly = selected.Contains(IgnoreOptionId.TrackedGitFilesOnly);
        // Ordinary hidden options do not affect runtime rules. Tracked-only is different:
        // it is an explicit fail-closed policy and must survive lost repository evidence.
        selected.IntersectWith(visibleIds);
        if (preserveTrackedOnly)
            selected.Add(IgnoreOptionId.TrackedGitFilesOnly);
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

    private IReadOnlyDictionary<string, bool>? SnapshotExtensionOptionStateCacheOrNull(bool isInitialized)
    {
        if (!isInitialized)
            return null;

        return _session.Extensions.SnapshotOptionStatesOrNull(
            suppressLegacySelectedOnlyState: ShouldSuppressAllTogglesOverride());
    }

    private IgnoreSectionSnapshotState CaptureIgnoreSectionSnapshotState() =>
        new(
            _hasIgnoreOptionCounts,
            _ignoreOptionCounts,
            _ignoreControllerImpactCounts,
            _hasExtensionlessExtensionEntries,
            _extensionlessExtensionEntriesCount,
            _gitWorkspaceEvidence);

    private IgnoreRules GetOrBuildIgnoreRules(
        string path,
        IReadOnlyCollection<IgnoreOptionId> selectedIgnoreOptions,
        IReadOnlyCollection<string>? selectedRootFolders)
    {
        return _ignoreRulesBuildCache.GetOrBuild(path, selectedIgnoreOptions, selectedRootFolders);
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

    private void RequestPendingApplyEvaluation()
    {
        if (_pendingApplyEvaluationDeferral > 0)
        {
            _pendingApplyEvaluationRequested = true;
            return;
        }

        EvaluatePendingApplyChanges();
    }

    private void BeginPendingApplyEvaluationDeferral() =>
        _pendingApplyEvaluationDeferral++;

    private void EndPendingApplyEvaluationDeferral()
    {
        if (_pendingApplyEvaluationDeferral <= 0)
            throw new InvalidOperationException("Pending Apply evaluation deferral is unbalanced.");

        _pendingApplyEvaluationDeferral--;
        if (_pendingApplyEvaluationDeferral != 0 || !_pendingApplyEvaluationRequested)
            return;

        _pendingApplyEvaluationRequested = false;
        EvaluatePendingApplyChanges();
    }

    private void EvaluatePendingApplyChanges()
    {
        var hasPendingChanges = _appliedSelectionState is not null &&
                                !_appliedSelectionState.Matches(currentPathProvider(), viewModel);
        viewModel.SetPendingFilterSettingsChanges(hasPendingChanges);
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
        _ignoreRulesBuildCache.Invalidate();
        _stableSelectionSnapshot = null;
        _reversibleSelectionSnapshot = null;
        ClearAppliedSelectionState();

        // Unsubscribe from collection change events
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

		if (!ProjectPresentationCatalog.ContentTransformationOptionIds.Contains(option.Id) &&
		    _session.IgnoreOptions.AllPreference.HasValue)
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

    private GitFilteringMode ResolvePreferredGitFilteringMode(
        IReadOnlySet<IgnoreOptionId> previousSelections)
    {
        var mode = GitFilteringModeResolver.Resolve(_session.IgnoreOptions.OptionStateCache);
        if (mode != GitFilteringMode.None)
            return mode;

        mode = GitFilteringModeResolver.Resolve(previousSelections);
        if (mode != GitFilteringMode.None)
            return mode;

        return _session.IgnoreOptions.AllPreference == true
            ? GitFilteringMode.RespectGitIgnore
            : GitFilteringMode.None;
    }

    private static void NormalizeGitFilteringOptions(
        IReadOnlyList<IgnoreOptionViewModel> options,
        GitFilteringMode preferredMode)
    {
        IgnoreOptionViewModel? useGitIgnore = null;
        IgnoreOptionViewModel? trackedOnly = null;
        foreach (var option in options)
        {
            if (option.Id == IgnoreOptionId.UseGitIgnore)
                useGitIgnore = option;
            else if (option.Id == IgnoreOptionId.TrackedGitFilesOnly)
                trackedOnly = option;
        }

        if (useGitIgnore is not { IsChecked: true } ||
            trackedOnly is not { IsChecked: true })
        {
            return;
        }

        if (preferredMode == GitFilteringMode.TrackedFilesOnly)
            useGitIgnore.IsChecked = false;
        else
            trackedOnly.IsChecked = false;
    }

    private void ApplyGitFilteringCheckboxTransition(IgnoreOptionViewModel changedOption)
    {
        // The two Git checkboxes represent one optional mode, not a mandatory radio
        // selection. Enabling either mode clears its peer; clearing the active mode must
        // leave both unchecked so users can run Smart Ignore alone or disable all filters.
        _suppressIgnoreItemCheck = true;
        try
        {
            foreach (var option in viewModel.IgnoreOptions)
            {
                if (option.Id != changedOption.Id &&
                    GitFilteringModeResolver.IsGitFilteringOption(option.Id))
                {
                    option.IsChecked = false;
                }
            }
        }
        finally
        {
            _suppressIgnoreItemCheck = false;
        }
    }

    private void SetAllIgnoreOptionsChecked(bool isChecked)
    {
        var gitMode = GitFilteringModeResolver.Resolve(_session.IgnoreOptions.OptionStateCache);
        if (isChecked && gitMode == GitFilteringMode.None)
        {
            gitMode = viewModel.IgnoreOptions.Any(
                static option => option.Id == IgnoreOptionId.UseGitIgnore)
                ? GitFilteringMode.RespectGitIgnore
                : GitFilteringMode.TrackedFilesOnly;
        }

        _suppressIgnoreItemCheck = true;
        try
        {
            foreach (var option in viewModel.IgnoreOptions)
            {
				if (ProjectPresentationCatalog.ContentTransformationOptionIds.Contains(option.Id))
					continue;
                option.IsChecked = option.Id switch
                {
                    IgnoreOptionId.UseGitIgnore =>
                        isChecked && gitMode == GitFilteringMode.RespectGitIgnore,
                    IgnoreOptionId.TrackedGitFilesOnly =>
                        isChecked && gitMode == GitFilteringMode.TrackedFilesOnly,
                    _ => isChecked
                };
            }
        }
        finally
        {
            _suppressIgnoreItemCheck = false;
        }
    }

    private IReadOnlyList<SelectionOption> ApplyMissingProfileSelectionsFallbackToExtensions(
        IReadOnlyList<SelectionOption> options) =>
        SelectionRefreshPolicy.ApplyMissingProfileSelectionsFallbackToExtensions(
            _session.PreparedMode,
            _session.Extensions.SelectedNames,
            options);

    private bool ShouldUseIgnoreDefaultFallback(
        IReadOnlyList<IgnoreOptionDescriptor> options,
        IReadOnlySet<IgnoreOptionId> previousSelections) =>
        SelectionRefreshPolicy.ShouldUseIgnoreDefaultFallback(
            _session.PreparedMode,
            options,
            previousSelections);

}
