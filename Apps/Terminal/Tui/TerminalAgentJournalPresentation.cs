using System.Globalization;
using System.Text;
using DevProjex.Kernel.Models;
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
		foreach (var delivered in receipt.DeliveredPaths)
		{
			if (TerminalAgentJournalPresentation.TryResolveDeliveredPath(
					projectRoot,
					delivered.Path,
					out var path))
			{
				paths[path] = delivered.Calls;
			}
		}
		return new TerminalAgentJournalSnapshot(
			receipt.Session,
			receipt.Calls.OrderBy(static call => call.Sequence).LastOrDefault(),
			receipt.Totals.Calls,
			paths);
	}
}

internal sealed record TerminalAgentJournalSessionRow(AgentJournalSession Session)
{
	public override string ToString()
	{
		var started = Session.StartedUtc.ToLocalTime().ToString(
			"yyyy-MM-dd HH:mm:ss",
			CultureInfo.InvariantCulture);
		var mode = Session.Mode.ToString().ToLowerInvariant();
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
		       $"{formattedDuration} | {(Session.IsLive ? "yes" : "no")}";
	}
}

internal static class TerminalAgentJournalPresentation
{
	internal const string SessionHeader =
		"Session | Started | Client | Mode | Project | Calls | Characters | Tokens | Files | Masked | Duration | Live";
	private const string CallHeader =
		"# | UTC | Tool | Root | Revision | Duration ms | Characters | Tokens | Files | Masked | Notices | Error";

	public static string BuildActivityIndicator(
		TerminalAgentJournalSnapshot snapshot,
		string? focusedTreePath,
		bool compact)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(snapshot.LatestCall);
		var tool = TerminalTextEscaping.EscapeSingleLine(snapshot.LatestCall.Tool);
		if (!string.IsNullOrWhiteSpace(focusedTreePath) &&
			snapshot.DeliveredPathCalls.TryGetValue(focusedTreePath, out var deliveredCalls))
		{
			return compact
				? $"A F:{deliveredCalls:N0} {tool} ({snapshot.TotalCalls:N0})"
				: $"Agent activity: focused file delivered in {deliveredCalls:N0} " +
				  (deliveredCalls == 1 ? "call" : "calls") +
				  $"; {tool} ({snapshot.TotalCalls:N0} calls)";
		}
		return compact
			? $"A {tool} ({snapshot.TotalCalls:N0})"
			: $"Agent activity: {tool} ({snapshot.TotalCalls:N0} calls)";
	}

	public static string BuildCallDetails(
		AgentJournalSession session,
		IReadOnlyList<AgentJournalCall> calls)
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
		output.AppendLine()
			.AppendLine(CallHeader);

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
		output.AppendLine()
			.Append("Totals | ")
			.Append(totals.Calls.ToString("N0", CultureInfo.InvariantCulture))
			.Append(" calls | ")
			.Append(totals.ResultCharacters.ToString("N0", CultureInfo.InvariantCulture))
			.Append(" characters | ")
			.Append(totals.EstimatedTokens.ToString("N0", CultureInfo.InvariantCulture))
			.Append(" tokens | ")
			.Append(totals.FilesDelivered.ToString("N0", CultureInfo.InvariantCulture))
			.Append(" files | ")
			.Append(totals.SecretsMasked.ToString("N0", CultureInfo.InvariantCulture))
			.Append(" secrets | ")
			.Append(totals.PrivateDataMasked.ToString("N0", CultureInfo.InvariantCulture))
			.Append(" private data | ")
			.Append(totals.Errors.ToString("N0", CultureInfo.InvariantCulture))
			.Append(" errors");
		return output.ToString();
	}

	public static IReadOnlySet<string> BuildDeliveredPathSet(
		string projectRoot,
		AgentJournalReceipt receipt)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ArgumentNullException.ThrowIfNull(receipt);
		var paths = new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer);
		foreach (var delivered in receipt.DeliveredPaths)
		{
			if (TryResolveDeliveredPath(projectRoot, delivered.Path, out var path))
				paths.Add(path);
		}
		return paths;
	}

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
