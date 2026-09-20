using System.Text.Json.Serialization;

namespace DevProjex.Infrastructure.AgentJournal;

[JsonSourceGenerationOptions(
	JsonSerializerDefaults.Web,
	UseStringEnumConverter = true)]
[JsonSerializable(typeof(AgentJournalLine))]
[JsonSerializable(typeof(AgentJournalReceiptDocument))]
internal sealed partial class AgentJournalJsonSerializerContext : JsonSerializerContext;

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
