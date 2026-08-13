using DevProjex.Application.Models;

namespace DevProjex.Avalonia.Coordinators;

public sealed class ProjectProfilePersistenceCoordinator(
    MainWindowViewModel viewModel,
    SelectionSyncCoordinator selectionCoordinator,
    IProjectProfileStore profileStore,
    SecretRedactionSession secretRedactionSession,
	Func<string?>? activeProjectPathProvider = null)
{
	private static readonly TimeSpan GuiLookupTimeout = TimeSpan.FromMilliseconds(200);
    private readonly PendingProjectProfileWriteQueue _pendingWrites = new(profileStore);
	private readonly PersistentSecretMarkDeltaWriter? _markWriter =
		profileStore is IPersistentSecretMarkStore markStore
			? new PersistentSecretMarkDeltaWriter(markStore)
			: null;
	private readonly object _loadStateSync = new();
	private readonly Dictionary<string, ProfileLoadState> _loadStates =
		new(PathComparer.Default);
	private long _nextLoadRevision;

    public bool EnsureStorageExists() => profileStore.EnsureStorageExists();

    public void ClearAllProfiles() => profileStore.ClearAllProfiles();

	public async Task PersistIfNeededAsync(
		string? currentPath,
		CancellationToken cancellationToken = default)
    {
		if (!CanPersist(currentPath))
            return;

        var profile = CaptureCurrentProfile();
        await _pendingWrites
			.PersistAsync(
				currentPath!,
				profile,
				DateTimeOffset.UtcNow,
				CanPersistNormalizedPath,
				cancellationToken)
			.ConfigureAwait(false);
    }

	public async Task<PersistentSecretMarkWriteResult> ApplyMarkDeltaAsync(
		string? currentPath,
		PersistentSecretMarkDelta delta,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(delta);
		if (!CanPersist(currentPath) || _markWriter is null)
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
				CompleteLoad(normalizedPath, attempt.Revision, result.Status);
				return new ProjectProfileLoadSnapshot(result.Status, null, null);
			}

			var marksResult = await LoadPersistentMarksAsync(
				normalizedPath,
				result.Profile,
				cancellationToken).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			if (!marksResult.Succeeded || marksResult.Snapshot is null)
			{
				var unavailableStatus = MapMarkStoreStatus(marksResult.Status);
				CompleteLoad(normalizedPath, attempt.Revision, unavailableStatus);
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
				CompleteLoad(normalizedPath, attempt.Revision, unavailableStatus);
				return new ProjectProfileLoadSnapshot(unavailableStatus, null, null);
			}

			CompleteLoad(normalizedPath, attempt.Revision, result.Status);
			return new ProjectProfileLoadSnapshot(result.Status, result.Profile, marksResult.Snapshot);
		}
		catch
		{
			RestoreLoadState(normalizedPath, attempt);
			throw;
		}
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

    public void FlushPending() => _pendingWrites.Flush(CanPersistNormalizedPath);

    private bool IsApplicable(string? currentPath)
    {
        return !string.IsNullOrWhiteSpace(currentPath);
    }

	private bool CanPersist(string? currentPath)
	{
		if (!IsApplicable(currentPath))
			return false;
		return CanPersistNormalizedPath(Path.GetFullPath(currentPath!));
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
				revision);
			return new ProfileLoadAttempt(revision, hadPrevious, previous);
		}
	}

	private void CompleteLoad(
		string normalizedPath,
		long revision,
		ProjectProfileLookupStatus status)
	{
		lock (_loadStateSync)
		{
			if (_loadStates.TryGetValue(normalizedPath, out var current) &&
			    current.Revision == revision)
			{
				_loadStates[normalizedPath] = new ProfileLoadState(status, revision);
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

	private readonly record struct ProfileLoadState(ProjectProfileLookupStatus Status, long Revision);
	private readonly record struct ProfileLoadAttempt(
		long Revision,
		bool HadPrevious,
		ProfileLoadState Previous);

    private ProjectSelectionProfile CaptureCurrentProfile()
    {
        var applied = selectionCoordinator.SnapshotAppliedSelectionForPersistence();
        return ProjectSelectionProfileBuilder.Create(
            visibleExtensions: viewModel.Extensions.Select(option => new SelectionOption(
                option.Name,
                applied?.ExtensionOptionStates.GetValueOrDefault(option.Name) ?? option.IsChecked)),
            visibleIgnoreOptions: viewModel.IgnoreOptions.Select(option => new IgnoreSelectionOption(
                option.Id,
                applied?.IgnoreOptionStates.GetValueOrDefault(option.Id) ?? option.IsChecked)),
            cachedExtensionStates: applied?.ExtensionOptionStates ??
                                   selectionCoordinator.SnapshotExtensionOptionStatesForPersistence(),
            cachedIgnoreOptionStates: applied?.IgnoreOptionStates ??
                                      selectionCoordinator.SnapshotIgnoreOptionStatesForPersistence(),
            selectedIgnoreOptions: applied?.SelectedIgnoreOptions ??
                                   selectionCoordinator.GetSelectedIgnoreOptionIds(),
			extensionComparer: StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed class PendingProjectProfileWriteQueue(IProjectProfileStore profileStore)
{
    private readonly Dictionary<string, PendingProfileWrite> _pending =
        new(PathComparer.Default);

	private readonly SemaphoreSlim _gate = new(1, 1);

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
		FlushCore(canPersist);
        var normalizedPath = Path.GetFullPath(projectPath);
		if (canPersist is not null && !canPersist(normalizedPath))
			return;
        if (profileStore.TrySaveProfile(normalizedPath, profile, updatedUtc))
        {
            _pending.Remove(normalizedPath);
            return;
        }

        _pending[normalizedPath] = new PendingProfileWrite(
            ProjectSelectionProfileBuilder.Clone(profile),
            updatedUtc);
    }

	public void Flush(Func<string, bool>? canPersist = null)
	{
		_gate.Wait();
		try
		{
			FlushCore(canPersist);
		}
		finally
		{
			_gate.Release();
		}
	}

	private void FlushCore(Func<string, bool>? canPersist)
	{
        foreach (var (path, pending) in _pending.ToArray())
        {
			if (canPersist is not null && !canPersist(path))
				continue;
            if (!profileStore.TrySaveProfile(
                    path,
                    ProjectSelectionProfileBuilder.Clone(pending.Profile),
                    pending.UpdatedUtc))
            {
                continue;
            }

            _pending.Remove(path);
        }
    }

    private sealed record PendingProfileWrite(
        ProjectSelectionProfile Profile,
        DateTimeOffset UpdatedUtc);
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
				lastResult = await Task.Run(
					async () => await store
						.ApplyMarkDeltaAsync(projectPath, delta, cancellationToken)
						.ConfigureAwait(false),
					cancellationToken).ConfigureAwait(false);
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
