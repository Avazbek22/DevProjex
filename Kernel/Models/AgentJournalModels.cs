namespace DevProjex.Kernel.Models;

public enum AgentJournalMode
{
	Standard = 0,
	Live = 1
}

public enum AgentJournalToolSet
{
	Full = 0,
	Reduced = 1
}

public enum AgentJournalChangeKind
{
	CallAppended = 0,
	SessionEnded = 1
}

public static class AgentJournalNoticeCodes
{
	public const string OutsideSelection = "outside-selection";
	public const string StalePack = "stale-pack";
	public const string SearchPartial = "search-partial";
	public const string MatchesOmitted = "matches-omitted";
	public const string BudgetSkipped = "budget-skipped";
	public const string MissingPath = "missing-path";
	public const string Unavailable = "unavailable";
}

public sealed record AgentJournalRoot(
	string ConfiguredPath,
	string Name);

public sealed record AgentJournalTotals(
	long Calls,
	long ResultCharacters,
	long EstimatedTokens,
	long FilesDelivered,
	long SecretsMasked,
	long PrivateDataMasked,
	long Errors)
{
	public static AgentJournalTotals Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
}

public sealed record AgentJournalSession(
	string Id,
	DateTimeOffset StartedUtc,
	DateTimeOffset? EndedUtc,
	int Pid,
	DateTimeOffset ProcessStartUtc,
	string ClientName,
	string ClientVersion,
	AgentJournalMode Mode,
	IReadOnlyList<AgentJournalRoot> Roots,
	AgentJournalToolSet ToolSet,
	string ServerVersion,
	bool HidePrivateData,
	AgentJournalTotals Totals,
	bool IsLive);

public sealed record AgentJournalCall(
	long Sequence,
	DateTimeOffset Utc,
	string Tool,
	int? RootIndex,
	IReadOnlyDictionary<string, string> Arguments,
	int? Revision,
	long DurationMs,
	long ResultCharacters,
	long EstimatedTokens,
	int FilesDelivered,
	IReadOnlyList<string> DeliveredPaths,
	int AdditionalDeliveredPaths,
	long SecretsMasked,
	long PrivateDataMasked,
	IReadOnlyList<string> Notices,
	string? ErrorCode);

public sealed record AgentJournalDeliveredPath(
	string Path,
	long Calls);

public sealed record AgentJournalReceipt(
	AgentJournalSession Session,
	AgentJournalTotals Totals,
	IReadOnlyList<AgentJournalDeliveredPath> DeliveredPaths,
	IReadOnlyList<AgentJournalCall> Calls);

public sealed record AgentJournalChange(
	string SessionId,
	AgentJournalChangeKind Kind,
	long? Sequence = null);

public sealed record AgentJournalRetentionPolicy(
	TimeSpan MaximumAge,
	int MaximumSessions)
{
	public static AgentJournalRetentionPolicy Default { get; } = new(TimeSpan.FromDays(30), 200);
}
