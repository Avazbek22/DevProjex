using System.Globalization;
using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Tui;

internal sealed partial class TerminalWorkspaceSession
{
	private TerminalWorkspaceActionRegistry BuildWorkspaceActionRegistry()
	{
		var key = new TerminalWorkspaceActionRegistryCacheKey(
			_state?.Revision ?? -1,
			_services.Localization.CurrentLanguage,
			_previewView,
			_format);
		if (_workspaceActionRegistry is not null &&
			_workspaceActionRegistryKey == key)
		{
			return _workspaceActionRegistry;
		}

		_workspaceActionRegistry = new TerminalWorkspaceActionRegistry(
			BuildWorkspacePaletteItems(),
			BuildWorkspaceCommandActions());
		_workspaceActionRegistryKey = key;
		return _workspaceActionRegistry;
	}

	private IReadOnlyList<TerminalWorkspaceCommandAction> BuildWorkspaceCommandActions() =>
		TerminalWorkspaceCommandCatalog.All
			.Select(CreateCommandAction)
			.ToArray();

	private TerminalWorkspaceCommandAction CreateCommandAction(
		TerminalWorkspaceCommandDefinition definition) =>
		new(
			definition,
			() => definition.Availability switch
			{
				TerminalWorkspaceCommandAvailability.Always => true,
				TerminalWorkspaceCommandAvailability.GitClone => IsGitCloneCommandAvailable(),
				_ => _screen == TerminalWorkspaceScreen.Workspace &&
				     _state is not null && !HasActiveOperation
			},
			command => definition.Handler(this, command),
			definition.Availability == TerminalWorkspaceCommandAvailability.GitClone
				? () => L("Terminal.Tui.Command.Error.GitCloneRequired")
				: null);

	private bool IsGitCloneCommandAvailable() =>
		_screen == TerminalWorkspaceScreen.Workspace &&
		_state?.Plan.SourceIdentity?.SourceType == ProjectSourceType.GitClone &&
		!HasActiveOperation;

	internal TerminalWorkspaceCommandExecutionResult ExecuteSetCommand(
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

	internal TerminalWorkspaceCommandExecutionResult ExecuteAllCommand(
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

	internal TerminalWorkspaceCommandExecutionResult ExecuteTypeCommand(
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

	internal TerminalWorkspaceCommandExecutionResult ExecuteViewCommand(
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

	internal TerminalWorkspaceCommandExecutionResult ExecuteFormatCommand(
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

	internal TerminalWorkspaceCommandExecutionResult ExecuteSearchCommand(
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

	internal TerminalWorkspaceCommandExecutionResult ExecuteFilterCommand(
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

	internal TerminalWorkspaceCommandExecutionResult ExecuteExportCommand(
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

	internal TerminalWorkspaceCommandExecutionResult ExecuteCopyCommand(
		TerminalWorkspaceCommand command)
	{
		CopyCurrentContext(command);
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}

	internal TerminalWorkspaceCommandExecutionResult ExecuteAnalyzeCommand(
		TerminalWorkspaceCommand command)
	{
		AnalyzeCurrentContext(originatedFromCommandLine: true);
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}

	internal TerminalWorkspaceCommandExecutionResult ExecuteBranchCommand(
		TerminalWorkspaceCommand command)
	{
		SwitchRepositoryBranch(command.Text, originatedFromCommandLine: true);
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}

	internal TerminalWorkspaceCommandExecutionResult ExecuteUpdateCommand(
		TerminalWorkspaceCommand command)
	{
		GetRepositoryUpdates(originatedFromCommandLine: true);
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}

	internal TerminalWorkspaceCommandExecutionResult ExecuteRecentCommand(
		TerminalWorkspaceCommand command)
	{
		return TryLeaveWorkspace(() =>
			{
				ShowWelcome();
				_application.Invoke(OpenRecentWorkspaces);
			})
			? TerminalWorkspaceCommandExecutionResult.Deferred()
			: TerminalWorkspaceCommandExecutionResult.Unavailable();
	}

	internal TerminalWorkspaceCommandExecutionResult ExecuteProfileCommand(
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

	internal TerminalWorkspaceCommandExecutionResult ExecuteRefreshCommand(
		TerminalWorkspaceCommand command)
	{
		RefreshCurrentProject(originatedFromCommandLine: true);
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}

	internal TerminalWorkspaceCommandExecutionResult ExecuteLanguageCommand(
		TerminalWorkspaceCommand command)
	{
		var availableCodes = string.Join(' ', CliChoiceSets.Language.Tokens);
		if (command.Text is null)
		{
			ShowScrollableOverlay(
				L("Terminal.Tui.Command.Language.Title"),
				string.Format(
					CultureInfo.CurrentCulture,
					L("Terminal.Tui.Command.Language.Result.Current"),
					AppLanguageUtility.ToCode(_services.Localization.CurrentLanguage),
					availableCodes),
				TerminalWorkspaceTheme.Dialog,
				preferredWidth: 88,
				preferredHeight: 10);
			return TerminalWorkspaceCommandExecutionResult.Deferred();
		}

		if (!AppLanguageUtility.TryParseCode(command.Text, out var language))
			return InvalidCommandExecution();

		_services.Localization.SetLanguage(language);
		TrackBackgroundTask(PersistLanguageAsync(language));
		return TerminalWorkspaceCommandExecutionResult.Success(string.Format(
			CultureInfo.CurrentCulture,
			L("Terminal.Tui.Command.Language.Result.Changed"),
			AppLanguageUtility.ToCode(language)));
	}

	private async Task PersistLanguageAsync(AppLanguage language)
	{
		try
		{
			await _services.TerminalSettingsStore
				.SaveLanguageAsync(language, CancellationToken.None)
				.ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// A read-only configuration directory must not break the live language switch.
		}
	}

	private static bool IsValidProfileName(string name) =>
		!string.IsNullOrWhiteSpace(name) &&
		!Path.IsPathRooted(name) &&
		name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
		!name.Contains(Path.DirectorySeparatorChar) &&
		!name.Contains(Path.AltDirectorySeparatorChar);

	internal TerminalWorkspaceCommandExecutionResult ExecuteHelpCommand(
		TerminalWorkspaceCommand command)
	{
		ShowCommandHelp(command.Target);
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}

	internal TerminalWorkspaceCommandExecutionResult ExecuteDiagnosticsCommand(
		TerminalWorkspaceCommand command)
	{
		ShowDiagnostics();
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}

	internal TerminalWorkspaceCommandExecutionResult ExecuteQuitCommand(
		TerminalWorkspaceCommand command)
	{
		TryExitWorkspace();
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
		_screen == TerminalWorkspaceScreen.Welcome
			? new([], new HashSet<TerminalWorkspaceCommandVerb>
			{
				TerminalWorkspaceCommandVerb.Recent,
				TerminalWorkspaceCommandVerb.Language,
				TerminalWorkspaceCommandVerb.Help,
				TerminalWorkspaceCommandVerb.Quit
			})
			: new(
				_state?.Plan.AvailableExtensions ?? [],
				WorkingDirectory: _state?.Plan.SourceRoot ?? Directory.GetCurrentDirectory());

	private void OpenCommandLine(string initialText = "")
	{
		if ((_screen != TerminalWorkspaceScreen.Workspace && _screen != TerminalWorkspaceScreen.Welcome) ||
			_layoutMode == TerminalWorkspaceLayoutMode.TooSmall ||
			_operationProgress is not null ||
			_commandLine is null)
		{
			return;
		}
		CancelCommandResult();
		_commandReturnPane = _activePane;
		if (_footer is not null)
			_footer.Visible = false;
		if (_welcomeFooter is not null)
			_welcomeFooter.Visible = false;
		_commandLine.Open(initialText);
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
			if (_commandHistoryPersistence.Enqueue(_commandHistory.Entries.ToArray()) is { } saveTask)
				TrackBackgroundTask(saveTask);
		}

		var parse = _commandParser.Parse(text, BuildCommandParseContext());
		if (!parse.IsSuccess)
		{
			ShowCommandResult(FormatCommandError(parse.Error!), success: false);
			return;
		}

		var result = _screen == TerminalWorkspaceScreen.Welcome
			? ExecuteWelcomeCommand(parse.Command!)
			: BuildWorkspaceActionRegistry().Execute(parse.Command!);
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

	private TerminalWorkspaceCommandExecutionResult ExecuteWelcomeCommand(TerminalWorkspaceCommand command) =>
		command.Definition.Verb switch
		{
			TerminalWorkspaceCommandVerb.Recent => ExecuteRecentCommand(command),
			TerminalWorkspaceCommandVerb.Language => ExecuteLanguageCommand(command),
			TerminalWorkspaceCommandVerb.Help => ExecuteHelpCommand(command),
			TerminalWorkspaceCommandVerb.Quit => ExecuteQuitCommand(command),
			_ => TerminalWorkspaceCommandExecutionResult.Unavailable()
		};

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
			TerminalWorkspaceCommandErrorCode.UnknownLanguage =>
				"Terminal.Tui.Command.Language.Error.Unknown",
			_ => throw new ArgumentOutOfRangeException()
		};
		var value = error.Value ?? string.Empty;
		if (error.Code == TerminalWorkspaceCommandErrorCode.UnknownLanguage)
		{
			return string.Format(
				CultureInfo.CurrentCulture,
				L(key),
				value,
				string.Join(' ', error.Candidates));
		}
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
		if (message.Contains('\n') || message.Contains('\r'))
		{
			RestoreCommandFooterAndFocus();
			ShowScrollableOverlay(
				success ? L("Terminal.Tui.Command.Result.Completed") : L("Terminal.Tui.Error"),
				message,
				success ? TerminalWorkspaceTheme.Dialog : TerminalWorkspaceTheme.Warning,
				preferredWidth: 92,
				preferredHeight: 24);
			return;
		}
		if (_footer is not null)
			_footer.Visible = false;
		if (_welcomeFooter is not null)
			_welcomeFooter.Visible = false;
		var singleLineMessage = NormalizeCommandResult(message);
		_commandLine.ShowResult(singleLineMessage, success);
		_application.LayoutAndDraw();
		if (success)
		{
			var resultCts = _operations.Start(WorkspaceOperationKind.CommandResult);
			TrackOperation(
				WorkspaceOperationKind.CommandResult,
				resultCts,
				RestoreCommandFooterAfterDelayAsync(resultCts));
		}
	}

	internal static string NormalizeCommandResult(string message) =>
		TerminalTextEscaping.EscapeSingleLine(string.Join(
			" · ",
			message.Split(
				["\r\n", "\n", "\r"],
				StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));

	private async Task RestoreCommandFooterAfterDelayAsync(CancellationTokenSource resultCts)
	{
		try
		{
			await Task.Delay(TimeSpan.FromMilliseconds(2750), resultCts.Token).ConfigureAwait(false);
			await InvokeAsync(() =>
			{
				if (!_operations.IsCurrent(WorkspaceOperationKind.CommandResult, resultCts))
					return false;
				_activeCommandResult = null;
				_commandLine?.Close();
				RestoreCommandFooterAndFocus();
				return true;
			}).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (resultCts.IsCancellationRequested)
		{
		}
		finally
		{
			_operations.Complete(WorkspaceOperationKind.CommandResult, resultCts);
		}
	}

	private void CancelCommandResult()
	{
		_operations.Cancel(WorkspaceOperationKind.CommandResult);
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
		if (_welcomeFooter is not null && _layoutMode != TerminalWorkspaceLayoutMode.TooSmall)
			_welcomeFooter.Visible = true;
		if (_screen != TerminalWorkspaceScreen.Workspace)
		{
			_welcomeList?.SetFocus();
			_application.LayoutAndDraw();
			return;
		}
		FocusPane(_commandReturnPane);
		_application.LayoutAndDraw();
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

internal readonly record struct TerminalWorkspaceActionRegistryCacheKey(
	long Revision,
	AppLanguage Language,
	ProjectContextView PreviewView,
	ProjectContextDocumentFormat Format);
