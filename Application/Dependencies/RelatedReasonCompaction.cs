using System.Globalization;

namespace DevProjex.Application.Dependencies;

/// <summary>
/// Shortens the reasons of one related-file row for text output. Every "&lt;text&gt; at line N"
/// that shares the same text becomes one "&lt;text&gt; at lines 56, 141, 186" entry, placed where
/// the first of them stood; every other reason keeps its text and its place. Structured output
/// keeps one reason per site and does not go through this.
/// </summary>
public static class RelatedReasonCompaction
{
	private const int MaximumListedLines = 5;
	private const string SiteMarker = " at line ";

	public static IReadOnlyList<string> Compact(IReadOnlyList<string> reasons)
	{
		ArgumentNullException.ThrowIfNull(reasons);
		if (reasons.Count < 2)
			return reasons;

		var entries = new List<ReasonEntry>(reasons.Count);
		var linesByText = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
		foreach (var reason in reasons)
		{
			if (!TrySplitSite(reason, out var text, out var line))
			{
				entries.Add(new ReasonEntry(reason, SiteText: null));
				continue;
			}
			if (linesByText.TryGetValue(text, out var lines))
			{
				lines.Add(line);
				continue;
			}
			linesByText.Add(text, [line]);
			entries.Add(new ReasonEntry(reason, text));
		}
		if (entries.Count == reasons.Count)
			return reasons;

		return entries
			.Select(entry => entry.SiteText is null
				? entry.Reason
				: FormatSites(entry.Reason, entry.SiteText, linesByText[entry.SiteText]))
			.ToArray();
	}

	private static string FormatSites(string firstReason, string text, SortedSet<int> lines)
	{
		if (lines.Count == 1)
			return firstReason;
		var listed = string.Join(", ", lines
			.Take(MaximumListedLines)
			.Select(static line => line.ToString(CultureInfo.InvariantCulture)));
		var hidden = lines.Count - MaximumListedLines;
		return hidden > 0
			? $"{text} at lines {listed} (+{hidden.ToString(CultureInfo.InvariantCulture)} more)"
			: $"{text} at lines {listed}";
	}

	// Only the canonical spelling of a line number counts as a site, so a compacted entry never
	// stands for a reason whose text it does not reproduce.
	private static bool TrySplitSite(string reason, out string text, out int line)
	{
		text = string.Empty;
		line = 0;
		var markerIndex = reason.LastIndexOf(SiteMarker, StringComparison.Ordinal);
		if (markerIndex <= 0)
			return false;
		var digits = reason.AsSpan(markerIndex + SiteMarker.Length);
		if (digits.IsEmpty ||
			(digits.Length > 1 && digits[0] == '0') ||
			!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out line))
		{
			return false;
		}
		text = reason[..markerIndex];
		return true;
	}

	private readonly record struct ReasonEntry(string Reason, string? SiteText);
}
