using DevProjex.Kernel.Models;

namespace DevProjex.Kernel.Abstractions;

public interface IAgentJournalWriter
{
	ValueTask StartSession(
		AgentJournalSession session,
		CancellationToken cancellationToken = default);

	ValueTask RecordCall(
		string sessionId,
		AgentJournalCall call,
		CancellationToken cancellationToken = default);

	ValueTask EndSession(
		string sessionId,
		DateTimeOffset endedUtc,
		AgentJournalTotals totals,
		CancellationToken cancellationToken = default);
}

public interface IAgentJournalReader
{
	AgentJournalRetentionPolicy Retention { get; }

	ValueTask<IReadOnlyList<AgentJournalSession>> ListSessionsAsync(
		string? projectRoot = null,
		int limit = 200,
		CancellationToken cancellationToken = default);

	ValueTask<IReadOnlyList<AgentJournalCall>> ReadCallsAsync(
		string sessionId,
		CancellationToken cancellationToken = default);

	ValueTask<AgentJournalReceipt?> ReadReceiptAsync(
		string sessionId,
		CancellationToken cancellationToken = default);

	IAsyncEnumerable<AgentJournalChange> WatchChangesAsync(
		string sessionId,
		CancellationToken cancellationToken = default);

	ValueTask<int> ClearAsync(
		string? projectRoot = null,
		CancellationToken cancellationToken = default);
}

public interface IAgentJournalReceiptFormatter
{
	string FormatMarkdown(AgentJournalReceipt receipt);

	string FormatJson(AgentJournalReceipt receipt);
}
