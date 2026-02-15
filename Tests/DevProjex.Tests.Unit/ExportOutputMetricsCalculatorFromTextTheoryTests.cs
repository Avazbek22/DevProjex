using System;
using System.Collections.Generic;
using DevProjex.Application.Services;
using Xunit;

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
			" \t ",
			"x\r\ny\nz"
		};

		var suffixes = new[]
		{
			string.Empty,
			"\n",
			"\r\n",
			"\n\n",
			"\r\n\r\n",
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
					yield return new object[] { id++, prefix + body + suffix };
				}
			}
		}
	}

	[Theory]
	[MemberData(nameof(FromTextCases))]
	public void FromText_ReturnsExpectedMetrics(int caseId, string text)
	{
		var actual = ExportOutputMetricsCalculator.FromText(text);
		var expectedChars = text.Length;
		var expectedLines = GetExpectedLineCount(text);
		var expectedTokens = (int)Math.Ceiling(expectedChars / 4.0);

		Assert.Equal(expectedLines, actual.Lines);
		Assert.Equal(expectedChars, actual.Chars);
		Assert.Equal(expectedTokens, actual.Tokens);
		Assert.True(caseId >= 0);
	}

	private static int GetExpectedLineCount(string text)
	{
		if (string.IsNullOrEmpty(text))
			return 0;

		var lineBreaks = 0;
		foreach (var c in text.AsSpan())
		{
			if (c == '\n')
				lineBreaks++;
		}

		var endsWithLineBreak = text[^1] == '\n' || text[^1] == '\r';
		return lineBreaks + (endsWithLineBreak ? 0 : 1);
	}
}
