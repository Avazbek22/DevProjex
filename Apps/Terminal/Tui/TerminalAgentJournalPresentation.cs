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
		var started = Session.StartedUtc.UtcDateTime.ToString(
			"yyyy-MM-dd HH:mm:ss'Z'",
			CultureInfo.InvariantCulture);
		var mode = Session.Mode.ToString().ToLowerInvariant();
		var client = TerminalTextEscaping.EscapeSingleLine(Session.ClientName);
		return $"{started} | {mode} | {client} | " +
		       $"{Session.Totals.Calls.ToString("N0", CultureInfo.InvariantCulture)} calls | " +
		       $"{Session.Totals.FilesDelivered.ToString("N0", CultureInfo.InvariantCulture)} files" +
		       (Session.Totals.Errors > 0
			       ? $" | {Session.Totals.Errors.ToString("N0", CultureInfo.InvariantCulture)} errors"
			       : string.Empty);
	}
}

internal static class TerminalAgentJournalPresentation
{
	private const string CallHeader =
		"UTC | Tool | Duration | Characters | Tokens | Files | Secrets | Private data | Result";

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
			output.Append(call.Utc.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
				.Append(" | ")
				.Append(TerminalTextEscaping.EscapeSingleLine(call.Tool))
				.Append(" | ")
				.Append(call.DurationMs.ToString("N0", CultureInfo.InvariantCulture))
				.Append(" ms | ")
				.Append(call.ResultCharacters.ToString("N0", CultureInfo.InvariantCulture))
				.Append(" | ")
				.Append(call.EstimatedTokens.ToString("N0", CultureInfo.InvariantCulture))
				.Append(" | ")
				.Append(call.FilesDelivered.ToString("N0", CultureInfo.InvariantCulture))
				.Append(" | ")
				.Append(call.SecretsMasked.ToString("N0", CultureInfo.InvariantCulture))
				.Append(" | ")
				.Append(call.PrivateDataMasked.ToString("N0", CultureInfo.InvariantCulture))
				.Append(" | ")
				.AppendLine(call.ErrorCode is null
					? "ok"
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
