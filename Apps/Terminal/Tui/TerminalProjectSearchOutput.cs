using System.Globalization;
using DevProjex.Terminal.Execution;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Tui;

internal static class TerminalProjectSearchOutput
{
	private const string MatchTextIndent = "  ";

	public static string Format(
		SearchCommandHandler.SearchResult result,
		LocalizationService localization)
	{
		ArgumentNullException.ThrowIfNull(result);
		ArgumentNullException.ThrowIfNull(localization);
		var pattern = TerminalTextEscaping.EscapeSingleLine(result.Pattern);
		var boundary = result.Boundary;
		var lines = new List<string>(result.Matches.Count * 2 + 4);
		if (boundary.EncounteredMatches == 0)
		{
			lines.Add(localization.Format(
				"Terminal.Tui.Command.Grep.Result.None",
				pattern,
				boundary.InspectedSources));
		}
		else
		{
			lines.Add(localization.Format(
				"Terminal.Tui.Command.Grep.Result.Summary",
				pattern,
				boundary.EncounteredMatches,
				result.MatchingFiles));
			lines.Add(string.Empty);
			foreach (var match in result.Matches)
			{
				// Match text arrives escaped by the shared search renderer; paths and names do not.
				var location = TerminalTextEscaping.EscapeSingleLine(match.Path) + ":" +
					match.Line.ToString(CultureInfo.InvariantCulture);
				lines.Add(match.Declaration is { Length: > 0 } declaration
					? $"{location} — {TerminalTextEscaping.EscapeSingleLine(declaration)}"
					: location);
				lines.Add(MatchTextIndent + TrimIndentation(match.Text));
			}
			if (result.Matches.Count < boundary.EncounteredMatches)
			{
				lines.Add(string.Empty);
				lines.Add(localization.Format(
					"Terminal.Tui.Command.Grep.Result.Shown",
					result.Matches.Count,
					boundary.EncounteredMatches));
			}
		}
		if (boundary.InspectionByteLimitReached)
		{
			lines.Add(localization.Format(
				"Terminal.Tui.Command.Grep.Result.Partial",
				boundary.InspectedSources,
				boundary.EligibleSources));
		}
		if (boundary.UnscannableSources > 0)
		{
			lines.Add(localization.Format(
				"Terminal.Tui.Command.Grep.Result.Unscannable",
				boundary.UnscannableSources));
		}
		return string.Join('\n', lines);
	}

	// The shared renderer escapes tab indentation as a literal \t; indentation carries no
	// meaning in a one-line match preview.
	private static string TrimIndentation(string text)
	{
		var remaining = text.AsSpan().TrimStart();
		while (remaining.StartsWith(@"\t", StringComparison.Ordinal))
			remaining = remaining[2..].TrimStart();
		return remaining.TrimEnd().ToString();
	}
}
