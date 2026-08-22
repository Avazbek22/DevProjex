using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Rendering;

internal static class TerminalColumnLayout
{
	private const int DefaultSpacing = 2;

	public static IReadOnlyList<string> Format(
		IReadOnlyList<string[]> rows,
		int spacing = DefaultSpacing)
	{
		if (rows.Count == 0)
			return [];

		var columnCount = rows[0].Length;
		if (columnCount == 0)
			return Enumerable.Repeat(string.Empty, rows.Count).ToArray();
		if (rows.Any(row => row.Length != columnCount))
			throw new ArgumentException("All terminal table rows must have the same column count.", nameof(rows));

		var widths = new int[columnCount];
		foreach (var row in rows)
		{
			for (var column = 0; column < columnCount; column++)
				widths[column] = Math.Max(widths[column], TerminalCellWidth.Measure(row[column]));
		}

		var separator = new string(' ', Math.Max(1, spacing));
		return rows.Select(row => FormatRow(row, widths, separator)).ToArray();
	}

	private static string FormatRow(
		IReadOnlyList<string> row,
		IReadOnlyList<int> widths,
		string separator)
	{
		var result = new StringBuilder();
		for (var column = 0; column < row.Count; column++)
		{
			if (column > 0)
				result.Append(separator);
			result.Append(column == row.Count - 1
				? row[column]
				: TerminalCellWidth.PadRight(row[column], widths[column]));
		}

		return result.ToString();
	}
}
