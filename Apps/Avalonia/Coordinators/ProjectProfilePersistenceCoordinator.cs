using DevProjex.Application.Models;
using DevProjex.Infrastructure.ProjectProfiles;

namespace DevProjex.Avalonia.Coordinators;

public readonly record struct ProjectProfileFlushResult(
    bool GateAcquired,
    int Attempted,
    int Saved,
    int Remaining)
{
    public bool Succeeded => GateAcquired && Remaining == 0;
}

public sealed class ProjectProfilePersistenceCoordinator(
    MainWindowViewModel viewModel,
    SelectionSyncCoordinator selectionCoordinator,
    IProjectProfileStore profileStore,
    SecretRedactionSession secretRedactionSession,
    Func<string?>? activeProjectPathProvider = null,
    Func<IReadOnlyCollection<string>?>? selectedPathsProvider = null,
    Func<TimeSpan, CancellationToken, Task>? profileLoadDelay = null,
    IReadOnlyList<TimeSpan>? profileLoadRetryDelays = null)
{
    private static readonly TimeSpan GuiLookupTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan[] DefaultProfileLoadRetryDelays =
    [
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(100)),
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(200)),
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(400))
    ];
    private readonly PendingProjectProfileWriteQueue _pendingWrites = new(profileStore);
    private readonly Func<TimeSpan, CancellationToken, Task> _profileLoadDelay =
        profileLoadDelay ?? Task.Delay;
    private readonly IReadOnlyList<TimeSpan> _profileLoadRetryDelays =
        profileLoadRetryDelays ?? DefaultProfileLoadRetryDelays;
    private readonly PersistentSecretMarkDeltaWriter? _markWriter =
        profileStore is IPersistentSecretMarkStore markStore
            ? new PersistentSecretMarkDeltaWriter(markStore)
            : null;
    private readonly object _loadStateSync = new();
    private readonly Dictionary<string, ProfileLoadState> _loadStates =
        new(PathComparer.Default);
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);
    private long _nextLoadRevision;
    private int _persistenceOperationCount;

    public bool EnsureStorageExists() => profileStore.EnsureStorageExists();

    public ProjectProfileClearStatus ClearAllProfiles()
    {
        if (!_persistenceGate.Wait(GuiLookupTimeout))
            return ProjectProfileClearStatus.Busy;

        try
        {
            var result = _pendingWrites.ClearAllProfiles();
            if (result != ProjectProfileClearStatus.Cleared)
                return result;

            lock (_loadStateSync)
                _loadStates.Clear();
            return result;
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    public Task PersistIfNeededAsync(
        string? currentPath,
        CancellationToken cancellationToken = default)
    {
        if (!IsApplicable(currentPath) ||
            !selectionCoordinator.IsSelectionStateCompleteForPersistence)
        {
            return Task.CompletedTask;
        }

        return RunSerializedPersistenceAsync(
            () => PersistIfNeededCoreAsync(currentPath!, cancellationToken),
            cancellationToken);
    }

    private async Task PersistIfNeededCoreAsync(
        string currentPath,
        CancellationToken cancellationToken)
    {
        var normalizedPath = Path.GetFullPath(currentPath);
        var profile = CaptureCurrentProfile(currentPath);
        var updatedUtc = DateTimeOffset.UtcNow;
        var readiness = await PreparePersistenceAsync(currentPath, cancellationToken).ConfigureAwait(false);
        if (!readiness.CanPersist)
        {
            await EnqueueFullProfileWriteAsync(
                    normalizedPath,
                    profile,
                    readiness.RecoveredSnapshot?.Profile ?? GetSuccessfulSnapshot(normalizedPath)?.Profile,
                    updatedUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (profileStore is ProjectProfileStore)
        {
            var pendingRevision = await _pendingWrites
                .GetPendingRevisionAsync(normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            var merged = await PersistMergedAsync(
                normalizedPath,
                profile,
                readiness.RecoveredSnapshot?.Profile,
                ProjectProfileMergeFields.AllSelections,
                cancellationToken).ConfigureAwait(false);
            if (!merged)
            {
                await _pendingWrites
                    .EnqueueMergeAsync(
                        normalizedPath,
                        profile,
                        readiness.RecoveredSnapshot?.Profile,
                        ProjectProfileMergeFields.AllSelections,
                        updatedUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _pendingWrites
                    .RemovePendingAsync(normalizedPath, pendingRevision, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            return;
        }
        await _pendingWrites
            .PersistAsync(
                normalizedPath,
                profile,
                updatedUtc,
                CanPersistNormalizedPath,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<ProjectProfilePersistenceResult> PersistSelectedPathsAsync(
        string? currentPath,
        IReadOnlyCollection<string>? selectedPaths,
        CancellationToken cancellationToken = default) =>
        RunSerializedPersistenceAsync(
            () => PersistSelectedPathsCoreAsync(currentPath, selectedPaths, cancellationToken),
            cancellationToken);

    private async Task<ProjectProfilePersistenceResult> PersistSelectedPathsCoreAsync(
        string? currentPath,
        IReadOnlyCollection<string>? selectedPaths,
        CancellationToken cancellationToken)
    {
        var readiness = await PreparePersistenceAsync(currentPath, cancellationToken).ConfigureAwait(false);
        if (!readiness.CanPersist)
        {
            return ProjectProfilePersistenceResult.Deferred(
                readiness.BlockedStatus?.ToString() ?? "The project profile is not ready for persistence.");
        }

        var profile = readiness.RecoveredSnapshot?.Profile is { } recoveredProfile
            ? ProjectSelectionProfileBuilder.Clone(recoveredProfile) with
            {
                SelectedPaths = selectedPaths?.ToArray()
            }
            : CaptureProfileForSelectionWrite(currentPath!, selectedPaths);
        if (profile is null)
        {
            return ProjectProfilePersistenceResult.Deferred(
                "The project selection state is incomplete.");
        }
        if (profileStore is ProjectProfileStore)
        {
            var pendingRevision = await _pendingWrites
                .GetPendingRevisionAsync(currentPath!, cancellationToken)
                .ConfigureAwait(false);
            var merged = await PersistMergedAsync(
                currentPath!,
                profile,
                readiness.RecoveredSnapshot?.Profile,
                ProjectProfileMergeFields.SelectedPaths,
                cancellationToken).ConfigureAwait(false);
            if (!merged)
            {
                return ProjectProfilePersistenceResult.Failed(
                    "The project selection could not be saved without overwriting a newer revision.");
            }
            await _pendingWrites
                .CoalesceSelectedPathsAsync(
                    currentPath!,
                    pendingRevision,
                    selectedPaths,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return ProjectProfilePersistenceResult.Saved();
        }

        try
        {
            await _pendingWrites
                .PersistAsync(
                    currentPath!,
                    profile,
                    DateTimeOffset.UtcNow,
                    CanPersistNormalizedPath,
                    cancellationToken)
                .ConfigureAwait(false);
            return ProjectProfilePersistenceResult.Saved();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ProjectProfilePersistenceResult.Failed(exception.Message);
        }
    }

    public async Task<PersistentSecretMarkWriteResult> ApplyMarkDeltaAsync(
        string? currentPath,
        PersistentSecretMarkDelta delta,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delta);
        var readiness = await PreparePersistenceAsync(currentPath, cancellationToken).ConfigureAwait(false);
        if (!readiness.CanPersist || _markWriter is null)
        {
            return new PersistentSecretMarkWriteResult(
                PersistentSecretMarkStoreStatus.InvalidProjectPath,
                null);
        }

        var expectedProjectPath = Path.GetFullPath(currentPath!);
        var result = await _markWriter
            .ApplyAsync(expectedProjectPath, delta, cancellationToken)
            .ConfigureAwait(false);
        if (result is { Succeeded: true, Snapshot: not null } &&
            IsStillActiveProject(expectedProjectPath))
        {
            try
            {
                secretRedactionSession.AcknowledgePersistentMarkDelta(
                    expectedProjectPath,
                    delta.OperationId,
                    result.Snapshot);
            }
            catch (ObjectDisposedException)
            {
                // The durable write remains successful when the owning window closes meanwhile.
            }
        }
        return result;
    }

    private bool IsStillActiveProject(string expectedProjectPath)
    {
        if (activeProjectPathProvider is null)
            return true;
        var activePath = activeProjectPathProvider();
        if (string.IsNullOrWhiteSpace(activePath))
            return false;
        try
        {
            return PathComparer.Default.Equals(
                expectedProjectPath,
                Path.GetFullPath(activePath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public async Task<ProjectProfileLoadSnapshot> LoadSnapshotAsync(
        string? currentPath,
        CancellationToken cancellationToken)
    {
        if (!IsApplicable(currentPath))
            return new ProjectProfileLoadSnapshot(
                ProjectProfileLookupStatus.InvalidProjectPath,
                null,
                null);

        var normalizedPath = Path.GetFullPath(currentPath!);
        var attempt = BeginLoad(normalizedPath);
        try
        {
            var result = await Task.Run(
                () => profileStore.LookupProfile(normalizedPath, GuiLookupTimeout),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Status is not (ProjectProfileLookupStatus.Found or ProjectProfileLookupStatus.Missing))
            {
                CompleteLoad(
                    normalizedPath,
                    attempt.Revision,
                    result.Status,
                    successfulSnapshot: null,
                    retryPersistenceLoad: result.Status == ProjectProfileLookupStatus.TemporarilyUnavailable);
                return new ProjectProfileLoadSnapshot(result.Status, null, null);
            }
            if (result.RecoveryStatus is not null && attempt.Previous.SuccessfulSnapshot is { } previousSnapshot)
            {
                CompleteLoad(
                    normalizedPath,
                    attempt.Revision,
                    previousSnapshot.Status,
                    previousSnapshot);
                return previousSnapshot;
            }

            var marksResult = await LoadPersistentMarksAsync(
                normalizedPath,
                result.Profile,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!marksResult.Succeeded || marksResult.Snapshot is null)
            {
                var unavailableStatus = MapMarkStoreStatus(marksResult.Status);
                CompleteLoad(normalizedPath, attempt.Revision, unavailableStatus, successfulSnapshot: null);
                return new ProjectProfileLoadSnapshot(unavailableStatus, null, null);
            }
            var identityAvailability = await secretRedactionSession
                .EnsurePersistentIdentityReadyAsync(marksResult.Snapshot.Marks, cancellationToken)
                .ConfigureAwait(false);
            if (identityAvailability != PersistentSecretIdentityAvailability.Ready)
            {
                var unavailableStatus = identityAvailability ==
                                        PersistentSecretIdentityAvailability.TemporarilyUnavailable
                    ? ProjectProfileLookupStatus.TemporarilyUnavailable
                    : ProjectProfileLookupStatus.InvalidStorage;
                CompleteLoad(normalizedPath, attempt.Revision, unavailableStatus, successfulSnapshot: null);
                return new ProjectProfileLoadSnapshot(unavailableStatus, null, null);
            }

            var snapshot = new ProjectProfileLoadSnapshot(result.Status, result.Profile, marksResult.Snapshot);
            CompleteLoad(normalizedPath, attempt.Revision, result.Status, snapshot);
            return snapshot;
        }
        catch
        {
            RestoreLoadState(normalizedPath, attempt);
            throw;
        }
    }

    public async Task<ProjectProfileLoadSnapshot> LoadSnapshotWithRetryAsync(
        string? currentPath,
        CancellationToken cancellationToken)
    {
        ProjectProfileLoadSnapshot snapshot = default;
        for (var attempt = 0; attempt <= _profileLoadRetryDelays.Count; attempt++)
        {
            snapshot = await LoadSnapshotAsync(currentPath, cancellationToken).ConfigureAwait(false);
            if (snapshot.Status != ProjectProfileLookupStatus.TemporarilyUnavailable ||
                attempt == _profileLoadRetryDelays.Count)
            {
                return snapshot;
            }

            await _profileLoadDelay(_profileLoadRetryDelays[attempt], cancellationToken)
                .ConfigureAwait(false);
        }

        return snapshot;
    }

    private async ValueTask<PersistentSecretMarksLoadResult> LoadPersistentMarksAsync(
        string normalizedPath,
        ProjectSelectionProfile? profile,
        CancellationToken cancellationToken)
    {
        if (profileStore is IPersistentSecretMarkStore markStore)
        {
            return await markStore
                .LoadMarksAsync(normalizedPath, cancellationToken)
                .ConfigureAwait(false);
        }

        return new PersistentSecretMarksLoadResult(
            PersistentSecretMarkStoreStatus.Success,
            new PersistentSecretMarksSnapshot(0, profile?.MarkedSecrets ?? []));
    }

    private static ProjectProfileLookupStatus MapMarkStoreStatus(
        PersistentSecretMarkStoreStatus status) =>
        status switch
        {
            PersistentSecretMarkStoreStatus.TemporarilyUnavailable =>
                ProjectProfileLookupStatus.TemporarilyUnavailable,
            PersistentSecretMarkStoreStatus.InvalidProjectPath =>
                ProjectProfileLookupStatus.InvalidProjectPath,
            PersistentSecretMarkStoreStatus.UnsupportedFutureSchema =>
                ProjectProfileLookupStatus.UnsupportedFutureSchema,
            _ => ProjectProfileLookupStatus.InvalidStorage
        };

    private static bool ShouldRetryBlockedPersistence(ProjectProfileLookupStatus? status) =>
        status is ProjectProfileLookupStatus.TemporarilyUnavailable or
            ProjectProfileLookupStatus.InvalidStorage;

    public ProjectProfileFlushResult FlushPending(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        var startedTimestamp = Stopwatch.GetTimestamp();
        if (!_persistenceGate.Wait(timeout))
            return new ProjectProfileFlushResult(false, 0, 0, -1);

        try
        {
            var remaining = timeout - Stopwatch.GetElapsedTime(startedTimestamp);
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;
            return _pendingWrites.Flush(remaining, CanPersistNormalizedPath);
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    public async Task<ProjectProfileFlushResult> FlushPendingAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        var startedTimestamp = Stopwatch.GetTimestamp();
        if (!await _persistenceGate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
            return new ProjectProfileFlushResult(false, 0, 0, -1);

        try
        {
            var remaining = timeout - Stopwatch.GetElapsedTime(startedTimestamp);
            if (remaining <= TimeSpan.Zero)
                return new ProjectProfileFlushResult(true, 0, 0, _pendingWrites.Count);

            using (var refreshTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                refreshTimeout.CancelAfter(remaining);
                try
                {
                    await RefreshPendingPersistenceReadinessAsync(refreshTimeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    refreshTimeout.IsCancellationRequested &&
                    !cancellationToken.IsCancellationRequested)
                {
                    return new ProjectProfileFlushResult(true, 0, 0, _pendingWrites.Count);
                }
            }

            remaining = timeout - Stopwatch.GetElapsedTime(startedTimestamp);
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;
            return await Task.Run(
                    () => _pendingWrites.Flush(remaining, CanPersistNormalizedPath),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private async Task RefreshPendingPersistenceReadinessAsync(CancellationToken cancellationToken)
    {
        var projectPaths = await _pendingWrites
            .GetPendingProjectPathsAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var projectPath in projectPaths)
        {
            if (!ShouldRetryBlockedPersistence(GetLoadStatus(projectPath)))
                continue;

            _ = await LoadSnapshotWithRetryAsync(projectPath, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public bool HasPendingWrites =>
        Volatile.Read(ref _persistenceOperationCount) > 0 || _pendingWrites.HasPending;

    public async Task<bool> DiscardPendingWritesAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        var startedTimestamp = Stopwatch.GetTimestamp();
        if (!await _persistenceGate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
            return false;

        try
        {
            var remaining = timeout - Stopwatch.GetElapsedTime(startedTimestamp);
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;
            return await _pendingWrites
                .DiscardPendingAsync(remaining, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private Task EnqueueFullProfileWriteAsync(
        string normalizedPath,
        ProjectSelectionProfile profile,
        ProjectSelectionProfile? baseline,
        DateTimeOffset updatedUtc,
        CancellationToken cancellationToken) =>
        profileStore is ProjectProfileStore
            ? _pendingWrites.EnqueueMergeAsync(
                normalizedPath,
                profile,
                baseline,
                ProjectProfileMergeFields.AllSelections,
                updatedUtc,
                cancellationToken)
            : _pendingWrites.EnqueueAsync(
                normalizedPath,
                profile,
                updatedUtc,
                cancellationToken);

    private async Task RunSerializedPersistenceAsync(
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _persistenceOperationCount);
        try
        {
            await _persistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await operation().ConfigureAwait(false);
            }
            finally
            {
                _persistenceGate.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _persistenceOperationCount);
        }
    }

    private async Task<TResult> RunSerializedPersistenceAsync<TResult>(
        Func<Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _persistenceOperationCount);
        try
        {
            await _persistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await operation().ConfigureAwait(false);
            }
            finally
            {
                _persistenceGate.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _persistenceOperationCount);
        }
    }

    private bool IsApplicable(string? currentPath)
    {
        return !string.IsNullOrWhiteSpace(currentPath);
    }

    private async Task<ProfilePersistenceReadiness> PreparePersistenceAsync(
        string? currentPath,
        CancellationToken cancellationToken)
    {
        if (!IsApplicable(currentPath))
            return new ProfilePersistenceReadiness(false, null, ProjectProfileLookupStatus.InvalidProjectPath);

        var normalizedPath = Path.GetFullPath(currentPath!);
        if (CanPersistNormalizedPath(normalizedPath))
            return new ProfilePersistenceReadiness(true, GetSuccessfulSnapshot(normalizedPath), null);
        if (!ShouldRetryProfileLoadForPersistence(normalizedPath))
            return new ProfilePersistenceReadiness(false, null, GetLoadStatus(normalizedPath));

        var snapshot = await LoadSnapshotWithRetryAsync(normalizedPath, cancellationToken)
            .ConfigureAwait(false);
        return snapshot.Status is ProjectProfileLookupStatus.Found or ProjectProfileLookupStatus.Missing &&
               CanPersistNormalizedPath(normalizedPath)
            ? new ProfilePersistenceReadiness(true, snapshot, null)
            : new ProfilePersistenceReadiness(false, null, snapshot.Status);
    }

    private ProjectProfileLookupStatus? GetLoadStatus(string normalizedPath)
    {
        lock (_loadStateSync)
        {
            return _loadStates.TryGetValue(normalizedPath, out var state)
                ? state.Status
                : null;
        }
    }

    private ProjectProfileLoadSnapshot? GetSuccessfulSnapshot(string normalizedPath)
    {
        lock (_loadStateSync)
        {
            return _loadStates.TryGetValue(normalizedPath, out var state)
                ? state.SuccessfulSnapshot
                : null;
        }
    }

    private async Task<bool> PersistMergedAsync(
        string projectPath,
        ProjectSelectionProfile candidate,
        ProjectSelectionProfile? baseline,
        ProjectProfileMergeFields fields,
        CancellationToken cancellationToken)
    {
        var result = await Task.Run(
            () => ProjectProfileMergeWriter.TryMerge(
                profileStore,
                projectPath,
                candidate,
                baseline,
                fields,
                GuiLookupTimeout,
                cancellationToken: cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.PersistedProfile is null)
            return false;

        var normalizedPath = Path.GetFullPath(projectPath);
        lock (_loadStateSync)
        {
            if (_loadStates.TryGetValue(normalizedPath, out var state))
            {
                var previous = state.SuccessfulSnapshot;
                var localBaseline = previous?.Profile is { } previousProfile
                    ? ProjectProfileMergeWriter.Apply(previousProfile, candidate, fields)
                    : ProjectSelectionProfileBuilder.Clone(candidate);
                var snapshot = new ProjectProfileLoadSnapshot(
                    ProjectProfileLookupStatus.Found,
                    localBaseline,
                    previous?.PersistentMarks);
                _loadStates[normalizedPath] = state with
                {
                    Status = ProjectProfileLookupStatus.Found,
                    SuccessfulSnapshot = snapshot,
                    RetryPersistenceLoad = false
                };
            }
        }
        return true;
    }

    private bool ShouldRetryProfileLoadForPersistence(string normalizedPath)
    {
        lock (_loadStateSync)
        {
            return _loadStates.TryGetValue(normalizedPath, out var state) &&
                   state.RetryPersistenceLoad;
        }
    }

    private bool CanPersistNormalizedPath(string normalizedPath)
    {
        lock (_loadStateSync)
        {
            return !_loadStates.TryGetValue(normalizedPath, out var state) ||
                   state.Status is ProjectProfileLookupStatus.Found or ProjectProfileLookupStatus.Missing;
        }
    }

    private ProfileLoadAttempt BeginLoad(string normalizedPath)
    {
        lock (_loadStateSync)
        {
            var previous = _loadStates.GetValueOrDefault(normalizedPath);
            var hadPrevious = _loadStates.ContainsKey(normalizedPath);
            var revision = checked(++_nextLoadRevision);
            _loadStates[normalizedPath] = new ProfileLoadState(
                ProjectProfileLookupStatus.TemporarilyUnavailable,
                revision,
                previous.SuccessfulSnapshot,
                RetryPersistenceLoad: false);
            return new ProfileLoadAttempt(revision, hadPrevious, previous);
        }
    }

    private void CompleteLoad(
        string normalizedPath,
        long revision,
        ProjectProfileLookupStatus status,
        ProjectProfileLoadSnapshot? successfulSnapshot,
        bool retryPersistenceLoad = false)
    {
        lock (_loadStateSync)
        {
            if (_loadStates.TryGetValue(normalizedPath, out var current) &&
                current.Revision == revision)
            {
                _loadStates[normalizedPath] = new ProfileLoadState(
                    status,
                    revision,
                    successfulSnapshot ?? current.SuccessfulSnapshot,
                    retryPersistenceLoad);
            }
        }
    }

    private void RestoreLoadState(string normalizedPath, ProfileLoadAttempt attempt)
    {
        lock (_loadStateSync)
        {
            if (!_loadStates.TryGetValue(normalizedPath, out var current) ||
                current.Revision != attempt.Revision)
            {
                return;
            }
            if (attempt.HadPrevious)
                _loadStates[normalizedPath] = attempt.Previous;
            else
                _loadStates.Remove(normalizedPath);
        }
    }

    private readonly record struct ProfileLoadState(
        ProjectProfileLookupStatus Status,
        long Revision,
        ProjectProfileLoadSnapshot? SuccessfulSnapshot,
        bool RetryPersistenceLoad);
    private readonly record struct ProfileLoadAttempt(
        long Revision,
        bool HadPrevious,
        ProfileLoadState Previous);
    private readonly record struct ProfilePersistenceReadiness(
        bool CanPersist,
        ProjectProfileLoadSnapshot? RecoveredSnapshot,
        ProjectProfileLookupStatus? BlockedStatus);

    private ProjectSelectionProfile? CaptureProfileForSelectionWrite(
        string currentPath,
        IReadOnlyCollection<string>? selectedPaths)
    {
        if (selectionCoordinator.IsSelectionStateCompleteForPersistence)
            return CaptureCurrentProfile(
                currentPath,
                selectedPaths,
                hasSelectedPathsOverride: true);

        var lookup = profileStore.LookupProfile(currentPath, GuiLookupTimeout);
        return lookup is { Status: ProjectProfileLookupStatus.Found, Profile: not null }
            ? ProjectSelectionProfileBuilder.Clone(lookup.Profile) with
            {
                SelectedPaths = selectedPaths?.ToArray()
            }
            : null;
    }

    private ProjectSelectionProfile CaptureCurrentProfile(
        string currentPath,
        IReadOnlyCollection<string>? selectedPaths = null,
        bool hasSelectedPathsOverride = false)
    {
        var candidate = selectionCoordinator.SnapshotAppliedSelectionForPersistence();
        var applied = candidate is not null && candidate.IsForProject(currentPath)
            ? candidate
            : null;
        var appliedIgnoreStates = applied is null
            ? null
            : selectionCoordinator.GetPersistableIgnoreOptionStates(applied.IgnoreOptionStates);
        var persistedIgnoreStates = appliedIgnoreStates ??
            selectionCoordinator.SnapshotIgnoreOptionStatesForPersistence();
        return ProjectSelectionProfileBuilder.Create(
            visibleExtensions: viewModel.Extensions.Select(option => new SelectionOption(
                option.Name,
                applied?.ExtensionOptionStates.GetValueOrDefault(option.Name) ?? option.IsChecked)),
            visibleIgnoreOptions: viewModel.IgnoreOptions.Select(option => new IgnoreSelectionOption(
                option.Id,
                persistedIgnoreStates?.GetValueOrDefault(option.Id) ?? option.IsChecked)),
            cachedExtensionStates: applied?.ExtensionOptionStates ??
                                   selectionCoordinator.SnapshotExtensionOptionStatesForPersistence(),
            cachedIgnoreOptionStates: persistedIgnoreStates,
            selectedIgnoreOptions: applied is null
                ? selectionCoordinator.GetPersistableSelectedIgnoreOptionIds()
                : selectionCoordinator.GetPersistableSelectedIgnoreOptionIds(applied.SelectedIgnoreOptions),
            extensionComparer: StringComparer.OrdinalIgnoreCase,
            selectedPaths: hasSelectedPathsOverride
                ? selectedPaths
                : selectedPathsProvider?.Invoke());
    }
}

internal sealed class PendingProjectProfileWriteQueue(IProjectProfileStore profileStore)
{
    private static readonly TimeSpan DefaultFlushTimeout = TimeSpan.FromSeconds(5);
    private readonly Dictionary<string, PendingProfileWrite> _pending =
        new(PathComparer.Default);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _nextRevision;

    internal bool HasPending
    {
        get
        {
            if (!_gate.Wait(0))
                return true;
            try
            {
                return _pending.Count > 0;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    internal int Count
    {
        get
        {
            _gate.Wait();
            try
            {
                return _pending.Count;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public async Task<IReadOnlyList<string>> GetPendingProjectPathsAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _pending.Keys.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PersistAsync(
        string projectPath,
        ProjectSelectionProfile profile,
        DateTimeOffset updatedUtc,
        Func<string, bool>? canPersist,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(
                () => PersistCore(projectPath, profile, updatedUtc, canPersist),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnqueueAsync(
        string projectPath,
        ProjectSelectionProfile profile,
        DateTimeOffset updatedUtc,
        CancellationToken cancellationToken)
    {
        var normalizedPath = Path.GetFullPath(projectPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _pending[normalizedPath] = new PendingProfileWrite(
                ProjectSelectionProfileBuilder.Clone(profile),
                updatedUtc,
                Merge: null,
                checked(++_nextRevision));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnqueueMergeAsync(
        string projectPath,
        ProjectSelectionProfile candidate,
        ProjectSelectionProfile? baseline,
        ProjectProfileMergeFields fields,
        DateTimeOffset updatedUtc,
        CancellationToken cancellationToken)
    {
        var normalizedPath = Path.GetFullPath(projectPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _pending[normalizedPath] = new PendingProfileWrite(
                ProjectSelectionProfileBuilder.Clone(candidate),
                updatedUtc,
                new PendingProfileMerge(
                    baseline is null ? null : ProjectSelectionProfileBuilder.Clone(baseline),
                    fields),
                checked(++_nextRevision));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Persist(
        string projectPath,
        ProjectSelectionProfile profile,
        DateTimeOffset updatedUtc)
    {
        _gate.Wait();
        try
        {
            PersistCore(projectPath, profile, updatedUtc, canPersist: null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void PersistCore(
        string projectPath,
        ProjectSelectionProfile profile,
        DateTimeOffset updatedUtc,
        Func<string, bool>? canPersist)
    {
        var normalizedPath = Path.GetFullPath(projectPath);
        FlushCore(canPersist, DefaultFlushTimeout, normalizedPath);
        if (canPersist is not null && !canPersist(normalizedPath))
            return;
        if (profileStore.TrySaveProfile(normalizedPath, profile, updatedUtc))
        {
            _pending.Remove(normalizedPath);
            return;
        }

        _pending[normalizedPath] = new PendingProfileWrite(
            ProjectSelectionProfileBuilder.Clone(profile),
            updatedUtc,
            Merge: null,
            checked(++_nextRevision));
    }

    public async Task<long?> GetPendingRevisionAsync(
        string projectPath,
        CancellationToken cancellationToken)
    {
        var normalizedPath = Path.GetFullPath(projectPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _pending.TryGetValue(normalizedPath, out var pending)
                ? pending.Revision
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemovePendingAsync(
        string projectPath,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        if (expectedRevision is null)
            return;

        var normalizedPath = Path.GetFullPath(projectPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pending.TryGetValue(normalizedPath, out var pending) &&
                pending.Revision == expectedRevision.Value)
            {
                _pending.Remove(normalizedPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CoalesceSelectedPathsAsync(
        string projectPath,
        long? expectedRevision,
        IReadOnlyCollection<string>? selectedPaths,
        CancellationToken cancellationToken)
    {
        if (expectedRevision is null)
            return;

        var normalizedPath = Path.GetFullPath(projectPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_pending.TryGetValue(normalizedPath, out var pending) ||
                pending.Revision != expectedRevision.Value)
            {
                return;
            }

            var profile = ProjectSelectionProfileBuilder.Clone(pending.Profile) with
            {
                SelectedPaths = selectedPaths?.ToArray()
            };
            var merge = pending.Merge is null
                ? null
                : pending.Merge with
                {
                    Fields = pending.Merge.Fields & ~ProjectProfileMergeFields.SelectedPaths
                };
            _pending[normalizedPath] = pending with
            {
                Profile = profile,
                Merge = merge
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Flush(Func<string, bool>? canPersist = null) =>
        _ = Flush(DefaultFlushTimeout, canPersist);

    public ProjectProfileClearStatus ClearAllProfiles()
    {
        _gate.Wait();
        try
        {
            var result = profileStore.ClearAllProfiles();
            if (result == ProjectProfileClearStatus.Cleared)
                _pending.Clear();
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DiscardPendingAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        if (!await _gate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
            return false;

        try
        {
            _pending.Clear();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ProjectProfileFlushResult Flush(
        TimeSpan timeout,
        Func<string, bool>? canPersist = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        var startedTimestamp = Stopwatch.GetTimestamp();
        if (!_gate.Wait(timeout))
            return new ProjectProfileFlushResult(false, 0, 0, -1);

        try
        {
            var remaining = timeout - Stopwatch.GetElapsedTime(startedTimestamp);
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;
            return FlushCore(canPersist, remaining);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ProjectProfileFlushResult FlushCore(
        Func<string, bool>? canPersist,
        TimeSpan lockTimeout,
        string? excludedProjectPath = null)
    {
        var pending = _pending
            .Where(entry =>
                (excludedProjectPath is null ||
                 !PathComparer.Default.Equals(entry.Key, excludedProjectPath)) &&
                (canPersist is null || canPersist(entry.Key)))
            .ToArray();
        if (pending.Length == 0)
            return new ProjectProfileFlushResult(true, 0, 0, _pending.Count);

        var startedTimestamp = Stopwatch.GetTimestamp();
        var attempted = 0;
        var saved = 0;
        foreach (var entry in pending.Where(static entry => entry.Value.Merge is not null))
        {
            var remaining = GetRemainingTimeout(startedTimestamp, lockTimeout);
            if (remaining == TimeSpan.Zero)
                break;

            attempted++;
            var merge = entry.Value.Merge!;
            var result = ProjectProfileMergeWriter.TryMerge(
                profileStore,
                entry.Key,
                entry.Value.Profile,
                merge.Baseline,
                merge.Fields,
                remaining);
            if (!result.Succeeded)
                continue;

            _pending.Remove(entry.Key);
            saved++;
        }

        var requests = pending
            .Where(static entry => entry.Value.Merge is null)
            .Select(static entry => new ProjectProfileSaveRequest(
                entry.Key,
                ProjectSelectionProfileBuilder.Clone(entry.Value.Profile),
                entry.Value.UpdatedUtc))
            .ToArray();
        if (requests.Length > 0)
        {
            attempted += requests.Length;
            var remaining = GetRemainingTimeout(startedTimestamp, lockTimeout);
            if (remaining > TimeSpan.Zero)
            {
                var result = profileStore.TrySaveProfilesWithResult(requests, remaining);
                var savedPaths = result.SavedProjectPaths.ToHashSet(PathComparer.Default);
                foreach (var path in savedPaths)
                    _pending.Remove(path);
                saved += savedPaths.Count;
            }
        }

        return new ProjectProfileFlushResult(
            true,
            attempted,
            saved,
            _pending.Count);
    }

    private static TimeSpan GetRemainingTimeout(long startedTimestamp, TimeSpan timeout)
    {
        var remaining = timeout - Stopwatch.GetElapsedTime(startedTimestamp);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private sealed record PendingProfileWrite(
        ProjectSelectionProfile Profile,
        DateTimeOffset UpdatedUtc,
        PendingProfileMerge? Merge,
        long Revision);

    private sealed record PendingProfileMerge(
        ProjectSelectionProfile? Baseline,
        ProjectProfileMergeFields Fields);
}

internal sealed class PersistentSecretMarkDeltaWriter(
    IPersistentSecretMarkStore store,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    IReadOnlyList<TimeSpan>? retryDelays = null)
{
    // Short retries cover transient cross-process lock contention without ever blocking the UI thread.
    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1)
    ];

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly IReadOnlyList<TimeSpan> _retryDelays = retryDelays ?? DefaultRetryDelays;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<PersistentSecretMarkWriteResult> ApplyAsync(
        string projectPath,
        PersistentSecretMarkDelta delta,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PersistentSecretMarkWriteResult? lastResult = null;
            for (var attempt = 0; attempt <= _retryDelays.Count; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lastResult = await store
                    .ApplyMarkDeltaAsync(projectPath, delta, cancellationToken)
                    .ConfigureAwait(false);
                if (lastResult.Succeeded || !IsRetryable(lastResult.Status) || attempt == _retryDelays.Count)
                    return lastResult;

                await _delay(_retryDelays[attempt], cancellationToken).ConfigureAwait(false);
            }

            return lastResult ?? new PersistentSecretMarkWriteResult(
                PersistentSecretMarkStoreStatus.WriteFailed,
                null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsRetryable(PersistentSecretMarkStoreStatus status) =>
        status is PersistentSecretMarkStoreStatus.TemporarilyUnavailable or
            PersistentSecretMarkStoreStatus.WriteFailed;
}
