using DevProjex.Infrastructure.LiveContext;

namespace DevProjex.Terminal.Tui;

internal sealed partial class TerminalWorkspaceSession
{
	private bool PollLiveSessions()
	{
		if (_stopping || _disposed)
			return false;

		RefreshLiveSessions(force: false);
		return true;
	}

	private void RefreshLiveSessions(bool force)
	{
		var sessions = _state is null
			? Array.Empty<LiveSessionRecord>()
			: _liveSessionRegistry.ReadActive(_state.Plan.SourceRoot);
		if (!force && HaveSameLiveSessions(_liveSessions, sessions))
			return;

		_liveSessions = sessions;
		if (_status is not null && _state is not null &&
			!_operations.IsRunning(WorkspaceOperationKind.TransientStatus))
		{
			_status.Text = BuildStatus(_state, _application.Screen.Width);
		}
	}

	internal string BuildLiveSessionIndicator(IReadOnlyList<LiveSessionRecord> sessions)
	{
		ArgumentNullException.ThrowIfNull(sessions);
		return sessions.Count switch
		{
			0 => string.Empty,
			1 => $"Live context ({LiveSessionRegistry.FormatClientName(sessions[0].ClientName)})",
			_ => $"Live context ({NormalizeLocalizedText(
				_services.Localization.Format("LiveContext.Title.Sessions", sessions.Count),
				_options.Plain,
				_environment.SupportsUnicode)})"
		};
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

	private void FlushLocalProfilePersistence()
	{
		ScheduleLocalProfilePersistence();
		_selectionProfilePersistence.FlushAsync().GetAwaiter().GetResult();
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

	private async Task PersistLocalProfileAsync(
		string projectPath,
		ProjectSelectionProfile profile,
		CancellationToken cancellationToken)
	{
		await Task.Run(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			var lookup = _services.LocalProfileStore.LookupProfile(
				projectPath,
				TimeSpan.FromSeconds(5));
			if (lookup is { Status: ProjectProfileLookupStatus.Found, Profile: not null })
			{
				profile = profile with
				{
					SelectedRootFolders = lookup.Profile.SelectedRootFolders.ToArray(),
					RootFolderStates = lookup.Profile.RootFolderStates is null
						? null
						: new Dictionary<string, bool>(
							lookup.Profile.RootFolderStates,
							ProjectTreePathIdentity.CanonicalComparer),
					MarkedSecrets = lookup.Profile.MarkedSecrets?.ToArray()
				};
			}

			cancellationToken.ThrowIfCancellationRequested();
			var result = _services.LocalProfileStore.TrySaveProfileWithResult(projectPath, profile);
			if (!result.Succeeded)
				throw new IOException("The terminal project profile could not be saved.");
		}, cancellationToken).ConfigureAwait(false);
	}
}
