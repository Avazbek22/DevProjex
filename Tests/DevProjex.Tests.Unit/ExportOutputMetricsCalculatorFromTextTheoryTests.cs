namespace DevProjex.Tests.Unit;

public sealed class ExportOutputMetricsCalculatorFromTextTheoryTests
{
	public static IEnumerable<object[]> FromTextCases()
	{
		var prefixes = new[]
		{
			string.Empty,
			"PRE-"
		};

		var bodies = new[]
		{
			string.Empty,
			"a",
			"abc",
			"alpha beta",
			"line1\nline2",
			"line1\r\nline2",
			"line1\rline2",
			" \t ",
			"x\r\ny\nz",
			"x\r\ny\rz\n"
		};

		var suffixes = new[]
		{
			string.Empty,
			"\n",
			"\r",
			"\r\n",
			"\n\n",
			"\r\r",
			"\r\n\r\n",
			"\r\n\r",
			"\n\r\n",
			" \n",
			" \r\n"
		};

		var id = 0;
		foreach (var prefix in prefixes)
		{
			foreach (var body in bodies)
			{
				foreach (var suffix in suffixes)
				{
					yield return [ id++, prefix + body + suffix ];
				}
			}
		}
	}

	[Theory]
	[MemberData(nameof(FromTextCases))]
	public void FromText_ReturnsExpectedMetrics(int caseId, string text)
	{
		var actual = ExportOutputMetricsCalculator.FromText(text);
		var expectedChars = GetExpectedNormalizedCharCount(text);
		var expectedLines = GetExpectedNormalizedLineCount(text);
		var expectedTokens = (int)Math.Ceiling(expectedChars / 4.0);

		Assert.Equal(expectedLines, actual.Lines);
		Assert.Equal(expectedChars, actual.Chars);
		Assert.Equal(expectedTokens, actual.Tokens);
		Assert.True(caseId >= 0);
	}

	[Fact]
	public void FromText_NormalizesLineBreakStyles_ToSameMetrics()
	{
		const string lfText = "root:\n\n├── child\n└── file";
		const string crlfText = "root:\r\n\r\n├── child\r\n└── file";

		var lf = ExportOutputMetricsCalculator.FromText(lfText);
		var crlf = ExportOutputMetricsCalculator.FromText(crlfText);

		Assert.Equal(lf.Lines, crlf.Lines);
		Assert.Equal(lf.Chars, crlf.Chars);
		Assert.Equal(lf.Tokens, crlf.Tokens);
	}

	[Fact]
	public void FromText_TrailingLineBreak_AddsVisualEmptyLine()
	{
		var withoutTrailing = ExportOutputMetricsCalculator.FromText("a");
		var withTrailing = ExportOutputMetricsCalculator.FromText("a\n");

		Assert.Equal(1, withoutTrailing.Lines);
		Assert.Equal(2, withTrailing.Lines);
	}

	private static int GetExpectedNormalizedCharCount(string text)
	{
		if (string.IsNullOrEmpty(text))
			return 0;

		var normalizedChars = 0;
		for (var i = 0; i < text.Length; i++)
		{
			var c = text[i];
			if (c == '\r')
			{
				if (i + 1 < text.Length && text[i + 1] == '\n')
					i++;
			}

			normalizedChars++;
		}

		return normalizedChars;
	}

	private static int GetExpectedNormalizedLineCount(string text)
	{
		if (string.IsNullOrEmpty(text))
			return 0;

		var lineBreaks = 0;
		for (var i = 0; i < text.Length; i++)
		{
			var c = text[i];
			if (c == '\r')
			{
				if (i + 1 < text.Length && text[i + 1] == '\n')
					i++;

				lineBreaks++;
				continue;
			}

			if (c == '\n')
				lineBreaks++;
		}

		// Visual line counting: non-empty text always has at least one line.
		return lineBreaks + 1;
	}
}
