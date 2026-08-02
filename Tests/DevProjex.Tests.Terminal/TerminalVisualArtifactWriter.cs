using System.Security;

namespace DevProjex.Tests.Terminal;

internal static class TerminalVisualArtifactWriter
{
	private const string ArtifactDirectoryVariable = "DEVPROJEX_TUI_ARTIFACT_DIR";
	private const int CellWidth = 9;
	private const int CellHeight = 18;

	public static void WriteIfRequested(string name, TerminalPtyHarness terminal)
	{
		var directory = Environment.GetEnvironmentVariable(ArtifactDirectoryVariable);
		if (string.IsNullOrWhiteSpace(directory))
			return;

		Directory.CreateDirectory(directory);
		var columns = terminal.Columns;
		var rows = terminal.Rows;
		var svg = new StringBuilder();
		svg.AppendLine(
			$"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{columns * CellWidth}\" " +
			$"height=\"{rows * CellHeight}\" viewBox=\"0 0 {columns * CellWidth} {rows * CellHeight}\">");
		svg.AppendLine("<rect width=\"100%\" height=\"100%\" fill=\"#0d1117\"/>");
		svg.AppendLine(
			"<g font-family=\"Cascadia Mono, Consolas, monospace\" font-size=\"14\" " +
			"dominant-baseline=\"text-before-edge\">");
		var styles = new HashSet<string>(StringComparer.Ordinal);

		for (var row = 0; row < rows; row++)
		{
			for (var column = 0; column < columns; column++)
			{
				var style = terminal.CaptureCellStyle(row, column);
				styles.Add(
					$"fg={style.ForegroundMode}:{style.Foreground};" +
					$"bg={style.BackgroundMode}:{style.Background};" +
					$"bold={style.Bold};dim={style.Dim};inverse={style.Inverse}");
				var colors = ResolveColors(style);
				if (colors.Background != "#0d1117")
				{
					svg.AppendLine(
						$"<rect x=\"{column * CellWidth}\" y=\"{row * CellHeight}\" " +
						$"width=\"{CellWidth}\" height=\"{CellHeight}\" fill=\"{colors.Background}\"/>");
				}

				if (string.IsNullOrEmpty(style.Content) || style.Content == " ")
					continue;
				var content = SecurityElement.Escape(style.Content) ?? string.Empty;
				var weight = style.Bold ? " font-weight=\"700\"" : string.Empty;
				svg.AppendLine(
					$"<text x=\"{column * CellWidth}\" y=\"{row * CellHeight + 1}\" " +
					$"fill=\"{colors.Foreground}\"{weight}>{content}</text>");
			}
		}

		svg.AppendLine("</g>");
		foreach (var style in styles.Order(StringComparer.Ordinal))
			svg.AppendLine($"<!-- {style} -->");
		svg.AppendLine("</svg>");
		File.WriteAllText(
			Path.Combine(directory, $"{name}.svg"),
			svg.ToString(),
			new UTF8Encoding(false));
	}

	private static (string Foreground, string Background) ResolveColors(TerminalCellStyle style)
	{
		var foreground = ResolveColor(style.ForegroundMode, style.Foreground, "#d8dee9");
		var background = ResolveColor(style.BackgroundMode, style.Background, "#0d1117");
		if (style.Dim && style.ForegroundMode == 0)
			foreground = "#7d8590";
		if (style.Inverse)
			(foreground, background) = (background == "#0d1117" ? "#0d1117" : background,
				foreground == "#d8dee9" ? "#d8dee9" : foreground);
		return (foreground, background);
	}

	private static string ResolveColor(int mode, int value, string fallback)
	{
		const int rgbMode = 0x3000000;
		const int palette16Mode = 0x1000000;
		const int palette256Mode = 0x2000000;
		return mode switch
		{
			1 or 3 or rgbMode => $"#{value & 0xFFFFFF:X6}",
			2 or palette16Mode or palette256Mode => ResolvePalette(value & 0xFF),
			_ => fallback
		};
	}

	private static string ResolvePalette(int index)
	{
		string[] ansi =
		[
			"#0d1117", "#ff7b72", "#3fb950", "#d29922",
			"#58a6ff", "#bc8cff", "#39c5cf", "#d8dee9",
			"#484f58", "#ffa198", "#56d364", "#e3b341",
			"#79c0ff", "#d2a8ff", "#56d4dd", "#f0f6fc"
		];
		if (index < ansi.Length)
			return ansi[index];
		if (index is >= 16 and <= 231)
		{
			var cube = index - 16;
			var red = cube / 36;
			var green = cube / 6 % 6;
			var blue = cube % 6;
			return $"#{Cube(red):X2}{Cube(green):X2}{Cube(blue):X2}";
		}

		var gray = 8 + (index - 232) * 10;
		return $"#{gray:X2}{gray:X2}{gray:X2}";
	}

	private static int Cube(int component) => component == 0 ? 0 : 55 + component * 40;
}
