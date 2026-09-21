namespace DevProjex.Mcp;

/// <summary>
/// The trusted filter and protection lines describe server state, not the call. Repeating them
/// on every response is a fixed tax that grows with exactly the workload the server is good at:
/// many small, precise reads. This memo keeps one delivery per session per notice content, and
/// replaces the repeat with a constant marker that names exactly the effective-policy lines the
/// response withheld.
/// </summary>
/// <remarks>
/// Omission must be provable, so a line counts as delivered only after it is found in the text
/// the tool actually returned. A result that stores its body in a pack, truncates its trailing
/// diagnostics, or fails outright therefore leaves the memo untouched, and the next response
/// reports the full set again.
/// </remarks>
internal sealed class McpServiceNoticeMemo
{
	private const string DeferredUntrustedDataPrefix = "\u001eDEVPROJEX-UNTRUSTED-BASE64:";
	/// <summary>
	/// Stands in for both lines on a response that would have carried both. Never longer than the
	/// shortest set it can replace, so a response cannot grow by omitting a notice. The meaning of
	/// the marker is spelled out once in the server instructions, which points to the last effective
	/// policy report for this root rather than the startup defaults.
	/// </summary>
	public const string ContinuationNotice = "[Unchanged] effective filters, protection.";

	/// <summary>Stands in for the effective-filters line alone, on a response that carries no protection line.</summary>
	public const string FiltersContinuationNotice = "[Unchanged] effective filters.";

	/// <summary>Stands in for the protection line alone, on a response that carries no effective-filters line.</summary>
	public const string ProtectionContinuationNotice = "[Unchanged] protection.";

	private readonly Lock gate = new();
	private string? deliveredIdentity;
	private string? deliveredFilters;
	private string? deliveredProtection;
	private string? pendingIdentity;
	private string? pendingFilters;
	private string? pendingFiltersIdentity;
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
			var filtersIdentity = filters;
			var filtersForResponse = MaterializeUntrustedData(filters);
			var sameProject = identity is not null &&
				string.Equals(identity, deliveredIdentity, StringComparison.Ordinal);
			var alreadyDelivered = sameProject &&
				IsDelivered(filtersIdentity, deliveredFilters) &&
				IsDelivered(protection, deliveredProtection);
			if (!alwaysSend && alreadyDelivered)
				return new McpServiceNotices(null, null, Continuation(filters, protection));

			pendingIdentity = identity;
			pendingFilters = filtersForResponse ?? pendingFilters;
			pendingFiltersIdentity = filtersIdentity ?? pendingFiltersIdentity;
			pendingProtection = protection ?? pendingProtection;
			return new McpServiceNotices(filtersForResponse, protection, null);
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
			var filtersIdentity = pendingFiltersIdentity;
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
			deliveredFilters = filtersIdentity ?? deliveredFilters;
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
		pendingFiltersIdentity = null;
		pendingProtection = null;
	}

	private static bool Contains(string responseText, string? notice) =>
		notice is null || responseText.Contains(notice, StringComparison.Ordinal);

	private static bool IsDelivered(string? current, string? delivered) =>
		current is null || string.Equals(current, delivered, StringComparison.Ordinal);

	internal static string DeferUntrustedData(string trusted, string untrusted)
	{
		ArgumentNullException.ThrowIfNull(trusted);
		ArgumentNullException.ThrowIfNull(untrusted);
		return trusted + Environment.NewLine + DeferredUntrustedDataPrefix +
			Convert.ToBase64String(Encoding.UTF8.GetBytes(untrusted));
	}

	private static string? MaterializeUntrustedData(string? notice)
	{
		if (notice is null)
			return null;
		var marker = notice.IndexOf(DeferredUntrustedDataPrefix, StringComparison.Ordinal);
		if (marker < 0)
			return notice;
		var encoded = notice[(marker + DeferredUntrustedDataPrefix.Length)..];
		var untrusted = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
		return notice[..marker] + McpSpotlight.Wrap(untrusted);
	}

	/// <summary>
	/// Names the lines this response is actually withholding. A tool that reports no protection
	/// line is not suppressing one, so naming it would credit the session with a report it never
	/// received; the same holds for the effective-filters line. A caller reaches this only once
	/// every line it does carry has already been delivered unchanged, and a response carrying
	/// neither line never gets here at all.
	/// </summary>
	private static string Continuation(string? filters, string? protection) =>
		(filters, protection) switch
		{
			(null, _) => ProtectionContinuationNotice,
			(_, null) => FiltersContinuationNotice,
			_ => ContinuationNotice
		};
}

internal readonly record struct McpServiceNotices(
	string? Filters,
	string? Protection,
	string? Continuation);
