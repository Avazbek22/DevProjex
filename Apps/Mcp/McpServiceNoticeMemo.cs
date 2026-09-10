namespace DevProjex.Mcp;

/// <summary>
/// The trusted filter and protection lines describe server state, not the call. Repeating them
/// on every response is a fixed tax that grows with exactly the workload the server is good at:
/// many small, precise reads. This memo keeps one delivery per session per notice content, and
/// replaces the repeat with a constant pointer back to <c>list_projects</c>.
/// </summary>
/// <remarks>
/// Omission must be provable. When the project identity cannot be determined, or either line
/// says something that was never delivered for that identity, the full set goes out again.
/// </remarks>
internal sealed class McpServiceNoticeMemo
{
	/// <summary>
	/// Deliberately shorter than the shortest set it can replace, so a response never grows by
	/// omitting a notice. Its meaning is spelled out once in the server instructions.
	/// </summary>
	public const string ContinuationNotice = "[Unchanged] filters, protection; see list_projects.";

	private readonly Lock gate = new();
	private string? deliveredIdentity;
	private string? deliveredFilters;
	private string? deliveredProtection;

	/// <summary>
	/// Returns the filter and protection lines this response should carry, plus the continuation
	/// line when either was withheld.
	/// </summary>
	/// <param name="identity">The project this response describes, or <see langword="null"/> when it is unknown.</param>
	/// <param name="filters">The effective-filters line this response would carry, if any.</param>
	/// <param name="protection">The protection line this response would carry, if any.</param>
	/// <param name="alwaysSend">Set when the response has to explain itself regardless of history.</param>
	public McpServiceNotices Next(
		string? identity,
		string? filters,
		string? protection,
		bool alwaysSend)
	{
		if (filters is null && protection is null)
			return new McpServiceNotices(null, null, null);

		lock (gate)
		{
			var sameProject = identity is not null &&
				string.Equals(identity, deliveredIdentity, StringComparison.Ordinal);
			var alreadyDelivered = sameProject &&
				IsDelivered(filters, deliveredFilters) &&
				IsDelivered(protection, deliveredProtection);
			if (alwaysSend || !alreadyDelivered)
			{
				Remember(identity, filters, protection, sameProject);
				return new McpServiceNotices(filters, protection, null);
			}
		}

		return new McpServiceNotices(null, null, ContinuationNotice);
	}

	private static bool IsDelivered(string? current, string? delivered) =>
		current is null || string.Equals(current, delivered, StringComparison.Ordinal);

	private void Remember(string? identity, string? filters, string? protection, bool sameProject)
	{
		if (identity is null)
		{
			// Nothing provable was learned, so the next response starts from scratch.
			deliveredIdentity = null;
			deliveredFilters = null;
			deliveredProtection = null;
			return;
		}

		if (!sameProject)
		{
			deliveredFilters = null;
			deliveredProtection = null;
		}

		deliveredIdentity = identity;
		deliveredFilters = filters ?? deliveredFilters;
		deliveredProtection = protection ?? deliveredProtection;
	}
}

internal readonly record struct McpServiceNotices(
	string? Filters,
	string? Protection,
	string? Continuation);
