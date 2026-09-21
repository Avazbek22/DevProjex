using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DevProjex.Infrastructure.AgentJournal;

public sealed class AgentJournalReceiptFormatter : IAgentJournalReceiptFormatter
{
	public const string JsonSchema = "devprojex-agent-journal";
	public const int JsonSchemaVersion = 1;

	public string FormatMarkdown(AgentJournalReceipt receipt)
	{
		ArgumentNullException.ThrowIfNull(receipt);
		var output = new StringBuilder()
			.Append("# DevProjex agent journal ").AppendLine(EscapeMarkdown(receipt.Session.Id))
			.AppendLine()
			.Append("- Started: ").AppendLine(receipt.Session.StartedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))
			.Append("- Ended: ").AppendLine(receipt.Session.EndedUtc?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? "running")
			.Append("- Client: ").Append(EscapeMarkdown(receipt.Session.ClientName));
		if (!string.IsNullOrWhiteSpace(receipt.Session.ClientVersion))
			output.Append(' ').Append(EscapeMarkdown(receipt.Session.ClientVersion));
		var lostEvents = LostEventCount(receipt.Calls);
		output.AppendLine()
			.Append("- Mode: ").AppendLine(receipt.Session.Mode.ToString())
			.Append("- Tool set: ").AppendLine(receipt.Session.ToolSet.ToString());
		if (lostEvents > 0)
		{
			output.AppendLine()
				.Append("**History is incomplete: ").Append(lostEvents)
				.Append(lostEvents == 1 ? " event" : " events")
				.AppendLine(" could not be recorded.**");
		}
		output
			.AppendLine()
			.AppendLine("## Totals")
			.AppendLine()
			.AppendLine("| Calls | Characters | Estimated tokens | Files | Secrets masked | Private data masked | Errors |")
			.AppendLine("|---:|---:|---:|---:|---:|---:|---:|")
			.Append('|').Append(receipt.Totals.Calls)
			.Append('|').Append(receipt.Totals.ResultCharacters)
			.Append('|').Append(receipt.Totals.EstimatedTokens)
			.Append('|').Append(receipt.Totals.FilesDelivered)
			.Append('|').Append(receipt.Totals.SecretsMasked)
			.Append('|').Append(receipt.Totals.PrivateDataMasked)
			.Append('|').Append(receipt.Totals.Errors).AppendLine("|")
			.AppendLine()
			.AppendLine("## Delivered paths")
			.AppendLine()
			.AppendLine("| Path | Calls |")
			.AppendLine("|---|---:|");
		foreach (var path in receipt.DeliveredPaths)
			output.Append('|').Append(EscapeMarkdown(path.Path)).Append('|').Append(path.Calls).AppendLine("|");
		output.AppendLine()
			.AppendLine("## Calls")
			.AppendLine()
			.AppendLine("| # | UTC | Tool | Duration ms | Characters | Tokens | Files | Error |")
			.AppendLine("|---:|---|---|---:|---:|---:|---:|---|");
		foreach (var call in receipt.Calls)
		{
			output.Append('|').Append(call.Sequence)
				.Append('|').Append(call.Utc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))
				.Append('|').Append(EscapeMarkdown(call.Tool))
				.Append('|').Append(call.DurationMs)
				.Append('|').Append(call.ResultCharacters)
				.Append('|').Append(call.EstimatedTokens)
				.Append('|').Append(call.FilesDelivered)
				.Append('|').Append(EscapeMarkdown(call.ErrorCode ?? string.Empty)).AppendLine("|");
		}
		return output.ToString();
	}

	public string FormatJson(AgentJournalReceipt receipt)
	{
		ArgumentNullException.ThrowIfNull(receipt);
		var document = new AgentJournalReceiptDocument(JsonSchema, JsonSchemaVersion, receipt);
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
		{
			Indented = true,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		}))
		{
			JsonSerializer.Serialize(
				writer,
				document,
				AgentJournalJsonSerializerContext.Default.AgentJournalReceiptDocument);
		}
		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private static string EscapeMarkdown(string value) =>
		value.Replace("\\", "\\\\", StringComparison.Ordinal)
			.Replace("|", "\\|", StringComparison.Ordinal)
			.Replace("\r", " ", StringComparison.Ordinal)
			.Replace("\n", " ", StringComparison.Ordinal);

	internal static long LostEventCount(IEnumerable<AgentJournalCall> calls) => calls
		.Where(static call => call.Notices.Contains("history-incomplete", StringComparer.Ordinal))
		.Select(static call => call.Arguments.TryGetValue("lost_events", out var value) &&
			long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
				? Math.Max(0, parsed)
				: 0)
		.Sum();
}
