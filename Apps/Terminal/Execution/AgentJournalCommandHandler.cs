using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
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
	TimeProvider? timeProvider = null)
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
	};
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
		if (clear)
		{
			var removed = await reader.ClearAsync(normalizedRoot, cancellationToken).ConfigureAwait(false);
			return await WriteAsync(
				$"Cleared {removed.ToString(CultureInfo.InvariantCulture)} agent journal session(s).{Environment.NewLine}",
				outputPath,
				cancellationToken).ConfigureAwait(false);
		}

		if (sessionId is not null || last)
		{
			var resolvedId = sessionId ?? (await reader
				.ListSessionsAsync(normalizedRoot, limit: 1, cancellationToken)
				.ConfigureAwait(false))
				.FirstOrDefault()?.Id;
			if (resolvedId is null)
				return WriteNotFound("No matching journal session was found.");
			var receipt = await reader.ReadReceiptAsync(resolvedId, cancellationToken).ConfigureAwait(false);
			if (receipt is null || normalizedRoot is not null && !ContainsRoot(receipt.Session, normalizedRoot))
				return WriteNotFound($"Journal session '{resolvedId}' was not found for this project.");
			var content = format switch
			{
				AgentJournalOutputFormat.Text => FormatCalls(receipt),
				AgentJournalOutputFormat.Json => formatter.FormatJson(receipt) + Environment.NewLine,
				AgentJournalOutputFormat.Markdown => formatter.FormatMarkdown(receipt),
				_ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
			};
			return await WriteAsync(content, outputPath, cancellationToken).ConfigureAwait(false);
		}

		if (format == AgentJournalOutputFormat.Markdown)
			return WriteUsageError("DPX-CLI-JOURNAL-SESSION-REQUIRED", "markdown output requires --session or --last.");
		var sessions = await reader.ListSessionsAsync(normalizedRoot, cancellationToken: cancellationToken)
			.ConfigureAwait(false);
		var listing = format == AgentJournalOutputFormat.Json
			? FormatSessionsJson(sessions)
			: FormatSessions(sessions);
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
			await AtomicOutputWriter.WriteTextAsync(
				outputPath,
				content,
				overwrite: false,
				cancellationToken).ConfigureAwait(false);
			return CommandLineExitCodes.Success;
		}
		catch (OutputDestinationConflictException)
		{
			WriteError(
				"DPX-CLI-OUTPUT-EXISTS",
				$"output file already exists: {TerminalTextEscaping.EscapeSingleLine(outputPath)}");
			return CommandLineExitCodes.DestinationConflict;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return WriteRuntimeError("DPX-CLI-JOURNAL-WRITE-FAILED", exception.Message);
		}
	}

	private string FormatSessions(IReadOnlyList<AgentJournalSession> sessions)
	{
		if (sessions.Count == 0)
			return "No agent journal sessions found." + Environment.NewLine;
		var rows = new List<string[]>
		{
			new[] { "Session", "Started", "Client", "Mode", "Project", "Calls", "Characters", "Tokens", "Files", "Masked", "Duration", "Live" }
		};
		rows.AddRange(sessions.Select(session => new[]
		{
			session.Id,
			session.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
			DisplayClient(session),
			session.Mode.ToString().ToLowerInvariant(),
			string.Join(", ", session.Roots.Select(static root => root.Name)),
			session.Totals.Calls.ToString(CultureInfo.InvariantCulture),
			session.Totals.ResultCharacters.ToString(CultureInfo.InvariantCulture),
			session.Totals.EstimatedTokens.ToString(CultureInfo.InvariantCulture),
			session.Totals.FilesDelivered.ToString(CultureInfo.InvariantCulture),
			(session.Totals.SecretsMasked + session.Totals.PrivateDataMasked).ToString(CultureInfo.InvariantCulture),
			FormatDuration(session),
			session.IsLive ? "yes" : "no"
		}));
		return string.Join(Environment.NewLine, TerminalColumnLayout.Format(rows)) + Environment.NewLine;
	}

	private static string FormatCalls(AgentJournalReceipt receipt)
	{
		var rows = new List<string[]>
		{
			new[] { "#", "UTC", "Tool", "Root", "Revision", "Duration ms", "Characters", "Tokens", "Files", "Masked", "Notices", "Error" }
		};
		rows.AddRange(receipt.Calls.Select(call => new[]
		{
			call.Sequence.ToString(CultureInfo.InvariantCulture),
			call.Utc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
			call.Tool,
			call.RootIndex?.ToString(CultureInfo.InvariantCulture) ?? "-",
			call.Revision?.ToString(CultureInfo.InvariantCulture) ?? "-",
			call.DurationMs.ToString(CultureInfo.InvariantCulture),
			call.ResultCharacters.ToString(CultureInfo.InvariantCulture),
			call.EstimatedTokens.ToString(CultureInfo.InvariantCulture),
			call.FilesDelivered.ToString(CultureInfo.InvariantCulture),
			(call.SecretsMasked + call.PrivateDataMasked).ToString(CultureInfo.InvariantCulture),
			string.Join(',', call.Notices),
			call.ErrorCode ?? "-"
		}));
		return string.Join(Environment.NewLine, TerminalColumnLayout.Format(rows)) + Environment.NewLine;
	}

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
		var end = session.EndedUtc ?? clock.GetUtcNow();
		var duration = end > session.StartedUtc ? end - session.StartedUtc : TimeSpan.Zero;
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
		environment.Error.WriteLine($"error[{code}]: {TerminalTextEscaping.EscapeSingleLine(message)}");
}
