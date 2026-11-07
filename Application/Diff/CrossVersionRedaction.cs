using DevProjex.Application.Secrets;

namespace DevProjex.Application.Diff;

/// <summary>
/// Masks a value in both versions when either version had it masked. Detection is per text: a
/// hand mark is anchored to an offset in the working file, and a rule may need context the other
/// version lacks, so the same value can be found in one version and missed in the other. A diff
/// shows both versions side by side, which would put the missed copy next to its placeholder.
/// </summary>
internal static class CrossVersionRedaction
{
	// Shorter values would turn ordinary words into placeholders; detected secrets are far longer.
	internal const int MinimumPropagatedLength = 4;

	public static (string Old, string New) Apply(
		string oldSource,
		SecretTextRedactionResult oldResult,
		string newSource,
		SecretTextRedactionResult newResult)
	{
		var placeholders = new Dictionary<string, string>(StringComparer.Ordinal);
		Collect(oldSource, oldResult, placeholders);
		Collect(newSource, newResult, placeholders);
		if (placeholders.Count == 0)
			return (oldResult.Text, newResult.Text);
		var values = placeholders.Keys
			.OrderByDescending(static value => value.Length)
			.ThenBy(static value => value, StringComparer.Ordinal)
			.ToArray();
		return (Mask(oldResult, values, placeholders), Mask(newResult, values, placeholders));
	}

	private static void Collect(
		string source,
		SecretTextRedactionResult result,
		Dictionary<string, string> placeholders)
	{
		var map = result.CoordinateMap;
		for (var index = 0; index < map.RangeCount; index++)
		{
			var (sourceStart, sourceLength, transformedStart, transformedLength) = map.GetRange(index);
			if (sourceLength < MinimumPropagatedLength)
				continue;
			placeholders.TryAdd(
				source.Substring(sourceStart, sourceLength),
				result.Text.Substring(transformedStart, transformedLength));
		}
	}

	private static string Mask(
		SecretTextRedactionResult result,
		IReadOnlyList<string> values,
		IReadOnlyDictionary<string, string> placeholders)
	{
		var text = result.Text;
		var occupied = new List<(int Start, int End)>();
		var map = result.CoordinateMap;
		for (var index = 0; index < map.RangeCount; index++)
		{
			var (_, _, transformedStart, transformedLength) = map.GetRange(index);
			occupied.Add((transformedStart, transformedStart + transformedLength));
		}

		var matches = new List<(int Start, int Length, string Placeholder)>();
		foreach (var value in values)
		{
			for (var start = text.IndexOf(value, StringComparison.Ordinal);
				 start >= 0;
				 start = text.IndexOf(value, start + 1, StringComparison.Ordinal))
			{
				var end = start + value.Length;
				if (occupied.Any(range => start < range.End && range.Start < end))
					continue;
				occupied.Add((start, end));
				matches.Add((start, value.Length, placeholders[value]));
			}
		}
		if (matches.Count == 0)
			return text;

		matches.Sort(static (left, right) => left.Start.CompareTo(right.Start));
		var builder = new StringBuilder(text.Length);
		var cursor = 0;
		foreach (var (start, length, placeholder) in matches)
		{
			builder.Append(text, cursor, start - cursor).Append(placeholder);
			cursor = start + length;
		}
		return builder.Append(text, cursor, text.Length - cursor).ToString();
	}
}
