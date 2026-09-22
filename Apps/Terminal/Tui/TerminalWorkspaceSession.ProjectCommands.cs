using DevProjex.Terminal.Execution;

namespace DevProjex.Terminal.Tui;

internal sealed partial class TerminalWorkspaceSession
{
	private TerminalWorkspaceCommandExecutionResult OpenProjectSource(string? source)
	{
		if (string.IsNullOrWhiteSpace(source))
			return InvalidCommandExecution();

		var expanded = TerminalPathPickerModel.ExpandPath(source);
		var baseDirectory = _state?.Plan.SourceRoot ?? Directory.GetCurrentDirectory();
		var localCandidate = expanded;
		try
		{
			if (!Path.IsPathRooted(localCandidate))
				localCandidate = Path.Combine(baseDirectory, localCandidate);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
			return TerminalWorkspaceCommandExecutionResult.Failure(
				L("Terminal.Tui.Error.ProjectUnavailable"));
		}

		if (TryResolveDirectory(localCandidate, out var project))
		{
			if (!TryResolveAutomaticProfileInteractively(project, out var profile))
				return TerminalWorkspaceCommandExecutionResult.Deferred();
			TryLeaveWorkspace(() => BeginOpenProject(project, profile));
			return TerminalWorkspaceCommandExecutionResult.Deferred();
		}

		if (!RepositoryUrlUtility.IsSupportedCloneSource(source))
		{
			return TerminalWorkspaceCommandExecutionResult.Failure(
				source.Contains("://", StringComparison.Ordinal)
					? L("Git.Error.InvalidUrl")
					: L("Terminal.Tui.Error.ProjectUnavailable"));
		}

		var safeUrl = RepositoryUrlUtility.ToSafeDisplay(source);
		if (!Confirm(
				L("Terminal.Tui.Command.Open.Title"),
				string.Format(
					System.Globalization.CultureInfo.CurrentCulture,
					L("Terminal.Tui.Command.Open.CloneConfirm"),
					safeUrl)))
		{
			return TerminalWorkspaceCommandExecutionResult.Deferred();
		}

		TryLeaveWorkspace(() => BeginCloneRepository(source));
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}

	private TerminalWorkspaceCommandExecutionResult LoadProfile(string? nameOrPath)
	{
		if (_state is null || string.IsNullOrWhiteSpace(nameOrPath))
			return InvalidCommandExecution();
		var path = ResolvePortableProfilePath(nameOrPath);
		if (path is null || !File.Exists(path))
		{
			return TerminalWorkspaceCommandExecutionResult.Failure(
				L("Terminal.Tui.Error.ProfileUnavailable"));
		}

		BeginApplyProfile(new ProjectProfileReference(ProjectProfileSourceKind.Portable, path));
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}

	private TerminalWorkspaceCommandExecutionResult ShowCurrentProfile()
	{
		if (_state is null)
			return InvalidCommandExecution();
		var text = new ProfileCommandHandler(_services, _environment)
			.BuildText(_state.BuildSelection());
		ShowScrollableOverlay(
			L("Terminal.Tui.Command.Profile.Title"),
			text,
			TerminalWorkspaceTheme.Dialog,
			preferredWidth: 92,
			preferredHeight: 24);
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}

	private TerminalWorkspaceCommandExecutionResult ResetCurrentProfile()
	{
		if (_state is null)
			return InvalidCommandExecution();
		if (!Confirm(
				L("Terminal.Tui.Command.Profile.ResetTitle"),
				L("Terminal.Tui.Command.Profile.ResetMessage")))
		{
			return TerminalWorkspaceCommandExecutionResult.Deferred();
		}

		if (!FlushLocalProfilePersistence())
		{
			return TerminalWorkspaceCommandExecutionResult.Failure(
				L("Terminal.Tui.Error.ProfilePersistence"));
		}

		BeginResetProfile();
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}

	private void BeginResetProfile()
	{
		if (_state is not { } current)
			return;
		var projectRoot = current.Plan.SourceRoot;
		var sourceIdentity = current.Plan.SourceIdentity;
		var operationCts = ReplaceActiveOperation();
		TrackActiveOperation(Task.Run(async () =>
		{
			TerminalWorkspaceState? replacement = null;
			try
			{
				replacement = await _controller
					.OpenAsync(
						projectRoot,
						ProjectProfileReference.Standard,
						operationCts.Token,
						sourceIdentity)
					.ConfigureAwait(false);
				var gitCliAvailable = await ResolveGitCliAvailabilityAsync(
						replacement.Plan,
						operationCts.Token)
					.ConfigureAwait(false);
				var canCommit = await InvokeAsync(() =>
					_operations.IsCurrent(WorkspaceOperationKind.Active, operationCts) &&
					_screen == TerminalWorkspaceScreen.Workspace &&
					_state is { } state &&
					ProjectTreePathIdentity.CanonicalComparer.Equals(
						state.Plan.SourceRoot,
						projectRoot)).ConfigureAwait(false);
				if (!canCommit)
					return;

				operationCts.Token.ThrowIfCancellationRequested();
				var status = _services.LocalProfileStore.TryDeleteProfileWithResult(projectRoot);
				if (status != ProjectProfileDeleteStatus.Deleted)
				{
					await ShowCommandFailureAsync(
							status == ProjectProfileDeleteStatus.Partial
								? "DPX-TUI-PROFILE-RESET-PARTIAL"
								: "DPX-TUI-PROFILE-RESET-FAILED",
							L(status == ProjectProfileDeleteStatus.Partial
								? "Terminal.Tui.Command.Profile.ResetPartial"
								: "Terminal.Tui.Command.Profile.ResetFailed"))
						.ConfigureAwait(false);
					return;
				}

				var applied = await InvokeAsync(() =>
				{
					if (_screen != TerminalWorkspaceScreen.Workspace ||
						_state is not { } state ||
						!ProjectTreePathIdentity.CanonicalComparer.Equals(
							state.Plan.SourceRoot,
							projectRoot))
					{
						return false;
					}
					_gitCliAvailable = gitCliAvailable;
					ShowWorkspace(replacement);
					return true;
				}).ConfigureAwait(false);
				if (applied)
					replacement = null;
			}
			catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
			{
			}
			catch (ProjectContextValidationException exception)
			{
				await ShowCommandFailureAsync(
						exception.Code,
						ResolveValidationErrorMessage(exception.Code))
					.ConfigureAwait(false);
			}
			catch
			{
				await ShowCommandFailureAsync(
						"DPX-TUI-PROFILE-RESET-FAILED",
						L("Terminal.Tui.Error.OperationFailed"))
					.ConfigureAwait(false);
			}
			finally
			{
				replacement?.Dispose();
				ReleaseActiveOperation(operationCts);
			}
		}, CancellationToken.None));
	}

	private void BeginApplyProfile(ProjectProfileReference profile)
	{
		if (_state is not { } current)
			return;
		var projectRoot = current.Plan.SourceRoot;
		var sourceIdentity = current.Plan.SourceIdentity;
		var operationCts = ReplaceActiveOperation();
		TrackActiveOperation(Task.Run(async () =>
		{
			TerminalWorkspaceState? replacement = null;
			try
			{
				replacement = await _controller
					.OpenAsync(projectRoot, profile, operationCts.Token, sourceIdentity)
					.ConfigureAwait(false);
				var gitCliAvailable = await ResolveGitCliAvailabilityAsync(
						replacement.Plan,
						operationCts.Token)
					.ConfigureAwait(false);
				var applied = await InvokeAsync(() =>
				{
					if (!_operations.IsCurrent(WorkspaceOperationKind.Active, operationCts) ||
						_screen != TerminalWorkspaceScreen.Workspace)
					{
						return false;
					}
					_gitCliAvailable = gitCliAvailable;
					ShowWorkspace(replacement);
					PublishLoadedProfileAsLocal();
					return true;
				}).ConfigureAwait(false);
				if (applied)
					replacement = null;
			}
			catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
			{
			}
			catch (PortableProjectProfileException exception)
			{
				await ShowCommandFailureAsync(exception.Code, L("Terminal.Error.ProfileInvalid"))
					.ConfigureAwait(false);
			}
			catch (ProjectContextValidationException exception)
			{
				await ShowCommandFailureAsync(
						exception.Code,
						ResolveValidationErrorMessage(exception.Code))
					.ConfigureAwait(false);
			}
			catch
			{
				await ShowCommandFailureAsync(
						"DPX-TUI-PROFILE-APPLY-FAILED",
						L("Terminal.Tui.Error.OperationFailed"))
					.ConfigureAwait(false);
			}
			finally
			{
				replacement?.Dispose();
				ReleaseActiveOperation(operationCts);
			}
		}, CancellationToken.None));
	}

	private string? ResolvePortableProfilePath(string value)
		=> _state is { } state
			? TerminalPortableProfilePathResolver.Resolve(
				value,
				state.Plan.SourceRoot,
				ResolvePortableProfileDirectory())
			: null;

	private string? ResolvePortableProfileDirectory()
	{
		if (_state is null)
			return null;
		var candidate = BuildDefaultExportPath(
			_state.Plan.SourceRoot,
			Directory.GetCurrentDirectory(),
			"devprojex-profile.json");
		return string.IsNullOrWhiteSpace(candidate) ? null : Path.GetDirectoryName(candidate);
	}
}

internal static class TerminalPortableProfilePathResolver
{
	public static bool IsExplicitPath(string value)
	{
		var expanded = TerminalPathPickerModel.ExpandPath(value);
		return Path.IsPathRooted(expanded) ||
			expanded.Contains(Path.DirectorySeparatorChar) ||
			expanded.Contains(Path.AltDirectorySeparatorChar);
	}

	public static string? Resolve(
		string value,
		string workingDirectory,
		string? profileDirectory)
	{
		try
		{
			var expanded = TerminalPathPickerModel.ExpandPath(value);
			if (IsExplicitPath(expanded))
				return TerminalWorkspacePathResolver.Resolve(expanded, workingDirectory);
			if (string.IsNullOrWhiteSpace(profileDirectory))
				return null;

			var fileName = expanded.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
				? expanded
				: expanded + ".json";
			return TerminalWorkspacePathResolver.Resolve(fileName, profileDirectory);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
			return null;
		}
	}
}
