using System.Globalization;
using Terminal.Gui.Text;

namespace DevProjex.Terminal.CommandLine;

internal static class TerminalCellWidth
{
	public static int Measure(string value) =>
		string.IsNullOrEmpty(value) ? 0 : value.GetColumns();

	public static string Truncate(string value, int width)
	{
		if (string.IsNullOrEmpty(value) || width <= 0)
			return string.Empty;
		if (Measure(value) <= width)
			return value;

		var result = new StringBuilder(value.Length);
		var resultWidth = 0;
		var enumerator = StringInfo.GetTextElementEnumerator(value);
		while (enumerator.MoveNext())
		{
			var textElement = enumerator.GetTextElement();
			var textElementWidth = Math.Max(0, Measure(textElement));
			if (resultWidth + textElementWidth > width)
				break;
			result.Append(textElement);
			resultWidth += textElementWidth;
		}

		return result.ToString();
	}

	public static string PadRight(string value, int width)
	{
		var padding = Math.Max(0, width - Measure(value));
		return padding == 0 ? value : value + new string(' ', padding);
	}

	public static IReadOnlyList<string> Wrap(string value, int width)
	{
		var effectiveWidth = Math.Max(1, width);
		if (string.IsNullOrWhiteSpace(value))
			return [string.Empty];

		var lines = new List<string>();
		foreach (var paragraph in value.ReplaceLineEndings("\n").Split('\n'))
			WrapParagraph(paragraph, effectiveWidth, lines);
		return lines.Count == 0 ? [string.Empty] : lines;
	}

	private static void WrapParagraph(
		string paragraph,
		int width,
		ICollection<string> lines)
	{
		if (string.IsNullOrWhiteSpace(paragraph))
		{
			lines.Add(string.Empty);
			return;
		}

		var current = new StringBuilder();
		var currentWidth = 0;
		foreach (var word in paragraph.Split(
			         (char[]?)null,
			         StringSplitOptions.RemoveEmptyEntries))
		{
			var wordWidth = Measure(word);
			if (currentWidth > 0 && currentWidth + 1 + wordWidth <= width)
			{
				current.Append(' ').Append(word);
				currentWidth += 1 + wordWidth;
				continue;
			}

			if (currentWidth > 0)
			{
				lines.Add(current.ToString());
				current.Clear();
				currentWidth = 0;
			}

			if (wordWidth <= width)
			{
				current.Append(word);
				currentWidth = wordWidth;
				continue;
			}

			var chunks = SplitWord(word, width).ToArray();
			for (var index = 0; index < chunks.Length; index++)
			{
				var chunk = chunks[index];
				if (index < chunks.Length - 1)
					lines.Add(chunk);
				else
				{
					current.Append(chunk);
					currentWidth = Measure(chunk);
				}
			}
		}

		if (currentWidth > 0)
			lines.Add(current.ToString());
	}

	private static IEnumerable<string> SplitWord(string word, int width)
	{
		var chunk = new StringBuilder();
		var chunkWidth = 0;
		var lastBreakLength = 0;
		var enumerator = StringInfo.GetTextElementEnumerator(word);
		while (enumerator.MoveNext())
		{
			var textElement = enumerator.GetTextElement();
			var textElementWidth = Math.Max(0, Measure(textElement));
			if (chunkWidth > 0 && chunkWidth + textElementWidth > width)
			{
				if (lastBreakLength > 0)
				{
					yield return chunk.ToString(0, lastBreakLength);
					chunk.Remove(0, lastBreakLength);
					chunkWidth = Measure(chunk.ToString());
				}
				else
				{
					yield return chunk.ToString();
					chunk.Clear();
					chunkWidth = 0;
				}
				lastBreakLength = 0;
			}

			chunk.Append(textElement);
			chunkWidth += textElementWidth;
			if (textElement == "|")
				lastBreakLength = chunk.Length;
		}

		if (chunk.Length > 0)
			yield return chunk.ToString();
	}
}
