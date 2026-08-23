using System.Globalization;

namespace DevProjex.Terminal.Tui;

internal sealed partial class TerminalWorkspaceSession
{
	private TerminalWorkspaceActionRegistry BuildWorkspaceActionRegistry() =>
		new(BuildWorkspacePaletteItems(), BuildWorkspaceCommandActions());

	private IReadOnlyList<TerminalWorkspaceCommandAction> BuildWorkspaceCommandActions() =>
	[
		CreateCommandAction(TerminalWorkspaceCommandVerb.Set, ExecuteSetCommand),
		CreateCommandAction(TerminalWorkspaceCommandVerb.All, ExecuteAllCommand),
		CreateCommandAction(TerminalWorkspaceCommandVerb.Type, ExecuteTypeCommand),
		CreateCommandAction(TerminalWorkspaceCommandVerb.View, ExecuteViewCommand),
		CreateCommandAction(TerminalWorkspaceCommandVerb.Format, ExecuteFormatCommand),
		CreateCommandAction(TerminalWorkspaceCommandVerb.Search, ExecuteSearchCommand),
		CreateCommandAction(TerminalWorkspaceCommandVerb.Filter, ExecuteFilterCommand),
		CreateCommandAction(TerminalWorkspaceCommandVerb.Export, ExecuteExportCommand),
		CreateCommandAction(TerminalWorkspaceCommandVerb.Copy, ExecuteCopyCommand),
		CreateCommandAction(TerminalWorkspaceCommandVerb.Analyze, ExecuteAnalyzeCommand),
		CreateCommandAction(
			TerminalWorkspaceCommandVerb.Branch,
			ExecuteBranchCommand,
			IsGitCloneCommandAvailable,
			() => L("Terminal.Tui.Command.Error.GitCloneRequired")),
		CreateCommandAction(
			TerminalWorkspaceCommandVerb.Update,
			ExecuteUpdateCommand,
			IsGitCloneCommandAvailable,
			() => L("Terminal.Tui.Command.Error.GitCloneRequired")),
		CreateCommandAction(TerminalWorkspaceCommandVerb.Recent, ExecuteRecentCommand),
		CreateCommandAction(TerminalWorkspaceCommandVerb.Profile, ExecuteProfileCommand),
		CreateCommandAction(TerminalWorkspaceCommandVerb.Refresh, ExecuteRefreshCommand),
		CreateCommandAction(
			TerminalWorkspaceCommandVerb.Help,
			ExecuteHelpCommand,
			static () => true),
		CreateCommandAction(
			TerminalWorkspaceCommandVerb.Quit,
			ExecuteQuitCommand,
			static () => true)
	];

	private TerminalWorkspaceCommandAction CreateCommandAction(
		TerminalWorkspaceCommandVerb verb,
		Func<TerminalWorkspaceCommand, TerminalWorkspaceCommandExecutionResult> execute,
		Func<bool>? isAvailable = null,
		Func<string?>? unavailableMessage = null) =>
		new(
			TerminalWorkspaceCommandCatalog.Get(verb),
			isAvailable ?? (() => _screen == TerminalWorkspaceScreen.Workspace &&
				_state is not null && !HasActiveOperation),
			execute,
			unavailableMessage);

	private bool IsGitCloneCommandAvailable() =>
		_screen == TerminalWorkspaceScreen.Workspace &&
		_state?.Plan.SourceIdentity?.SourceType == ProjectSourceType.GitClone &&
		!HasActiveOperation;

	private TerminalWorkspaceCommandExecutionResult ExecuteSetCommand(
		TerminalWorkspaceCommand command)
	{
		if (_state is null || command.Target is null || command.Enabled is not { } enabled)
			return InvalidCommandExecution();

		var content = ProjectPresentationCatalog.ContentTransformations.FirstOrDefault(
			descriptor => string.Equals(descriptor.Token, command.Target, StringComparison.Ordinal));
		if (content is not null)
		{
			ApplyContentTransformation(content.LegacyOptionId, enabled, originatedFromCommandLine: true);
			return ToggleCommandResult(L(content.LabelKey), enabled);
		}

		var exclusion = ProjectPresentationCatalog.Exclusions.FirstOrDefault(
			descriptor => string.Equals(descriptor.Token, command.Target, StringComparison.Ordinal));
		if (exclusion is not null)
		{
			var values = (GetDisplayedSettingsSelection().Exclusions ?? []).ToHashSet();
			if (enabled)
				values.Add(exclusion.RequireId());
			else
				values.Remove(exclusion.RequireId());
			ApplyExclusions(values, originatedFromCommandLine: true);
			return ToggleCommandResult(L(exclusion.LabelKey), enabled);
		}

		var mode = command.Target switch
		{
			"gitignore" => GitFilteringMode.RespectGitIgnore,
			"tracked" => GitFilteringMode.TrackedFilesOnly,
			_ => (GitFilteringMode?)null
		};
		if (mode is null)
			return InvalidCommandExecution();
		var selection = GetDisplayedSettingsSelection();
		var nextMode = enabled
			? mode.Value
			: GitFilteringMode.None;
		if (enabled)
			_preferredGitMode = mode.Value;
		ApplyPathFilters(
			nextMode,
			selection.Exclusions ?? [],
			originatedFromCommandLine: true);
		return ToggleCommandResult(L(ProjectPresentationCatalog.Get(mode.Value).LabelKey), enabled);
	}

	private TerminalWorkspaceCommandExecutionResult ExecuteAllCommand(
		TerminalWorkspaceCommand command)
	{
		if (_state is null || command.Enabled is not { } enabled)
			return InvalidCommandExecution();
		switch (command.Target)
		{
			case "types":
				ApplyExtensions(enabled ? _state.Plan.AvailableExtensions : [], true);
				break;
			case "exclusions":
				ApplyAllExclusions(enabled, true);
				break;
			case "content":
				ApplyAllContentTransformations(enabled, true);
				break;
			default:
				return InvalidCommandExecution();
		}
		return ToggleCommandResult(L("Settings.All"), enabled);
	}

	private TerminalWorkspaceCommandExecutionResult ExecuteTypeCommand(
		TerminalWorkspaceCommand command)
	{
		if (_state is null || command.Enabled is not { } enabled || command.Values is null)
			return InvalidCommandExecution();
		var values = (GetDisplayedSettingsSelection().Extensions ?? _state.Plan.SelectedExtensions)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var extension in command.Values)
		{
			if (enabled)
				values.Add(extension);
			else
				values.Remove(extension);
		}
		ApplyExtensions(values, true);
		return ToggleCommandResult(string.Join(", ", command.Values), enabled);
	}

	private TerminalWorkspaceCommandExecutionResult ExecuteViewCommand(
		TerminalWorkspaceCommand command)
	{
		if (command.View is not { } view)
			return InvalidCommandExecution();
		_previewView = view;
		RefreshWorkspace();
		SchedulePreviewRefresh();
		return TerminalWorkspaceCommandExecutionResult.Success(string.Format(
			CultureInfo.CurrentCulture,
			L("Terminal.Tui.Command.Result.Value"),
			L("Terminal.Tui.Preview"),
			L(ProjectPresentationCatalog.Get(view).LabelKey)));
	}

	private TerminalWorkspaceCommandExecutionResult ExecuteFormatCommand(
		TerminalWorkspaceCommand command)
	{
		if (command.Format is not { } format)
			return InvalidCommandExecution();
		_format = format;
		RefreshWorkspace();
		SchedulePreviewRefresh();
		return TerminalWorkspaceCommandExecutionResult.Success(string.Format(
			CultureInfo.CurrentCulture,
			L("Terminal.Tui.Command.Result.Value"),
			L("Terminal.Tui.Action.Format"),
			TerminalWorkspace.FormatContextFormat(format)));
	}

	private TerminalWorkspaceCommandExecutionResult ExecuteSearchCommand(
		TerminalWorkspaceCommand command)
	{
		if (_preview is null)
			return InvalidCommandExecution();
		var query = command.Text?.Trim() ?? string.Empty;
		_previewSearchQuery = query.Length == 0 ? null : query;
		if (query.Length == 0)
		{
			CancelPreviewSearch(clearQuery: true);
			_preview.ClearSearch();
			UpdatePanelTitles();
			UpdatePreviewRange();
		}
		else
		{
			SchedulePreviewSearch(
				query,
				showNoResults: false,
				originatedFromCommandLine: true);
		}
		return TerminalWorkspaceCommandExecutionResult.Success(
			query.Length == 0
				? L("Terminal.Tui.Command.Result.SearchCleared")
				: string.Format(
					CultureInfo.CurrentCulture,
					L("Terminal.Tui.Command.Result.Search"),
					query));
	}

	private TerminalWorkspaceCommandExecutionResult ExecuteFilterCommand(
		TerminalWorkspaceCommand command)
	{
		if (_state is null || _tree is null)
			return InvalidCommandExecution();
		var query = command.Text?.Trim() ?? string.Empty;
		_searchQuery = query.Length == 0 ? null : query;
		_selectedTreePath = CaptureCurrentTreePath();
		_state.ApplyTreeFilter(query);
		RefreshWorkspace();
		if (_state.VisibleRows.Count > 0)
		{
			_tree.SelectedItem = 0;
			TrackTreeSelection();
		}
		return TerminalWorkspaceCommandExecutionResult.Success(
			query.Length == 0
				? L("Terminal.Tui.Command.Result.FilterCleared")
				: string.Format(
					CultureInfo.CurrentCulture,
					L("Terminal.Tui.Command.Result.Filter"),
					query));
	}

	private TerminalWorkspaceCommandExecutionResult ExecuteExportCommand(
		TerminalWorkspaceCommand command)
	{
		if (command.Target == "context")
		{
			ExportContext(command.Format, command.Destination, originatedFromCommandLine: true);
			return TerminalWorkspaceCommandExecutionResult.Deferred();
		}
		if (command.ProjectExportFormat is not { } projectFormat ||
			string.IsNullOrWhiteSpace(command.Destination))
		{
			return InvalidCommandExecution();
		}
		ExportProject(projectFormat, command.Destination, originatedFromCommandLine: true);
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}

	private TerminalWorkspaceCommandExecutionResult ExecuteCopyCommand(
		TerminalWorkspaceCommand command)
	{
		CopyCurrentContext(command);
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}

	private TerminalWorkspaceCommandExecutionResult ExecuteAnalyzeCommand(
		TerminalWorkspaceCommand command)
	{
		AnalyzeCurrentContext(originatedFromCommandLine: true);
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}

	private TerminalWorkspaceCommandExecutionResult ExecuteBranchCommand(
		TerminalWorkspaceCommand command)
	{
		SwitchRepositoryBranch(command.Text, originatedFromCommandLine: true);
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}

	private TerminalWorkspaceCommandExecutionResult ExecuteUpdateCommand(
		TerminalWorkspaceCommand command)
	{
		GetRepositoryUpdates(originatedFromCommandLine: true);
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}

	private TerminalWorkspaceCommandExecutionResult ExecuteRecentCommand(
		TerminalWorkspaceCommand command)
	{
		ShowWelcome();
		_application.Invoke(OpenRecentWorkspaces);
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}

	private TerminalWorkspaceCommandExecutionResult ExecuteProfileCommand(
		TerminalWorkspaceCommand command)
	{
		if (command.Target != "save")
			return InvalidCommandExecution();
		if (!string.IsNullOrWhiteSpace(command.Text) && !IsValidProfileName(command.Text))
		{
			return TerminalWorkspaceCommandExecutionResult.Failure(
				L("Terminal.Tui.Command.Error.InvalidProfileName"));
		}
		SaveProfile(command.Text, originatedFromCommandLine: true);
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}

	private TerminalWorkspaceCommandExecutionResult ExecuteRefreshCommand(
		TerminalWorkspaceCommand command)
	{
		RefreshCurrentProject(originatedFromCommandLine: true);
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}

	private static bool IsValidProfileName(string name) =>
		!string.IsNullOrWhiteSpace(name) &&
		!Path.IsPathRooted(name) &&
		name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
		!name.Contains(Path.DirectorySeparatorChar) &&
		!name.Contains(Path.AltDirectorySeparatorChar);

	private TerminalWorkspaceCommandExecutionResult ExecuteHelpCommand(
		TerminalWorkspaceCommand command)
	{
		ShowCommandHelp(command.Target);
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}

	private TerminalWorkspaceCommandExecutionResult ExecuteQuitCommand(
		TerminalWorkspaceCommand command)
	{
		if (HasActiveOperation)
		{
			CancelActiveOperation();
			ShowCancelingOperation();
		}
		else
		{
			RequestExit();
		}
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}

	private TerminalWorkspaceCommandExecutionResult ToggleCommandResult(string label, bool enabled) =>
		TerminalWorkspaceCommandExecutionResult.Success(string.Format(
			CultureInfo.CurrentCulture,
			L("Terminal.Tui.Command.Result.Toggle"),
			label,
			L(enabled
				? "Terminal.Tui.Command.State.On"
				: "Terminal.Tui.Command.State.Off")));

	private TerminalWorkspaceCommandExecutionResult InvalidCommandExecution() =>
		TerminalWorkspaceCommandExecutionResult.Failure(
			L("Terminal.Tui.Command.Error.InvalidState"));

	private void ShowCommandHelp(string? verb)
	{
		var definitions = verb is null
			? TerminalWorkspaceCommandCatalog.All
			: TerminalWorkspaceCommandCatalog.All
				.Where(definition => definition.Token == verb)
				.ToArray();
		var body = string.Join(
			"\n\n",
			definitions.Select(definition =>
				$"{definition.Syntax}\n{L(definition.DescriptionKey)}\n" +
				$"{L("Terminal.Tui.Command.Help.Example")}: {definition.Example}"));
		ShowScrollableOverlay(
			L("Terminal.Tui.Command.Help.OverlayTitle"),
			body,
			TerminalWorkspaceTheme.Dialog,
			preferredWidth: 92,
			preferredHeight: 27);
	}

	private TerminalWorkspaceCommandParseContext BuildCommandParseContext() =>
		new(_state?.Plan.AvailableExtensions ?? []);

	private void OpenCommandLine()
	{
		if (_screen != TerminalWorkspaceScreen.Workspace ||
			_layoutMode == TerminalWorkspaceLayoutMode.TooSmall ||
			_commandLine is null)
		{
			return;
		}
		CancelCommandResult();
		_commandReturnPane = _activePane;
		if (_footer is not null)
			_footer.Visible = false;
		_commandLine.Open();
		_application.LayoutAndDraw();
		_application.AddTimeout(TimeSpan.Zero, () =>
		{
			if (_commandLine?.IsEditing == true)
				_commandLine.RestoreInputFocus();
			return false;
		});
	}

	private void CancelCommandLine()
	{
		if (_commandLine is null)
			return;
		_commandLine.Close();
		RestoreCommandFooterAndFocus();
	}

	private void SubmitCommandLine(string text)
	{
		if (_commandLine is null)
			return;
		_commandLine.Close();
		var normalized = text.Trim();
		if (normalized.Length > 0)
		{
			_commandHistory.Add(normalized);
			_commandHistorySaveTask = PersistCommandHistoryAsync(
				_commandHistory.Entries.ToArray());
		}

		var parse = _commandParser.Parse(text, BuildCommandParseContext());
		if (!parse.IsSuccess)
		{
			ShowCommandResult(FormatCommandError(parse.Error!), success: false);
			return;
		}

		var result = BuildWorkspaceActionRegistry().Execute(parse.Command!);
		switch (result.Status)
		{
			case TerminalWorkspaceCommandExecutionStatus.Success:
				ShowCommandResult(
					result.Message ?? L("Terminal.Tui.Command.Result.Completed"),
					true,
					parse.Command);
				break;
			case TerminalWorkspaceCommandExecutionStatus.Failure:
				ShowCommandResult(result.Message ?? L("Terminal.Tui.Command.Error.InvalidState"), false);
				break;
			case TerminalWorkspaceCommandExecutionStatus.Unavailable:
				ShowCommandResult(
					result.Message ?? L("Terminal.Tui.Command.Error.Unavailable"),
					false);
				break;
			case TerminalWorkspaceCommandExecutionStatus.Deferred:
				RestoreCommandFooterAndFocus();
				break;
			default:
				throw new ArgumentOutOfRangeException();
		}
	}

	private string FormatCommandError(TerminalWorkspaceCommandError error)
	{
		var key = error.Code switch
		{
			TerminalWorkspaceCommandErrorCode.EmptyInput => "Terminal.Tui.Command.Error.Empty",
			TerminalWorkspaceCommandErrorCode.UnterminatedQuote => "Terminal.Tui.Command.Error.Quote",
			TerminalWorkspaceCommandErrorCode.UnknownVerb => "Terminal.Tui.Command.Error.UnknownVerb",
			TerminalWorkspaceCommandErrorCode.MissingArgument => "Terminal.Tui.Command.Error.Missing",
			TerminalWorkspaceCommandErrorCode.UnexpectedArgument => "Terminal.Tui.Command.Error.Unexpected",
			TerminalWorkspaceCommandErrorCode.UnknownToken => "Terminal.Tui.Command.Error.UnknownToken",
			TerminalWorkspaceCommandErrorCode.InvalidValue => "Terminal.Tui.Command.Error.InvalidValue",
			_ => throw new ArgumentOutOfRangeException()
		};
		var value = error.Value ?? string.Empty;
		var message = string.Format(
			CultureInfo.CurrentCulture,
			L(key),
			value,
			error.Position + 1);
		if (error.Candidates.Count > 0)
		{
			message += " " + string.Format(
				CultureInfo.CurrentCulture,
				L("Terminal.Tui.Command.Error.Similar"),
				string.Join(", ", error.Candidates));
		}
		return message;
	}

	private void ShowCommandResult(
		string message,
		bool success,
		TerminalWorkspaceCommand? command = null)
	{
		if (_commandLine is null || _layoutMode == TerminalWorkspaceLayoutMode.TooSmall)
			return;
		CancelCommandResult();
		_activeCommandResult = success ? command : null;
		if (_footer is not null)
			_footer.Visible = false;
		var singleLineMessage = string.Join(
			" · ",
			message.Split(
				["\r\n", "\n", "\r"],
				StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
		_commandLine.ShowResult(singleLineMessage, success);
		_application.LayoutAndDraw();
		var resultCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
		_commandResultCts = resultCts;
		_commandResultTask = RestoreCommandFooterAfterDelayAsync(resultCts);
	}

	private async Task RestoreCommandFooterAfterDelayAsync(CancellationTokenSource resultCts)
	{
		try
		{
			await Task.Delay(TimeSpan.FromMilliseconds(2750), resultCts.Token).ConfigureAwait(false);
			await InvokeAsync(() =>
			{
				if (!ReferenceEquals(_commandResultCts, resultCts))
					return false;
				_commandResultCts = null;
				_commandResultTask = null;
				_activeCommandResult = null;
				resultCts.Dispose();
				_commandLine?.Close();
				RestoreCommandFooterAndFocus();
				return true;
			}).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (resultCts.IsCancellationRequested)
		{
		}
	}

	private void CancelCommandResult()
	{
		CancelAndDispose(ref _commandResultCts);
		_commandResultTask = null;
		_activeCommandResult = null;
	}

	private void RefreshAppliedCommandResult()
	{
		if (_commandLine?.IsShowingResult != true ||
			_activeCommandResult is not
			{
				Definition.Verb: TerminalWorkspaceCommandVerb.Set,
				Target: { } target,
				Enabled: { } enabled
			})
		{
			return;
		}

		var row = _contentControlRows?.FirstOrDefault(candidate =>
			string.Equals(candidate.Key, $"content:{target}", StringComparison.Ordinal));
		if (row is null)
			return;
		var message = ToggleCommandResult(row.Label, enabled).Message;
		if (message is null)
			return;
		_commandLine.ShowResult(message, success: true);
		_application.LayoutAndDraw();
	}

	private void RestoreCommandFooterAndFocus()
	{
		if (_footer is not null && _layoutMode != TerminalWorkspaceLayoutMode.TooSmall)
		{
			_footer.Visible = true;
			UpdateFooter();
		}
		if (_screen != TerminalWorkspaceScreen.Workspace)
			return;
		FocusPane(_commandReturnPane);
		_application.LayoutAndDraw();
	}

	private async Task PersistCommandHistoryAsync(IReadOnlyList<string> history)
	{
		try
		{
			await _services.TerminalSettingsStore
				.SaveCommandHistoryAsync(history, CancellationToken.None)
				.ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Command execution remains usable when the per-user history cannot be persisted.
		}
	}

	private Task ShowCommandFailureAsync(string code, string message)
	{
		if (_stopping)
			return Task.CompletedTask;
		return InvokeAsync(() =>
		{
			SetWorkspaceBusy(null);
			RefreshWorkspace();
			ShowCommandResult($"{message} ({code})", success: false);
			return true;
		});
	}
}
