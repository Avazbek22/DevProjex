using DevProjex.Application.Models;
using DevProjex.Application.Context;

namespace DevProjex.Avalonia.Coordinators;

public sealed partial class SelectionSyncCoordinator
{
    internal sealed class ProjectCheckpoint
    {
        internal ProjectCheckpoint(
            ProjectSelectionSessionSnapshot session,
            IReadOnlyList<string> scanRoots,
            IReadOnlyList<SelectionOption> extensionOptions,
            IReadOnlyList<IgnoreOptionSnapshot> ignoreOptions,
            IReadOnlyList<IgnoreOptionDescriptor> ignoreDescriptors,
			string? ignoreOptionsProjectPath,
            bool hasExtensionlessExtensionEntries,
            int extensionlessExtensionEntriesCount,
            bool hasIgnoreOptionCounts,
            IgnoreOptionCounts ignoreOptionCounts,
            IgnoreControllerImpactCounts controllerImpactCounts,
            GitWorkspaceEvidence gitEvidence,
			bool gitRepositoryBoundaryKnownAbsent,
			bool preservePreferredGitModeForPersistence,
            SelectionRefreshRollbackSnapshot? stableSelectionSnapshot,
            SelectionRefreshRollbackSnapshot? reversibleSelectionSnapshot,
            AppliedSelectionState? appliedSelectionState,
            ProjectContextGitReadiness appliedGitReadiness,
            bool selectionPersistenceBlockedByIncompleteScan,
            bool selectionRefreshDirty)
        {
            Session = session;
            ScanRoots = scanRoots;
            ExtensionOptions = extensionOptions;
            IgnoreOptions = ignoreOptions;
            IgnoreDescriptors = ignoreDescriptors;
			IgnoreOptionsProjectPath = ignoreOptionsProjectPath;
            HasExtensionlessExtensionEntries = hasExtensionlessExtensionEntries;
            ExtensionlessExtensionEntriesCount = extensionlessExtensionEntriesCount;
            HasIgnoreOptionCounts = hasIgnoreOptionCounts;
            IgnoreOptionCounts = ignoreOptionCounts;
            ControllerImpactCounts = controllerImpactCounts;
            GitEvidence = gitEvidence;
			GitRepositoryBoundaryKnownAbsent = gitRepositoryBoundaryKnownAbsent;
			PreservePreferredGitModeForPersistence = preservePreferredGitModeForPersistence;
            StableSelectionSnapshot = stableSelectionSnapshot;
            ReversibleSelectionSnapshot = reversibleSelectionSnapshot;
            AppliedSelectionState = appliedSelectionState;
            AppliedGitReadiness = appliedGitReadiness;
            SelectionPersistenceBlockedByIncompleteScan = selectionPersistenceBlockedByIncompleteScan;
            SelectionRefreshDirty = selectionRefreshDirty;
        }

        internal ProjectSelectionSessionSnapshot Session { get; }
        internal IReadOnlyList<string> ScanRoots { get; }
        internal IReadOnlyList<SelectionOption> ExtensionOptions { get; }
        internal IReadOnlyList<IgnoreOptionSnapshot> IgnoreOptions { get; }
        internal IReadOnlyList<IgnoreOptionDescriptor> IgnoreDescriptors { get; }
		internal string? IgnoreOptionsProjectPath { get; }
        internal bool HasExtensionlessExtensionEntries { get; }
        internal int ExtensionlessExtensionEntriesCount { get; }
        internal bool HasIgnoreOptionCounts { get; }
        internal IgnoreOptionCounts IgnoreOptionCounts { get; }
        internal IgnoreControllerImpactCounts ControllerImpactCounts { get; }
        internal GitWorkspaceEvidence GitEvidence { get; }
		internal bool GitRepositoryBoundaryKnownAbsent { get; }
		internal bool PreservePreferredGitModeForPersistence { get; }
        internal SelectionRefreshRollbackSnapshot? StableSelectionSnapshot { get; }
        internal SelectionRefreshRollbackSnapshot? ReversibleSelectionSnapshot { get; }
        internal AppliedSelectionState? AppliedSelectionState { get; }
        internal ProjectContextGitReadiness AppliedGitReadiness { get; }
        internal bool SelectionPersistenceBlockedByIncompleteScan { get; }
        internal bool SelectionRefreshDirty { get; }
    }

    internal ProjectCheckpoint CaptureProjectCheckpoint()
    {
        var extensions = new SelectionOption[viewModel.Extensions.Count];
        for (var index = 0; index < extensions.Length; index++)
        {
            var option = viewModel.Extensions[index];
            extensions[index] = new SelectionOption(option.Name, option.IsChecked);
        }

        var ignoreOptions = new IgnoreOptionSnapshot[viewModel.IgnoreOptions.Count];
        for (var index = 0; index < ignoreOptions.Length; index++)
        {
            var option = viewModel.IgnoreOptions[index];
            ignoreOptions[index] = new IgnoreOptionSnapshot(option.Id, option.Label, option.IsChecked);
        }

        return new ProjectCheckpoint(
            _session.CaptureSnapshot(),
            _scanRoots.ToArray(),
            extensions,
            ignoreOptions,
            _ignoreOptions.ToArray(),
			_ignoreOptionsProjectPath,
            _hasExtensionlessExtensionEntries,
            _extensionlessExtensionEntriesCount,
            _hasIgnoreOptionCounts,
            _ignoreOptionCounts,
            _ignoreControllerImpactCounts,
            _gitWorkspaceEvidence,
			_gitRepositoryBoundaryKnownAbsent,
			_preservePreferredGitModeForPersistence,
            _stableSelectionSnapshot,
            _reversibleSelectionSnapshot,
            _appliedSelectionState,
            _appliedGitReadiness,
            _selectionPersistenceBlockedByIncompleteScan,
            HasDirtySelectionRefresh());
    }

    internal void RestoreProjectCheckpoint(ProjectCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        InvalidatePendingRefreshesForProjectCheckpointRestore();

        var extensionOptions = new SelectionOptionViewModel[checkpoint.ExtensionOptions.Count];
        for (var index = 0; index < extensionOptions.Length; index++)
        {
            var option = checkpoint.ExtensionOptions[index];
            extensionOptions[index] = new SelectionOptionViewModel(option.Name, option.IsChecked);
        }

        var controllerGroupEndIndex = FindLastControllerOptionIndex(
            checkpoint.IgnoreOptions,
            static option => option.Id);
        var ignoreOptions = new IgnoreOptionViewModel[checkpoint.IgnoreOptions.Count];
        for (var index = 0; index < ignoreOptions.Length; index++)
        {
            var option = checkpoint.IgnoreOptions[index];
            ignoreOptions[index] = new IgnoreOptionViewModel(
                option.Id,
                option.Label,
                option.IsChecked,
                isControllerGroupEnd: index == controllerGroupEndIndex);
        }

        _suppressExtensionAllCheck = true;
        _suppressExtensionItemCheck = true;
        _suppressIgnoreAllCheck = true;
        _suppressIgnoreItemCheck = true;
        try
        {
            _scanRoots.Clear();
            _scanRoots.AddRange(checkpoint.ScanRoots);
            ReplaceCollectionItems(viewModel.Extensions, extensionOptions);
            ReplaceCollectionItems(viewModel.IgnoreOptions, ignoreOptions);

            _session.RestoreSnapshot(checkpoint.Session);
            _ignoreOptions = checkpoint.IgnoreDescriptors;
			_ignoreOptionsProjectPath = checkpoint.IgnoreOptionsProjectPath;
            _hasExtensionlessExtensionEntries = checkpoint.HasExtensionlessExtensionEntries;
            _extensionlessExtensionEntriesCount = checkpoint.ExtensionlessExtensionEntriesCount;
            _hasIgnoreOptionCounts = checkpoint.HasIgnoreOptionCounts;
            _ignoreOptionCounts = checkpoint.IgnoreOptionCounts;
            _ignoreControllerImpactCounts = checkpoint.ControllerImpactCounts;
            _gitWorkspaceEvidence = checkpoint.GitEvidence;
			_gitRepositoryBoundaryKnownAbsent = checkpoint.GitRepositoryBoundaryKnownAbsent;
			_preservePreferredGitModeForPersistence = checkpoint.PreservePreferredGitModeForPersistence;
            _stableSelectionSnapshot = checkpoint.StableSelectionSnapshot;
            _reversibleSelectionSnapshot = checkpoint.ReversibleSelectionSnapshot;
            _appliedSelectionState = checkpoint.AppliedSelectionState;
            _appliedGitReadiness = checkpoint.AppliedGitReadiness;
            _selectionPersistenceBlockedByIncompleteScan =
                checkpoint.SelectionPersistenceBlockedByIncompleteScan;
            Volatile.Write(ref _selectionRefreshDirty, checkpoint.SelectionRefreshDirty ? 1 : 0);

            // Revision is a monotonic invalidation boundary. Restoring the old value would
            // allow a tree built for the canceled project to look current after rollback.
            _session.AdvanceRevision();
        }
        finally
        {
            _suppressExtensionAllCheck = false;
            _suppressExtensionItemCheck = false;
            _suppressIgnoreAllCheck = false;
            _suppressIgnoreItemCheck = false;
        }

		RefreshGitFilteringModePresentation();
		SynchronizeDerivedAggregateSelectionState();

        _pendingApplyEvaluationRequested = false;
        _selectionRefreshEngine.InvalidateCaches();
        _ignoreRulesBuildCache.Invalidate();
        RequestPendingApplyEvaluation();
		if (checkpoint.SelectionRefreshDirty)
		{
			// A dirty checkpoint represents a user selection whose dependent rows/counts were
			// not stable yet. The old task was detached above; one full refresh is the smallest
			// safe way to make the restored checkbox state and every dependent section converge.
			QueueFullRefresh(currentPathProvider(), changedIgnoreOptionId: null);
		}
    }

    private void InvalidatePendingRefreshesForProjectCheckpointRestore()
    {
		// A canceled filesystem operation is allowed to finish cooperatively on its old gate.
		// New-project work receives a fresh serialization boundary immediately, so a provider
		// that is temporarily slow to observe cancellation cannot freeze the restored session.
		Interlocked.Exchange(ref _refreshLock, new SemaphoreSlim(1, 1));

        CancellationTokenSource? liveRefreshCts;
        CancellationTokenSource? fullRefreshCts;
        Task liveRefreshTask;
        Task fullRefreshTask;
        lock (_backgroundRefreshSync)
        {
            liveRefreshCts = _liveOptionsRefreshCts;
            fullRefreshCts = _fullRefreshRequestCts;
            liveRefreshTask = _latestLiveOptionsRefreshTask;
            fullRefreshTask = _latestFullRefreshTask;

            liveRefreshCts?.Cancel();
            fullRefreshCts?.Cancel();
            _liveOptionsRefreshCts = null;
            _fullRefreshRequestCts = null;
            _latestLiveOptionsRefreshTask = Task.CompletedTask;
            _latestFullRefreshTask = Task.CompletedTask;
            _liveOptionsRequestVersion = unchecked(_liveOptionsRequestVersion + 1);
            _fullRefreshRequestVersion = unchecked(_fullRefreshRequestVersion + 1);
        }

        // Detached stale tasks remain observed by FireAndForgetSafe, but they are no longer
        // part of the restored project's idleness boundary or cancellation-source lifetime.
        DisposeCancellationSourceWhenTaskCompletes(liveRefreshCts, liveRefreshTask);
        DisposeCancellationSourceWhenTaskCompletes(fullRefreshCts, fullRefreshTask);

        Interlocked.Increment(ref _extensionScanVersion);
        Interlocked.Increment(ref _ignoreOptionsVersion);
    }
}
