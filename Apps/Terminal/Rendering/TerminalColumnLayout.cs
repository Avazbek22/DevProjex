using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Rendering;

internal static class TerminalColumnLayout
{
	private const int DefaultSpacing = 2;

	public static IReadOnlyList<string> Format(
		IReadOnlyList<string[]> rows,
		int spacing = DefaultSpacing)
		=> FormatCore(rows, headers: null, maximumWidth: null, truncationColumn: null, spacing);

	public static IReadOnlyList<string> FormatForOutput(
		IReadOnlyList<string[]> rows,
		IReadOnlyList<string> headers,
		ITerminalEnvironment environment,
		TerminalOutputOptions outputOptions,
		int? truncationColumn = null,
		int spacing = DefaultSpacing)
	{
		ArgumentNullException.ThrowIfNull(environment);
		ArgumentNullException.ThrowIfNull(outputOptions);
		if (!environment.IsOutputInteractive || environment.IsTermDumb)
			return FormatCore(rows, headers: null, maximumWidth: null, truncationColumn: null, spacing);
		var capabilities = TerminalCapabilities.Resolve(environment, outputOptions, forStandardError: false);
		return FormatCore(rows, headers, capabilities.Width, truncationColumn, spacing);
	}

	private static IReadOnlyList<string> FormatCore(
		IReadOnlyList<string[]> rows,
		IReadOnlyList<string>? headers,
		int? maximumWidth,
		int? truncationColumn,
		int spacing)
	{
		if (rows.Count == 0 && headers is null)
			return [];

		var columnCount = headers?.Count ?? rows[0].Length;
		if (columnCount == 0)
			return Enumerable.Repeat(string.Empty, rows.Count + (headers is null ? 0 : 1)).ToArray();
		if (rows.Any(row => row.Length != columnCount))
			throw new ArgumentException("All terminal table rows must have the same column count.", nameof(rows));
		if (headers is not null && headers.Count != columnCount)
			throw new ArgumentException("Terminal table headers must match the row column count.", nameof(headers));

		var materialized = headers is null
			? rows.Select(static row => row.ToArray()).ToArray()
			: new[] { headers.ToArray() }.Concat(rows.Select(static row => row.ToArray())).ToArray();

		var widths = new int[columnCount];
		foreach (var row in materialized)
		{
			for (var column = 0; column < columnCount; column++)
				widths[column] = Math.Max(widths[column], TerminalCellWidth.Measure(row[column]));
		}

		var separatorWidth = Math.Max(1, spacing);
		if (maximumWidth is { } limit)
		{
			var totalWidth = widths.Sum() + separatorWidth * Math.Max(0, columnCount - 1);
			if (totalWidth > limit)
			{
				var column = truncationColumn is >= 0 && truncationColumn < columnCount
					? truncationColumn.Value
					: Array.IndexOf(widths, widths.Max());
				widths[column] = Math.Max(1, widths[column] - (totalWidth - limit));
				foreach (var row in materialized)
					row[column] = TerminalCellWidth.TruncateMiddle(row[column], widths[column]);
			}
		}

		var separator = new string(' ', separatorWidth);
		return materialized.Select(row => FormatRow(row, widths, separator)).ToArray();
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
