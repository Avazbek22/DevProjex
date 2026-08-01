using DevProjex.Application.Models;
using DevProjex.Application.Context;

namespace DevProjex.Avalonia.Coordinators;

public sealed partial class SelectionSyncCoordinator
{
    internal sealed class ProjectCheckpoint
    {
        internal ProjectCheckpoint(
            ProjectSelectionSessionSnapshot session,
            IReadOnlyList<SelectionOption> rootOptions,
            IReadOnlyList<SelectionOption> extensionOptions,
            IReadOnlyList<IgnoreOptionSnapshot> ignoreOptions,
            IReadOnlyList<IgnoreOptionDescriptor> ignoreDescriptors,
            bool allRootFoldersChecked,
            bool allExtensionsChecked,
            bool allIgnoreChecked,
            bool hasExtensionlessExtensionEntries,
            int extensionlessExtensionEntriesCount,
            bool hasIgnoreOptionCounts,
            IgnoreOptionCounts ignoreOptionCounts,
            IgnoreControllerImpactCounts controllerImpactCounts,
            GitWorkspaceEvidence gitEvidence,
            SelectionRefreshRollbackSnapshot? stableSelectionSnapshot,
            SelectionRefreshRollbackSnapshot? reversibleSelectionSnapshot,
            AppliedSelectionState? appliedSelectionState,
            ProjectContextGitReadiness appliedGitReadiness,
            bool selectionRefreshDirty)
        {
            Session = session;
            RootOptions = rootOptions;
            ExtensionOptions = extensionOptions;
            IgnoreOptions = ignoreOptions;
            IgnoreDescriptors = ignoreDescriptors;
            AllRootFoldersChecked = allRootFoldersChecked;
            AllExtensionsChecked = allExtensionsChecked;
            AllIgnoreChecked = allIgnoreChecked;
            HasExtensionlessExtensionEntries = hasExtensionlessExtensionEntries;
            ExtensionlessExtensionEntriesCount = extensionlessExtensionEntriesCount;
            HasIgnoreOptionCounts = hasIgnoreOptionCounts;
            IgnoreOptionCounts = ignoreOptionCounts;
            ControllerImpactCounts = controllerImpactCounts;
            GitEvidence = gitEvidence;
            StableSelectionSnapshot = stableSelectionSnapshot;
            ReversibleSelectionSnapshot = reversibleSelectionSnapshot;
            AppliedSelectionState = appliedSelectionState;
            AppliedGitReadiness = appliedGitReadiness;
            SelectionRefreshDirty = selectionRefreshDirty;
        }

        internal ProjectSelectionSessionSnapshot Session { get; }
        internal IReadOnlyList<SelectionOption> RootOptions { get; }
        internal IReadOnlyList<SelectionOption> ExtensionOptions { get; }
        internal IReadOnlyList<IgnoreOptionSnapshot> IgnoreOptions { get; }
        internal IReadOnlyList<IgnoreOptionDescriptor> IgnoreDescriptors { get; }
        internal bool AllRootFoldersChecked { get; }
        internal bool AllExtensionsChecked { get; }
        internal bool AllIgnoreChecked { get; }
        internal bool HasExtensionlessExtensionEntries { get; }
        internal int ExtensionlessExtensionEntriesCount { get; }
        internal bool HasIgnoreOptionCounts { get; }
        internal IgnoreOptionCounts IgnoreOptionCounts { get; }
        internal IgnoreControllerImpactCounts ControllerImpactCounts { get; }
        internal GitWorkspaceEvidence GitEvidence { get; }
        internal SelectionRefreshRollbackSnapshot? StableSelectionSnapshot { get; }
        internal SelectionRefreshRollbackSnapshot? ReversibleSelectionSnapshot { get; }
        internal AppliedSelectionState? AppliedSelectionState { get; }
        internal ProjectContextGitReadiness AppliedGitReadiness { get; }
        internal bool SelectionRefreshDirty { get; }
    }

    internal ProjectCheckpoint CaptureProjectCheckpoint()
    {
        var roots = new SelectionOption[viewModel.RootFolders.Count];
        for (var index = 0; index < roots.Length; index++)
        {
            var option = viewModel.RootFolders[index];
            roots[index] = new SelectionOption(option.Name, option.IsChecked);
        }

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
            roots,
            extensions,
            ignoreOptions,
            _ignoreOptions.ToArray(),
            viewModel.AllRootFoldersChecked,
            viewModel.AllExtensionsChecked,
            viewModel.AllIgnoreChecked,
            _hasExtensionlessExtensionEntries,
            _extensionlessExtensionEntriesCount,
            _hasIgnoreOptionCounts,
            _ignoreOptionCounts,
            _ignoreControllerImpactCounts,
            _gitWorkspaceEvidence,
            _stableSelectionSnapshot,
            _reversibleSelectionSnapshot,
            _appliedSelectionState,
            _appliedGitReadiness,
            HasDirtySelectionRefresh());
    }

    internal void RestoreProjectCheckpoint(ProjectCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        InvalidatePendingRefreshesForProjectCheckpointRestore();

        var rootOptions = new SelectionOptionViewModel[checkpoint.RootOptions.Count];
        for (var index = 0; index < rootOptions.Length; index++)
        {
            var option = checkpoint.RootOptions[index];
            rootOptions[index] = new SelectionOptionViewModel(option.Name, option.IsChecked);
        }

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

        _suppressRootAllCheck = true;
        _suppressRootItemCheck = true;
        _suppressExtensionAllCheck = true;
        _suppressExtensionItemCheck = true;
        _suppressIgnoreAllCheck = true;
        _suppressIgnoreItemCheck = true;
        try
        {
            ReplaceCollectionItems(viewModel.RootFolders, rootOptions);
            ReplaceCollectionItems(viewModel.Extensions, extensionOptions);
            ReplaceCollectionItems(viewModel.IgnoreOptions, ignoreOptions);

            viewModel.AllRootFoldersChecked = checkpoint.AllRootFoldersChecked;
            viewModel.AllExtensionsChecked = checkpoint.AllExtensionsChecked;
            viewModel.AllIgnoreChecked = checkpoint.AllIgnoreChecked;

            _session.RestoreSnapshot(checkpoint.Session);
            _ignoreOptions = checkpoint.IgnoreDescriptors;
            _hasExtensionlessExtensionEntries = checkpoint.HasExtensionlessExtensionEntries;
            _extensionlessExtensionEntriesCount = checkpoint.ExtensionlessExtensionEntriesCount;
            _hasIgnoreOptionCounts = checkpoint.HasIgnoreOptionCounts;
            _ignoreOptionCounts = checkpoint.IgnoreOptionCounts;
            _ignoreControllerImpactCounts = checkpoint.ControllerImpactCounts;
            _gitWorkspaceEvidence = checkpoint.GitEvidence;
            _stableSelectionSnapshot = checkpoint.StableSelectionSnapshot;
            _reversibleSelectionSnapshot = checkpoint.ReversibleSelectionSnapshot;
            _appliedSelectionState = checkpoint.AppliedSelectionState;
            _appliedGitReadiness = checkpoint.AppliedGitReadiness;
            Volatile.Write(ref _selectionRefreshDirty, checkpoint.SelectionRefreshDirty ? 1 : 0);

            // Revision is a monotonic invalidation boundary. Restoring the old value would
            // allow a tree built for the canceled project to look current after rollback.
            _session.AdvanceRevision();
        }
        finally
        {
            _suppressRootAllCheck = false;
            _suppressRootItemCheck = false;
            _suppressExtensionAllCheck = false;
            _suppressExtensionItemCheck = false;
            _suppressIgnoreAllCheck = false;
            _suppressIgnoreItemCheck = false;
        }

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

        Interlocked.Increment(ref _rootScanVersion);
        Interlocked.Increment(ref _extensionScanVersion);
        Interlocked.Increment(ref _ignoreOptionsVersion);
    }
}
