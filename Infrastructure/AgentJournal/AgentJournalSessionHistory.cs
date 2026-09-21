using System.Globalization;
using System.Runtime.CompilerServices;

namespace DevProjex.Infrastructure.AgentJournal;

public sealed record AgentJournalHistoryState(
	DateTimeOffset? LastEventUtc,
	bool IsIncomplete,
	long LostEventsLowerBound,
	bool ExactLossUnknown);

public static class AgentJournalSessionHistory
{
	private static readonly ConditionalWeakTable<AgentJournalSession, AgentJournalHistoryState> States = new();

	public static bool TryGet(AgentJournalSession session, out AgentJournalHistoryState state) =>
		States.TryGetValue(session, out state!);

	internal static void Attach(AgentJournalSession session, AgentJournalHistoryState state)
	{
		States.Remove(session);
		States.Add(session, state);
	}

	internal static AgentJournalHistoryState FromCalls(IReadOnlyList<AgentJournalCall> calls)
	{
		var incomplete = calls.Where(static call =>
			call.Notices.Contains("history-incomplete", StringComparer.Ordinal)).ToArray();
		var lost = incomplete.Sum(static call =>
			call.Arguments.TryGetValue("lost_events", out var value) &&
			long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
				? Math.Max(0, parsed)
				: 0);
		return new AgentJournalHistoryState(
			calls.Count == 0 ? null : calls.Max(static call => call.Utc),
			incomplete.Length > 0,
			lost,
			incomplete.Any(static call => call.Arguments.ContainsKey("lost_events_unknown")));
	}
}
