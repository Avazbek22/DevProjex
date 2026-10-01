namespace DevProjex.Infrastructure.AgentJournal;

public interface IAgentJournalActivityReader
{
	ValueTask<AgentJournalActivitySnapshot?> ReadActivityAsync(
		string sessionId,
		long afterSequence,
		CancellationToken cancellationToken = default);
}

public sealed record AgentJournalActivitySnapshot(
	AgentJournalSession Session,
	AgentJournalCall? LatestCall,
	IReadOnlyList<AgentJournalCall> AppendedCalls,
	bool RequiresReset,
	DateTimeOffset? LastEventUtc);
