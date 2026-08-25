using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Tui;

internal sealed record TerminalGitProgress(
	string PhaseKey,
	int? Percent,
	string Detail);

internal static class TerminalGitProgressParser
{
	public static TerminalGitProgress Parse(
		string status,
		string currentPhaseKey,
		int? currentPercent)
	{
		var trimmed = status.Trim();
		if (TryParseStandalonePercent(trimmed, out var standalonePercent))
			return new TerminalGitProgress(currentPhaseKey, standalonePercent, string.Empty);

		var phaseKey = ResolvePhaseKey(trimmed) ?? currentPhaseKey;
		var percent = TryExtractPercent(trimmed, out var parsedPercent)
			? parsedPercent
			: currentPercent;
		return new TerminalGitProgress(
			phaseKey,
			percent,
			SanitizeDetail(trimmed));
	}

	private static string? ResolvePhaseKey(string value)
	{
		if (value.StartsWith("Receiving objects:", StringComparison.OrdinalIgnoreCase) ||
		    value.StartsWith("remote: Enumerating objects:", StringComparison.OrdinalIgnoreCase) ||
		    value.StartsWith("remote: Counting objects:", StringComparison.OrdinalIgnoreCase) ||
		    value.StartsWith("remote: Compressing objects:", StringComparison.OrdinalIgnoreCase))
		{
			return "Terminal.Tui.Clone.ReceivingObjects";
		}
		if (value.StartsWith("Resolving deltas:", StringComparison.OrdinalIgnoreCase))
			return "Terminal.Tui.Clone.ResolvingDeltas";
		if (value.StartsWith("Checking out files:", StringComparison.OrdinalIgnoreCase) ||
		    value.StartsWith("Updating files:", StringComparison.OrdinalIgnoreCase))
		{
			return "Terminal.Tui.Clone.CheckingOut";
		}

		return null;
	}

	private static string SanitizeDetail(string value)
	{
		if (value.Contains("://", StringComparison.Ordinal) ||
		    value.Contains("Authorization", StringComparison.OrdinalIgnoreCase))
		{
			return string.Empty;
		}

		return TerminalCellWidth.Measure(value) > 180
			? TerminalCellWidth.Truncate(value, 177) + "..."
			: value;
	}

	private static bool TryParseStandalonePercent(string value, out int percent)
	{
		percent = -1;
		return value.EndsWith('%') &&
		       int.TryParse(value.AsSpan(0, value.Length - 1), out percent) &&
		       percent is >= 0 and <= 100;
	}

	private static bool TryExtractPercent(string value, out int percent)
	{
		percent = -1;
		var percentIndex = value.IndexOf('%');
		if (percentIndex <= 0)
			return false;

		var start = percentIndex - 1;
		while (start >= 0 && char.IsDigit(value[start]))
			start--;
		return int.TryParse(
			       value.AsSpan(start + 1, percentIndex - start - 1),
			       out percent) &&
		       percent is >= 0 and <= 100;
	}
}
