using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevProjex.Kernel.Models;

namespace DevProjex.Infrastructure.AgentJournal;

[JsonSourceGenerationOptions(
	JsonSerializerDefaults.Web,
	UseStringEnumConverter = true)]
[JsonSerializable(typeof(AgentJournalLine))]
internal sealed partial class AgentJournalJsonSerializerContext : JsonSerializerContext;

[JsonSourceGenerationOptions(
	JsonSerializerDefaults.Web,
	Converters = [typeof(AgentJournalModeJsonConverter), typeof(AgentJournalToolSetJsonConverter)])]
[JsonSerializable(typeof(AgentJournalReceiptDocument))]
internal sealed partial class AgentJournalReceiptJsonSerializerContext : JsonSerializerContext;

public static class AgentJournalJsonSerialization
{
	public static JsonSerializerOptions CreateOptions(bool writeIndented)
	{
		var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
		{
			WriteIndented = writeIndented,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		};
		options.Converters.Add(new AgentJournalModeJsonConverter());
		options.Converters.Add(new AgentJournalToolSetJsonConverter());
		return options;
	}
}

internal sealed class AgentJournalModeJsonConverter()
	: JsonStringEnumConverter<AgentJournalMode>(JsonNamingPolicy.CamelCase);

internal sealed class AgentJournalToolSetJsonConverter()
	: JsonStringEnumConverter<AgentJournalToolSet>(JsonNamingPolicy.CamelCase);

internal sealed record AgentJournalLine(
	string Type,
	AgentJournalSession? Session = null,
	AgentJournalCall? Call = null,
	AgentJournalEnd? End = null);

internal sealed record AgentJournalEnd(
	DateTimeOffset EndedUtc,
	AgentJournalTotals Totals);

internal sealed record AgentJournalReceiptDocument(
	string Schema,
	int Version,
	AgentJournalReceipt Receipt);
