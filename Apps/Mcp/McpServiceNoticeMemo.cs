namespace DevProjex.Mcp;

/// <summary>
/// The trusted filter and protection lines describe server state, not the call. Repeating them
/// on every response is a fixed tax that grows with exactly the workload the server is good at:
/// many small, precise reads. This memo keeps one delivery per session per notice content, and
/// replaces the repeat with a constant pointer back to <c>list_projects</c>.
/// </summary>
/// <remarks>
/// Omission must be provable, so a line counts as delivered only after it is found in the text
/// the tool actually returned. A result that stores its body in a pack, truncates its trailing
/// diagnostics, or fails outright therefore leaves the memo untouched, and the next response
/// reports the full set again.
/// </remarks>
internal sealed class McpServiceNoticeMemo
{
	/// <summary>
	/// Never longer than the shortest set it can replace, so a response cannot grow by omitting
	/// a notice. Its meaning is spelled out once in the server instructions.
	/// </summary>
	public const string ContinuationNotice = "[Unchanged] filters, protection; see list_projects.";

	private readonly Lock gate = new();
	private string? deliveredIdentity;
	private string? deliveredFilters;
	private string? deliveredProtection;
	private string? pendingIdentity;
	private string? pendingFilters;
	private string? pendingProtection;

	/// <summary>
	/// Returns the filter and protection lines this response should carry, plus the continuation
	/// line when either was withheld. Lines that are sent stay pending until
	/// <see cref="CommitDelivered"/> confirms they reached the caller.
	/// </summary>
	/// <param name="identity">The project this response describes, or <see langword="null"/> when it is unknown.</param>
	/// <param name="filters">The effective-filters line this response would carry, if any.</param>
	/// <param name="protection">The protection line this response would carry, if any.</param>
	/// <param name="alwaysSend">Set when the response has to explain itself regardless of history.</param>
	public McpServiceNotices Prepare(
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
			if (!alwaysSend && alreadyDelivered)
				return new McpServiceNotices(null, null, ContinuationNotice);

			pendingIdentity = identity;
			pendingFilters = filters ?? pendingFilters;
			pendingProtection = protection ?? pendingProtection;
			return new McpServiceNotices(filters, protection, null);
		}
	}

	/// <summary>
	/// Records the pending lines as delivered when the returned text contains all of them, and
	/// drops the pending record otherwise. An unknown project identity is never recorded.
	/// </summary>
	public void CommitDelivered(string responseText)
	{
		ArgumentNullException.ThrowIfNull(responseText);
		lock (gate)
		{
			var identity = pendingIdentity;
			var filters = pendingFilters;
			var protection = pendingProtection;
			ClearPending();
			if (identity is null || !Contains(responseText, filters) || !Contains(responseText, protection))
				return;

			if (!string.Equals(identity, deliveredIdentity, StringComparison.Ordinal))
			{
				deliveredFilters = null;
				deliveredProtection = null;
			}

			deliveredIdentity = identity;
			deliveredFilters = filters ?? deliveredFilters;
			deliveredProtection = protection ?? deliveredProtection;
		}
	}

	/// <summary>Drops a pending record when the call did not produce a response at all.</summary>
	public void DiscardPending()
	{
		lock (gate)
		{
			ClearPending();
		}
	}

	private void ClearPending()
	{
		pendingIdentity = null;
		pendingFilters = null;
		pendingProtection = null;
	}

	private static bool Contains(string responseText, string? notice) =>
		notice is null || responseText.Contains(notice, StringComparison.Ordinal);

	private static bool IsDelivered(string? current, string? delivered) =>
		current is null || string.Equals(current, delivered, StringComparison.Ordinal);
}

internal readonly record struct McpServiceNotices(
	string? Filters,
	string? Protection,
	string? Continuation);
