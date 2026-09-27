using System.Collections.ObjectModel;
using System.Globalization;
using DevProjex.Kernel.Models;
using DevProjex.Terminal.Execution;
using DevProjex.Terminal.Rendering;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

#pragma warning disable CS0618

internal sealed partial class TerminalWorkspaceSession
{
	private string AgentJournalTitle => AgentJournalText("AgentJournal.Title", "Agent journal");

	private TerminalWorkspaceCommandExecutionResult ExecuteAgentJournalCommand(
		TerminalWorkspaceCommand command)
	{
		if (_state is null)
			return InvalidCommandExecution();
		if (command.McpAction == TerminalWorkspaceMcpAction.ClearLog)
		{
			var projectName = Path.GetFileName(Path.TrimEndingDirectorySeparator(_state.Plan.SourceRoot));
			if (!Confirm(
				AgentJournalText("AgentJournal.Clear.Title", "Clear agent journal"),
				string.Format(
					CultureInfo.CurrentCulture,
					AgentJournalText(
						"AgentJournal.Clear.ProjectMessage",
						"Delete the history for project “{0}”? Project files will not be changed."),
					projectName)))
				return TerminalWorkspaceCommandExecutionResult.Deferred();
		}

		var operationCts = ReplaceActiveOperation();
		TrackActiveOperation(Task.Run(
			() => RunAgentJournalCommandAsync(command, _state.Plan.SourceRoot, operationCts),
			CancellationToken.None));
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}

	private async Task RunAgentJournalCommandAsync(
		TerminalWorkspaceCommand command,
		string projectRoot,
		CancellationTokenSource operationCts)
	{
		try
		{
			switch (command.McpAction)
			{
				case TerminalWorkspaceMcpAction.ShowLog:
					await ShowAgentJournalAsync(
						projectRoot,
						command.Target,
						operationCts.Token).ConfigureAwait(false);
					break;
				case TerminalWorkspaceMcpAction.ExportLog:
					await ExportAgentJournalAsync(
						projectRoot,
						command,
						operationCts.Token).ConfigureAwait(false);
					break;
				case TerminalWorkspaceMcpAction.ClearLog:
					var removed = await _agentJournalStore.Value
						.ClearAsync(projectRoot, operationCts.Token)
						.ConfigureAwait(false);
					await InvokeAsync(() =>
					{
						_agentJournalSnapshot = null;
						_state?.SetAgentActivity(_agentActivityEnabled, null);
						ShowTransientStatus(
							$"Completed journal sessions cleared ({removed:N0}); active sessions preserved.",
							TerminalWorkspaceTheme.Success);
						return true;
					}).ConfigureAwait(false);
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(command));
			}
		}
		catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
		{
		}
		catch (Exception exception)
		{
			var error = MapAgentJournalFailure(exception, _services.Localization);
			await ShowAgentJournalErrorAsync(error.Code, error.Message).ConfigureAwait(false);
		}
		finally
		{
			ReleaseActiveOperation(operationCts);
		}
	}

	// The command runs as a tracked background task, so an exception left unhandled here
	// would be swallowed without any visible result.
	internal static TerminalError MapAgentJournalFailure(
		Exception exception,
		LocalizationService localization) =>
		exception switch
		{
			OutputDestinationConflictException => new TerminalError(
				"DPX-TUI-JOURNAL-DESTINATION-EXISTS",
				"The receipt destination already exists."),
			ProjectCopyExportException copyException =>
				ProjectCopyTerminalErrorMapper.Map(copyException, localization),
			IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException =>
				new TerminalError(
					"DPX-TUI-JOURNAL-UNAVAILABLE",
					"The agent journal is unavailable."),
			_ => new TerminalError(
				"DPX-TUI-OPERATION-FAILED",
				localization["Terminal.Tui.Error.OperationFailed"])
		};

	private async Task ShowAgentJournalAsync(
		string projectRoot,
		string? selector,
		CancellationToken cancellationToken)
	{
		var projectSessions = await _agentJournalStore.Value
			.ListSessionsAsync(projectRoot, cancellationToken: cancellationToken)
			.ConfigureAwait(false);
		if (SelectAgentJournalSessions(projectSessions, selector) is not { } sessions)
		{
			await ShowAgentJournalErrorAsync(
				"DPX-TUI-JOURNAL-NOT-FOUND",
				"No matching journal session was found.").ConfigureAwait(false);
			return;
		}

		var receipts = new Dictionary<string, AgentJournalReceipt>(StringComparer.Ordinal);
		foreach (var session in sessions)
		{
			var receipt = await _agentJournalStore.Value
				.ReadReceiptAsync(session.Id, cancellationToken)
				.ConfigureAwait(false);
			if (receipt is not null)
				receipts.Add(session.Id, receipt);
		}

		await InvokeAsync(() =>
		{
			ShowAgentJournalOverlay(sessions, receipts);
			return true;
		}).ConfigureAwait(false);
	}

	// Returns null when an explicit session id matches nothing in a non-empty journal, so an
	// id typo is not reported as an empty journal.
	internal static IReadOnlyList<AgentJournalSession>? SelectAgentJournalSessions(
		IReadOnlyList<AgentJournalSession> sessions,
		string? selector)
	{
		if (string.IsNullOrWhiteSpace(selector) || selector == "last" || sessions.Count == 0)
			return sessions;
		var selected = sessions
			.Where(session => string.Equals(session.Id, selector, StringComparison.Ordinal))
			.ToArray();
		return selected.Length == 0 ? null : selected;
	}

	private async Task ExportAgentJournalAsync(
		string projectRoot,
		TerminalWorkspaceCommand command,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(command.Destination))
			throw new ArgumentException("A receipt destination is required.", nameof(command));
		var session = await ResolveAgentJournalSessionAsync(
			projectRoot,
			command.Target,
			cancellationToken).ConfigureAwait(false);
		if (session is null)
		{
			await ShowAgentJournalErrorAsync(
				"DPX-TUI-JOURNAL-NOT-FOUND",
				"No matching journal session was found.").ConfigureAwait(false);
			return;
		}

		var receipt = await _agentJournalStore.Value
			.ReadReceiptAsync(session.Id, cancellationToken)
			.ConfigureAwait(false);
		if (receipt is null)
		{
			await ShowAgentJournalErrorAsync(
				"DPX-TUI-JOURNAL-NOT-FOUND",
				"No matching journal session was found.").ConfigureAwait(false);
			return;
		}
		var content = command.Format == ProjectContextDocumentFormat.Json
			? _agentJournalReceiptFormatter.FormatJson(receipt)
			: _agentJournalReceiptFormatter.FormatMarkdown(receipt);
		var requestedDestination = ResolveAgentJournalExportDestination(
			projectRoot,
			command.Destination);
		var destination = await AtomicOutputWriter.WriteTextAsync(
			requestedDestination,
			content,
			overwrite: false,
			cancellationToken,
			path => ExactOutputDestinationValidator.ValidateContext(
				projectRoot,
				path,
				overwrite: false)).ConfigureAwait(false);

		await InvokeAsync(() =>
		{
			ShowTransientStatus(
				FormatStatusPath("Agent journal exported: {0}", destination, _terminalWidth - 2),
				TerminalWorkspaceTheme.Success);
			return true;
		}).ConfigureAwait(false);
	}

	internal static string ResolveAgentJournalExportDestination(
		string projectRoot,
		string destination) =>
		TerminalWorkspacePathResolver.Resolve(destination, projectRoot);

	private async ValueTask<AgentJournalSession?> ResolveAgentJournalSessionAsync(
		string projectRoot,
		string? selector,
		CancellationToken cancellationToken)
	{
		var sessions = await _agentJournalStore.Value
			.ListSessionsAsync(projectRoot, cancellationToken: cancellationToken)
			.ConfigureAwait(false);
		return string.IsNullOrWhiteSpace(selector) || selector == "last"
			? sessions.FirstOrDefault()
			: sessions.FirstOrDefault(session => string.Equals(
				session.Id,
				selector,
				StringComparison.Ordinal));
	}

	private void ShowAgentJournalOverlay(
		IReadOnlyList<AgentJournalSession> sessions,
		IReadOnlyDictionary<string, AgentJournalReceipt> receipts)
	{
		if (sessions.Count == 0)
		{
			ShowScrollableOverlay(
				AgentJournalTitle,
				AgentJournalText(
					"AgentJournal.Empty.Tui",
					"The journal is empty. Connect an agent: :mcp connect codex or :mcp connect claude-code"),
				TerminalWorkspaceTheme.Dialog,
				preferredWidth: 72,
				preferredHeight: 10);
			return;
		}

		var width = ResolveDialogWidth(118);
		var height = Math.Clamp(_terminalHeight - 4, 18, Math.Max(18, _terminalHeight - 2));
		using var dialog = CreateDialog(AgentJournalTitle, width, height);
		var rows = new ObservableCollection<TerminalAgentJournalSessionRow>(
			sessions.Select(session => new TerminalAgentJournalSessionRow(session, AgentJournalText)));
		var list = new ListView
		{
			X = 1,
			Y = 1,
			Width = Dim.Fill(1),
			Height = Math.Clamp(rows.Count, 3, 8),
			SchemeName = TerminalWorkspaceTheme.List
		};
		var sessionHeader = new Label
		{
			X = 1,
			Y = 0,
			Width = Dim.Fill(1),
			Height = 1,
			Text = TerminalAgentJournalPresentation.BuildSessionHeader(AgentJournalText),
			SchemeName = TerminalWorkspaceTheme.Base
		};
		list.SetSource(rows);
		list.SelectedItem = 0;
		var details = new TextView
		{
			X = 1,
			Y = Pos.Bottom(list) + 1,
			Width = Dim.Fill(1),
			Height = Dim.Fill(1),
			ReadOnly = true,
			WordWrap = false,
			ScrollBars = true,
			SchemeName = TerminalWorkspaceTheme.Base
		};

		void UpdateDetails()
		{
			var index = Math.Clamp(list.SelectedItem ?? 0, 0, sessions.Count - 1);
			var session = sessions[index];
			details.Text = receipts.TryGetValue(session.Id, out var receipt)
				? TerminalAgentJournalPresentation.BuildCallDetails(session, receipt.Calls, AgentJournalText)
				: TerminalAgentJournalPresentation.BuildCallDetails(session, [], AgentJournalText);
			details.SetNeedsDraw();
		}

		list.ValueChanged += (_, _) => UpdateDetails();
		// Choosing a session only shows its calls; Enter or a double-click must not close the journal.
		list.Accepting += static (_, args) => args.Handled = true;
		dialog.Add(sessionHeader, list, details);
		dialog.AddButton(CreateDialogButton(L("Terminal.Tui.Close")));
		UpdateDetails();
		RunOverlay(dialog, list);
	}

	private string AgentJournalText(string key, string fallback)
	{
		var value = L(key);
		return string.Equals(value, $"[[{key}]]", StringComparison.Ordinal)
			? fallback
			: value;
	}

	private async Task ShowAgentJournalErrorAsync(string code, string message)
	{
		if (_stopping)
			return;
		await InvokeAsync(() =>
		{
			ShowError(code, message);
			return true;
		}).ConfigureAwait(false);
	}
}

#pragma warning restore CS0618
