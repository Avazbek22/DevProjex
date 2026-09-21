using DevProjex.Infrastructure.LiveContext;
using DevProjex.Infrastructure.ProjectProfiles;

namespace DevProjex.Terminal.Tui;

internal sealed partial class TerminalWorkspaceSession
{
	private bool PollLiveSessions()
	{
		if (_stopping || _disposed)
			return false;

		RefreshLiveSessions(force: false);
		ScheduleAgentJournalRefresh();
		return true;
	}

	private void RefreshLiveSessions(bool force)
	{
		var sessions = _state is null
			? Array.Empty<LiveSessionRecord>()
			: _liveSessionRegistry.ReadActive(_state.Plan.SourceRoot)
				.Where(static session => session.Mode == AgentJournalMode.Live)
				.ToArray();
		if (!force && HaveSameLiveSessions(_liveSessions, sessions))
			return;

		_liveSessions = sessions;
		ScheduleAgentJournalRefresh();
		if (_status is not null && _state is not null &&
			!_operations.IsRunning(WorkspaceOperationKind.TransientStatus))
		{
			_status.Text = BuildStatus(_state, _application.Screen.Width);
		}
	}

	private void ScheduleAgentJournalRefresh()
	{
		if (!_agentActivityEnabled || _state is null || _stopping ||
			Interlocked.CompareExchange(ref _agentJournalRefreshInProgress, 1, 0) != 0)
		{
			return;
		}

		var projectRoot = _state.Plan.SourceRoot;
		TrackBackgroundTask(Task.Run(
			() => RefreshAgentJournalAsync(projectRoot),
			CancellationToken.None));
	}

	private async Task RefreshAgentJournalAsync(string projectRoot)
	{
		try
		{
			var sessions = await _agentJournalStore.Value
				.ListSessionsAsync(projectRoot, limit: 10, _sessionCts.Token)
				.ConfigureAwait(false);
			var session = sessions.FirstOrDefault(static candidate =>
				candidate.IsLive && candidate.Mode == AgentJournalMode.Live);
			var receipt = session is null
				? null
				: await _agentJournalStore.Value
					.ReadReceiptAsync(session.Id, _sessionCts.Token)
					.ConfigureAwait(false);
			var snapshot = receipt is null
				? null
				: TerminalAgentJournalSnapshot.Create(projectRoot, receipt);
			await InvokeAsync(() =>
			{
				if (!_agentActivityEnabled || _state is null ||
					!ProjectTreePathIdentity.CanonicalComparer.Equals(
						_state.Plan.SourceRoot,
						projectRoot))
				{
					return false;
				}
				_agentJournalSnapshot = snapshot;
				_state.SetAgentActivity(
					enabled: true,
					snapshot?.DeliveredPathCalls.Keys);
				RefreshWorkspace();
				return true;
			}).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (_sessionCts.IsCancellationRequested)
		{
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// The status projection is optional; explicit journal commands report storage errors.
		}
		finally
		{
			Interlocked.Exchange(ref _agentJournalRefreshInProgress, 0);
		}
	}

	internal string BuildLiveSessionIndicator(IReadOnlyList<LiveSessionRecord> sessions)
	{
		return TerminalAgentJournalPresentation.BuildLiveSessionIndicator(
			sessions,
			count => NormalizeLocalizedText(
				_services.Localization.Format("LiveContext.Title.Sessions", count),
				_options.Plain,
				_environment.SupportsUnicode));
	}

	private static bool HaveSameLiveSessions(
		IReadOnlyList<LiveSessionRecord> current,
		IReadOnlyList<LiveSessionRecord> next)
	{
		if (current.Count != next.Count)
			return false;
		for (var index = 0; index < current.Count; index++)
		{
			if (current[index].Pid != next[index].Pid ||
				current[index].ProcessStartUtc != next[index].ProcessStartUtc ||
				!StringComparer.Ordinal.Equals(current[index].ClientName, next[index].ClientName))
			{
				return false;
			}
		}
		return true;
	}

	private void ScheduleLocalProfilePersistence()
	{
		if (_state is not { } state || _stopping)
			return;

		_selectionProfilePersistence.Schedule(
			state.Plan.SourceRoot,
			CaptureLocalProfile(state));
	}

	private bool FlushLocalProfilePersistence()
	{
		return _selectionProfilePersistence.FlushAsync().GetAwaiter().GetResult();
	}

	private static ProjectSelectionProfile CaptureLocalProfile(TerminalWorkspaceState state)
	{
		var selection = state.BuildSelection();
		var selectedIgnoreOptions = ProjectSelectionAdapter.ToIgnoreOptions(selection).ToArray();
		var ignoreStates = Enum.GetValues<IgnoreOptionId>().ToDictionary(
			static option => option,
			selectedIgnoreOptions.Contains);

		return new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: state.Plan.SelectedExtensions.ToArray(),
			SelectedIgnoreOptions: selectedIgnoreOptions,
			RootFolderStates: null,
			ExtensionStates: new Dictionary<string, bool>(
				state.ExtensionOptionStates,
				StringComparer.OrdinalIgnoreCase),
			IgnoreOptionStates: ignoreStates,
			SelectedPaths: selection.SelectedPaths?.ToArray());
	}

	private async Task<ProjectProfilePersistenceResult> PersistLocalProfileAsync(
		string projectPath,
		ProjectSelectionProfile profile,
		CancellationToken cancellationToken)
	{
		return await Task.Run(() =>
		{
			ProjectSelectionProfile? baseline;
			lock (_localProfileBaselineSync)
				baseline = _localProfileBaseline;
			var result = ProjectProfileMergeWriter.TryMerge(
				_services.LocalProfileStore,
				projectPath,
				profile,
				baseline,
				ProjectProfileMergeFields.Extensions |
				ProjectProfileMergeFields.IgnoreOptions |
				ProjectProfileMergeFields.ExtensionStates |
				ProjectProfileMergeFields.IgnoreOptionStates |
				ProjectProfileMergeFields.SelectedPaths,
				TimeSpan.FromSeconds(5),
				cancellationToken: cancellationToken);
			if (!result.Succeeded)
			{
				return ProjectProfilePersistenceResult.Failed(
					"The terminal project profile could not be saved.");
			}
			lock (_localProfileBaselineSync)
				_localProfileBaseline = result.PersistedProfile ?? profile;
			return ProjectProfilePersistenceResult.Saved();
		}, cancellationToken).ConfigureAwait(false);
	}
}
