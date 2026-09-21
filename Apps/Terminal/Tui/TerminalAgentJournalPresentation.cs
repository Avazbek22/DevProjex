using System.Globalization;
using System.Text;
using DevProjex.Kernel.Models;
using DevProjex.Infrastructure.LiveContext;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Tui;

internal sealed record TerminalAgentJournalSnapshot(
	AgentJournalSession Session,
	AgentJournalCall? LatestCall,
	long TotalCalls,
	IReadOnlyDictionary<string, long> DeliveredPathCalls)
{
	public static TerminalAgentJournalSnapshot Create(
		string projectRoot,
		AgentJournalReceipt receipt)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ArgumentNullException.ThrowIfNull(receipt);
		var paths = new Dictionary<string, long>(ProjectTreePathIdentity.CanonicalComparer);
		var rootIndex = ResolveRootIndex(projectRoot, receipt.Session.Roots);
		var matchingCalls = receipt.Calls
			.Where(call => call.RootIndex == rootIndex || receipt.Session.Roots.Count == 1 && call.RootIndex is null)
			.ToArray();
		foreach (var delivered in matchingCalls
			.SelectMany(static call => call.DeliveredPaths.Distinct(ProjectTreePathIdentity.CanonicalComparer)))
		{
			if (TerminalAgentJournalPresentation.TryResolveDeliveredPath(
					projectRoot,
					delivered,
					out var path))
			{
				paths[path] = paths.TryGetValue(path, out var count) ? count + 1 : 1;
			}
		}
		return new TerminalAgentJournalSnapshot(
			receipt.Session,
			matchingCalls.OrderBy(static call => call.Sequence).LastOrDefault(),
			receipt.Totals.Calls,
			paths);
	}

	private static int ResolveRootIndex(string projectRoot, IReadOnlyList<AgentJournalRoot> roots)
	{
		for (var index = 0; index < roots.Count; index++)
			if (PathComparer.Default.Equals(PathUtility.Normalize(projectRoot), PathUtility.Normalize(roots[index].ConfiguredPath)))
				return index;
		return -1;
	}
}

internal sealed record TerminalAgentJournalSessionRow(
	AgentJournalSession Session,
	Func<string, string, string>? Localize = null)
{
	public override string ToString()
	{
		var started = Session.StartedUtc.ToLocalTime().ToString(
			"yyyy-MM-dd HH:mm:ss",
			CultureInfo.InvariantCulture);
		var mode = Session.Mode == AgentJournalMode.Live
			? Text(Localize, "AgentJournal.Mode.Live", "live")
			: Text(Localize, "AgentJournal.Mode.Standard", "standard");
		var client = TerminalTextEscaping.EscapeSingleLine(
			string.IsNullOrWhiteSpace(Session.ClientVersion)
				? Session.ClientName
				: Session.ClientName + " " + Session.ClientVersion);
		var projects = TerminalTextEscaping.EscapeSingleLine(
			string.Join(", ", Session.Roots.Select(static root => root.Name)));
		var end = Session.EndedUtc ?? DateTimeOffset.UtcNow;
		var duration = end > Session.StartedUtc ? end - Session.StartedUtc : TimeSpan.Zero;
		var formattedDuration = duration.TotalHours >= 1
			? duration.ToString("h\\:mm\\:ss", CultureInfo.InvariantCulture)
			: duration.ToString("m\\:ss", CultureInfo.InvariantCulture);
		return $"{TerminalTextEscaping.EscapeSingleLine(Session.Id)} | {started} | {client} | {mode} | " +
			   $"{projects} | {Session.Totals.Calls.ToString(CultureInfo.InvariantCulture)} | " +
			   $"{Session.Totals.ResultCharacters.ToString(CultureInfo.InvariantCulture)} | " +
			   $"{Session.Totals.EstimatedTokens.ToString(CultureInfo.InvariantCulture)} | " +
			   $"{Session.Totals.FilesDelivered.ToString(CultureInfo.InvariantCulture)} | " +
			   $"{(Session.Totals.SecretsMasked + Session.Totals.PrivateDataMasked).ToString(CultureInfo.InvariantCulture)} | " +
			   $"{formattedDuration} | {(Session.IsLive
				   ? Text(Localize, "Terminal.Value.Yes", "yes")
				   : Text(Localize, "Terminal.Value.No", "no"))}";
	}

	private static string Text(
		Func<string, string, string>? localize,
		string key,
		string fallback) => localize?.Invoke(key, fallback) ?? fallback;
}

internal static class TerminalAgentJournalPresentation
{
	public static string BuildLiveSessionIndicator(
		IReadOnlyList<LiveSessionRecord> sessions,
		Func<int, string>? multipleSessionsText)
	{
		ArgumentNullException.ThrowIfNull(sessions);
		var live = sessions.Where(static session => session.Mode == AgentJournalMode.Live).ToArray();
		return live.Length switch
		{
			0 => string.Empty,
			1 => $"Live context ({LiveSessionRegistry.FormatClientName(live[0].ClientName)})",
			_ => $"Live context ({multipleSessionsText?.Invoke(live.Length) ?? $"{live.Length} sessions"})"
		};
	}
	public static string BuildSessionHeader(Func<string, string, string>? localize = null) => string.Join(
		" | ",
		"Session",
		Text(localize, "AgentJournal.Column.Time", "Started"),
		Text(localize, "AgentJournal.Column.Client", "Client"),
		Text(localize, "AgentJournal.Column.Mode", "Mode"),
		Text(localize, "AgentJournal.Column.Project", "Project"),
		Text(localize, "AgentJournal.Column.Calls", "Calls"),
		Text(localize, "AgentJournal.Column.Characters", "Characters"),
		Text(localize, "AgentJournal.Column.Tokens", "Tokens"),
		Text(localize, "AgentJournal.Column.Files", "Files"),
		Text(localize, "AgentJournal.Column.Masked", "Masked"),
		Text(localize, "AgentJournal.Column.Duration", "Duration"),
		Text(localize, "AgentJournal.Live", "Live"));

	private static string BuildCallHeader(Func<string, string, string>? localize) => string.Join(
		" | ",
		Text(localize, "AgentJournal.Column.Number", "#"),
		"UTC",
		Text(localize, "AgentJournal.Column.Tool", "Tool"),
		Text(localize, "AgentJournal.Column.Project", "Root"),
		Text(localize, "AgentJournal.Column.Revision", "Revision"),
		Text(localize, "AgentJournal.Column.Duration", "Duration ms"),
		Text(localize, "AgentJournal.Column.Characters", "Characters"),
		Text(localize, "AgentJournal.Column.Tokens", "Tokens"),
		Text(localize, "AgentJournal.Column.Files", "Files"),
		Text(localize, "AgentJournal.Column.Masked", "Masked"),
		Text(localize, "AgentJournal.Column.Notices", "Notices"),
		Text(localize, "AgentJournal.Column.Error", "Error"));

	public static string BuildActivityIndicator(
		TerminalAgentJournalSnapshot snapshot,
		string? focusedTreePath,
		bool compact,
		Func<string, string, string>? localize = null)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(snapshot.LatestCall);
		var tool = TerminalTextEscaping.EscapeSingleLine(snapshot.LatestCall.Tool);
		if (!string.IsNullOrWhiteSpace(focusedTreePath) &&
			snapshot.DeliveredPathCalls.TryGetValue(focusedTreePath, out var deliveredCalls))
		{
			var fileHint = string.Format(
				CultureInfo.CurrentCulture,
				Text(
					localize,
					"AgentActivity.Tree.ToolTip",
					deliveredCalls == 1
						? "focused file delivered in {0} call"
						: "focused file delivered in {0} calls"),
				deliveredCalls);
			return compact
				? $"A F:{deliveredCalls:N0} {tool} ({snapshot.TotalCalls:N0})"
				: $"{Text(localize, "Menu.View.AgentActivity", "Agent activity")}: {fileHint}; " +
				  $"{tool} ({FormatCalls(snapshot.TotalCalls, localize)})";
		}
		return compact
			? $"A {tool} ({snapshot.TotalCalls:N0})"
			: $"{Text(localize, "Menu.View.AgentActivity", "Agent activity")}: " +
			  $"{tool} ({FormatCalls(snapshot.TotalCalls, localize)})";
	}

	public static string BuildCallDetails(
		AgentJournalSession session,
		IReadOnlyList<AgentJournalCall> calls,
		Func<string, string, string>? localize = null)
	{
		ArgumentNullException.ThrowIfNull(session);
		ArgumentNullException.ThrowIfNull(calls);
		var output = new StringBuilder();
		output.Append("Session ")
			.AppendLine(TerminalTextEscaping.EscapeSingleLine(session.Id));
		output.Append("Client ")
			.Append(TerminalTextEscaping.EscapeSingleLine(session.ClientName))
			.Append(" | Mode ")
			.Append(session.Mode.ToString().ToLowerInvariant())
			.Append(" | ")
			.AppendLine(session.IsLive ? "live" : "ended");
		var lostEvents = LostEventCount(calls);
		if (lostEvents > 0)
		{
			var incompleteHistory = Text(
				localize,
				"AgentJournal.Notice.HistoryIncomplete",
				"History is incomplete: {0} events could not be recorded.");
			output.AppendLine(string.Format(
				CultureInfo.CurrentCulture,
				incompleteHistory,
				lostEvents));
		}
		output.AppendLine()
			.AppendLine(BuildCallHeader(localize));

		foreach (var call in calls.OrderBy(static call => call.Sequence))
		{
			output.Append(call.Sequence.ToString(CultureInfo.InvariantCulture))
				.Append(" | ")
				.Append(call.Utc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))
				.Append(" | ")
				.Append(TerminalTextEscaping.EscapeSingleLine(call.Tool))
				.Append(" | ")
				.Append(call.RootIndex?.ToString(CultureInfo.InvariantCulture) ?? "-")
				.Append(" | ")
				.Append(call.Revision?.ToString(CultureInfo.InvariantCulture) ?? "-")
				.Append(" | ")
				.Append(call.DurationMs.ToString(CultureInfo.InvariantCulture))
				.Append(" | ")
				.Append(call.ResultCharacters.ToString(CultureInfo.InvariantCulture))
				.Append(" | ")
				.Append(call.EstimatedTokens.ToString(CultureInfo.InvariantCulture))
				.Append(" | ")
				.Append(call.FilesDelivered.ToString(CultureInfo.InvariantCulture))
				.Append(" | ")
				.Append((call.SecretsMasked + call.PrivateDataMasked).ToString(CultureInfo.InvariantCulture))
				.Append(" | ")
				.Append(TerminalTextEscaping.EscapeSingleLine(string.Join(',', call.Notices)))
				.Append(" | ")
				.AppendLine(call.ErrorCode is null
					? "-"
					: TerminalTextEscaping.EscapeSingleLine(call.ErrorCode));
		}

		var totals = session.Totals;
		var footer = Text(
			localize,
			"AgentJournal.Footer",
			"{0} calls · {1} characters · ≈{2} tokens · {3} files · {4} masked · {5} errors");
		output.AppendLine()
			.Append(string.Format(
				CultureInfo.CurrentCulture,
				footer,
				totals.Calls,
				totals.ResultCharacters,
				totals.EstimatedTokens,
				totals.FilesDelivered,
				totals.SecretsMasked + totals.PrivateDataMasked,
				totals.Errors));
		return output.ToString();
	}

	private static string FormatCalls(
		long calls,
		Func<string, string, string>? localize) => string.Format(
		CultureInfo.CurrentCulture,
		Text(localize, "AgentActivity.Status.Calls", calls == 1 ? "{0} call" : "{0} calls"),
		calls);

	private static string Text(
		Func<string, string, string>? localize,
		string key,
		string fallback) => localize?.Invoke(key, fallback) ?? fallback;

	public static IReadOnlySet<string> BuildDeliveredPathSet(
		string projectRoot,
		AgentJournalReceipt receipt)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ArgumentNullException.ThrowIfNull(receipt);
		var paths = new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer);
		var rootIndex = ResolveRootIndex(projectRoot, receipt.Session.Roots);
		foreach (var delivered in receipt.Calls
			.Where(call => call.RootIndex == rootIndex || receipt.Session.Roots.Count == 1 && call.RootIndex is null)
			.SelectMany(static call => call.DeliveredPaths)
			.Distinct(ProjectTreePathIdentity.CanonicalComparer))
		{
			if (TryResolveDeliveredPath(projectRoot, delivered, out var path))
				paths.Add(path);
		}
		return paths;
	}

	private static int ResolveRootIndex(string projectRoot, IReadOnlyList<AgentJournalRoot> roots)
	{
		for (var index = 0; index < roots.Count; index++)
			if (PathComparer.Default.Equals(PathUtility.Normalize(projectRoot), PathUtility.Normalize(roots[index].ConfiguredPath)))
				return index;
		return -1;
	}

	private static long LostEventCount(IEnumerable<AgentJournalCall> calls) => calls
		.Where(static call => call.Notices.Contains("history-incomplete", StringComparer.Ordinal))
		.Select(static call => call.Arguments.TryGetValue("lost_events", out var value) &&
			long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
				? Math.Max(0, parsed)
				: 0)
		.Sum();

	internal static bool TryResolveDeliveredPath(
		string projectRoot,
		string deliveredPath,
		out string path)
	{
		path = string.Empty;
		if (string.IsNullOrWhiteSpace(deliveredPath) || Path.IsPathFullyQualified(deliveredPath))
			return false;
		try
		{
			var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
			var candidate = Path.GetFullPath(Path.Combine(root, deliveredPath));
			var relative = Path.GetRelativePath(root, candidate);
			if (relative == ".." ||
				relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
				Path.IsPathFullyQualified(relative))
			{
				return false;
			}
			path = candidate;
			return true;
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
	}
}
