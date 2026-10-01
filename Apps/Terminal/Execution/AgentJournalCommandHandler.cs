using System.Globalization;
using System.Text.Json;
using DevProjex.Infrastructure.AgentJournal;
using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Execution;

internal enum AgentJournalOutputFormat
{
	Text = 0,
	Json = 1,
	Markdown = 2
}

internal sealed class AgentJournalCommandHandler(
	IAgentJournalReader reader,
	IAgentJournalReceiptFormatter formatter,
	ITerminalEnvironment environment,
	LocalizationService localization,
	TerminalOutputOptions outputOptions,
	TimeProvider? timeProvider = null)
{
	private static readonly JsonSerializerOptions JsonOptions =
		AgentJournalJsonSerialization.CreateOptions(writeIndented: true);
	private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

	public async Task<int> RunAsync(
		string? projectRoot,
		string? sessionId,
		bool last,
		AgentJournalOutputFormat format,
		string? outputPath,
		bool clear,
		CancellationToken cancellationToken)
	{
		var normalizedRoot = NormalizeOptionalProjectRoot(projectRoot);
		var toFile = !string.IsNullOrWhiteSpace(outputPath);
		if (clear)
		{
			// Sessions that also served other projects are kept when one project is cleared.
			var sharedSessions = normalizedRoot is null
				? 0
				: (await reader.ListSessionsAsync(normalizedRoot, int.MaxValue, cancellationToken).ConfigureAwait(false))
					.Count(static session => !session.IsLive && session.Roots.Count > 1);
			var removed = await reader.ClearAsync(normalizedRoot, cancellationToken).ConfigureAwait(false);
			var message = localization.Format("Terminal.McpLog.Cleared", removed);
			if (sharedSessions > 0)
				message += " " + localization.Format("Terminal.McpLog.SharedKept", sharedSessions);
			return await WriteAsync(message + Environment.NewLine, outputPath, cancellationToken).ConfigureAwait(false);
		}

		if (sessionId is not null || last)
		{
			var resolvedId = sessionId ?? (await reader
				.ListSessionsAsync(normalizedRoot, limit: 1, cancellationToken)
				.ConfigureAwait(false))
				.FirstOrDefault()?.Id;
			if (resolvedId is null)
				return WriteNotFound(localization["AgentJournal.SessionNotFound"]);
			var receipt = await reader.ReadReceiptAsync(resolvedId, cancellationToken).ConfigureAwait(false);
			if (receipt is null || normalizedRoot is not null && !ContainsRoot(receipt.Session, normalizedRoot))
				return WriteNotFound(localization.Format("Terminal.McpLog.SessionNotFound", resolvedId));
			var content = format switch
			{
				AgentJournalOutputFormat.Text => FormatCalls(receipt, toFile),
				AgentJournalOutputFormat.Json => formatter.FormatJson(receipt) + Environment.NewLine,
				AgentJournalOutputFormat.Markdown => formatter.FormatMarkdown(receipt),
				_ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
			};
			return await WriteAsync(content, outputPath, cancellationToken).ConfigureAwait(false);
		}

		if (format == AgentJournalOutputFormat.Markdown)
		{
			return WriteUsageError(
				"DPX-CLI-JOURNAL-SESSION-REQUIRED",
				localization["Terminal.Validation.McpLogMarkdownRequiresSession"]);
		}
		var sessions = await reader.ListSessionsAsync(normalizedRoot, cancellationToken: cancellationToken)
			.ConfigureAwait(false);
		var listing = format == AgentJournalOutputFormat.Json
			? FormatSessionsJson(sessions)
			: FormatSessions(sessions, toFile);
		return await WriteAsync(listing, outputPath, cancellationToken).ConfigureAwait(false);
	}

	private async Task<int> WriteAsync(
		string content,
		string? outputPath,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(outputPath))
		{
			environment.Output.Write(content);
			return CommandLineExitCodes.Success;
		}
		try
		{
			var writtenPath = await AtomicOutputWriter.WriteTextAsync(
				outputPath,
				content,
				overwrite: false,
				cancellationToken).ConfigureAwait(false);
			TerminalTextEscaping.WriteSingleLine(environment.Output, writtenPath);
			return CommandLineExitCodes.Success;
		}
		catch (OutputDestinationConflictException)
		{
			WriteError(
				"DPX-CLI-OUTPUT-EXISTS",
				localization.Format("Terminal.McpLog.OutputExists", outputPath));
			return CommandLineExitCodes.DestinationConflict;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return WriteRuntimeError("DPX-CLI-JOURNAL-WRITE-FAILED", localization["Terminal.McpLog.WriteFailed"]);
		}
	}

	private string FormatSessions(IReadOnlyList<AgentJournalSession> sessions, bool toFile)
	{
		if (sessions.Count == 0)
			return localization["Terminal.McpLog.Empty"] + Environment.NewLine;
		var rows = sessions.Select(session => new[]
		{
			TerminalTextEscaping.EscapeSingleLine(session.Id),
			session.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
			TerminalTextEscaping.EscapeSingleLine(DisplayClient(session)),
			session.Mode.ToString().ToLowerInvariant(),
			TerminalTextEscaping.EscapeSingleLine(string.Join(", ", session.Roots.Select(static root => root.Name))),
			session.Totals.Calls.ToString(CultureInfo.InvariantCulture),
			session.Totals.ResultCharacters.ToString(CultureInfo.InvariantCulture),
			session.Totals.EstimatedTokens.ToString(CultureInfo.InvariantCulture),
			session.Totals.FilesDelivered.ToString(CultureInfo.InvariantCulture),
			(session.Totals.SecretsMasked + session.Totals.PrivateDataMasked).ToString(CultureInfo.InvariantCulture),
			FormatDuration(session),
			localization[session.IsLive ? "Terminal.Value.Yes" : "Terminal.Value.No"]
		}).ToArray();
		string[] headers =
		[
			localization["AgentJournal.Column.Session"],
			localization["AgentJournal.Column.Time"],
			localization["AgentJournal.Column.Client"],
			localization["AgentJournal.Column.Mode"],
			localization["AgentJournal.Column.Project"],
			localization["AgentJournal.Column.Calls"],
			localization["AgentJournal.Column.Characters"],
			localization["AgentJournal.Column.Tokens"],
			localization["AgentJournal.Column.Files"],
			localization["AgentJournal.Column.Masked"],
			localization["AgentJournal.Column.Duration"],
			localization["AgentJournal.Live"]
		];
		return FormatTable(rows, headers, toFile, truncationColumn: 4);
	}

	private string FormatCalls(AgentJournalReceipt receipt, bool toFile)
	{
		var lostEvents = LostEventCount(receipt.Calls);
		var rows = receipt.Calls.Select(call => new[]
		{
			call.Sequence.ToString(CultureInfo.InvariantCulture),
			call.Utc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
			TerminalTextEscaping.EscapeSingleLine(call.Tool),
			call.RootIndex?.ToString(CultureInfo.InvariantCulture) ?? "-",
			call.Revision?.ToString(CultureInfo.InvariantCulture) ?? "-",
			call.DurationMs.ToString(CultureInfo.InvariantCulture),
			call.ResultCharacters.ToString(CultureInfo.InvariantCulture),
			call.EstimatedTokens.ToString(CultureInfo.InvariantCulture),
			call.FilesDelivered.ToString(CultureInfo.InvariantCulture),
			(call.SecretsMasked + call.PrivateDataMasked).ToString(CultureInfo.InvariantCulture),
			TerminalTextEscaping.EscapeSingleLine(string.Join(',', call.Notices)),
			call.ErrorCode is null ? "-" : TerminalTextEscaping.EscapeSingleLine(call.ErrorCode)
		}).ToArray();
		string[] headers =
		[
			localization["AgentJournal.Column.Number"],
			"UTC",
			localization["AgentJournal.Column.Tool"],
			localization["AgentJournal.Column.Project"],
			localization["AgentJournal.Column.Revision"],
			localization["AgentJournal.Column.Duration"],
			localization["AgentJournal.Column.Characters"],
			localization["AgentJournal.Column.Tokens"],
			localization["AgentJournal.Column.Files"],
			localization["AgentJournal.Column.Masked"],
			localization["AgentJournal.Column.Notices"],
			localization["AgentJournal.Column.Error"]
		];
		var table = FormatTable(rows, headers, toFile, truncationColumn: 10);
		return lostEvents > 0
			? localization.Format("AgentJournal.Notice.HistoryIncomplete", lostEvents) + Environment.NewLine + table
			: table;
	}

	// Tables follow the shared rule: headers and width fitting only on an interactive
	// stdout; pipes, redirects, and --output files keep the headerless untruncated shape.
	private string FormatTable(
		IReadOnlyList<string[]> rows,
		IReadOnlyList<string> headers,
		bool toFile,
		int truncationColumn)
	{
		var lines = toFile
			? TerminalColumnLayout.Format(rows)
			: TerminalColumnLayout.FormatForOutput(rows, headers, environment, outputOptions, truncationColumn);
		return lines.Count == 0
			? string.Empty
			: string.Join(Environment.NewLine, lines) + Environment.NewLine;
	}

	private static long LostEventCount(IEnumerable<AgentJournalCall> calls) => calls
		.Where(static call => call.Notices.Contains("history-incomplete", StringComparer.Ordinal))
		.Select(static call => call.Arguments.TryGetValue("lost_events", out var value) &&
			long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
				? Math.Max(0, parsed)
				: 0)
		.Sum();

	private static string FormatSessionsJson(IReadOnlyList<AgentJournalSession> sessions) =>
		JsonSerializer.Serialize(
			new
			{
				schema = AgentJournalReceiptFormatter.JsonSchema,
				version = AgentJournalReceiptFormatter.JsonSchemaVersion,
				sessions
			},
			JsonOptions) + Environment.NewLine;

	private string FormatDuration(AgentJournalSession session)
	{
		if (session.EndedUtc is null && !session.IsLive)
		{
			if (!AgentJournalSessionHistory.TryGet(session, out var history) || history.LastEventUtc is null)
				return "unknown";
			return "≥" + FormatDuration(session.StartedUtc, history.LastEventUtc.Value);
		}
		var end = session.EndedUtc ?? clock.GetUtcNow();
		return FormatDuration(session.StartedUtc, end);
	}

	private static string FormatDuration(DateTimeOffset started, DateTimeOffset end)
	{
		var duration = end > started ? end - started : TimeSpan.Zero;
		return duration.TotalHours >= 1
			? duration.ToString("h\\:mm\\:ss", CultureInfo.InvariantCulture)
			: duration.ToString("m\\:ss", CultureInfo.InvariantCulture);
	}

	private static string DisplayClient(AgentJournalSession session) =>
		string.IsNullOrWhiteSpace(session.ClientVersion)
			? session.ClientName
			: session.ClientName + " " + session.ClientVersion;

	private static string? NormalizeOptionalProjectRoot(string? projectRoot)
	{
		if (string.IsNullOrWhiteSpace(projectRoot))
			return null;
		return Path.GetFullPath(projectRoot);
	}

	private static bool ContainsRoot(AgentJournalSession session, string root) =>
		session.Roots.Any(item => PathComparer.Default.Equals(PathUtility.Normalize(item.ConfiguredPath), PathUtility.Normalize(root)));

	private int WriteNotFound(string message) =>
		WriteRuntimeError("DPX-CLI-JOURNAL-NOT-FOUND", message);

	private int WriteUsageError(string code, string message)
	{
		WriteError(code, message);
		return CommandLineExitCodes.UsageError;
	}

	private int WriteRuntimeError(string code, string message)
	{
		WriteError(code, message);
		return CommandLineExitCodes.RuntimeError;
	}

	private void WriteError(string code, string message) =>
		environment.Error.WriteLine(
			$"{TerminalErrorHeader.Format(localization, code)} {TerminalTextEscaping.EscapeSingleLine(message)}");
}
