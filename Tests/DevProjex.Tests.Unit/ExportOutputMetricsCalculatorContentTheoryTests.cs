using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DevProjex.Application.Services;
using DevProjex.Kernel;
using Xunit;

namespace DevProjex.Tests.Unit;

public sealed class ExportOutputMetricsCalculatorContentTheoryTests
{
	private const string ClipboardBlankLine = "\u00A0";
	private const string NoContentMarker = "[No Content, 0 bytes]";
	private const string WhitespaceMarkerPrefix = "[Whitespace, ";
	private const string WhitespaceMarkerSuffix = " bytes]";

	public static IEnumerable<object[]> ContentCases()
	{
		var variants = GetVariants();
		var caseId = 0;

		// 8 single-file cases
		for (var i = 0; i < variants.Count; i++)
		{
			var entries = new List<(string Path, ContentVariant Variant)>
			{
				($"single-{i}.txt", variants[i])
			};
			yield return BuildCase(caseId++, entries);
		}

		// 64 two-file cases (8 x 8)
		for (var i = 0; i < variants.Count; i++)
		{
			for (var j = 0; j < variants.Count; j++)
			{
				var entries = new List<(string Path, ContentVariant Variant)>
				{
					($"zeta-{i}.txt", variants[i]),
					($"alpha-{j}.txt", variants[j])
				};
				yield return BuildCase(caseId++, entries);
			}
		}

		// 32 three-file cases with duplicate paths to verify first-entry dedup behavior.
		for (var i = 0; i < variants.Count; i++)
		{
			for (var offset = 1; offset <= 4; offset++)
			{
				var mid = variants[(i + offset) % variants.Count];
				var duplicate = variants[(i + offset + 1) % variants.Count];

				var entries = new List<(string Path, ContentVariant Variant)>
				{
					($"dup-{i}.txt", variants[i]),
					($"mid-{offset}-{i}.txt", mid),
					($"dup-{i}.txt", duplicate)
				};
				yield return BuildCase(caseId++, entries);
			}
		}
	}

	[Theory]
	[MemberData(nameof(ContentCases))]
	public void FromContentFiles_MatchesRenderedExportMetrics(
		int caseId,
		IReadOnlyList<ContentFileMetrics> files,
		string expectedExportText)
	{
		var actual = ExportOutputMetricsCalculator.FromContentFiles(files);
		var expected = ExportOutputMetricsCalculator.FromText(expectedExportText);

		Assert.Equal(expected.Lines, actual.Lines);
		Assert.Equal(expected.Chars, actual.Chars);
		Assert.Equal(expected.Tokens, actual.Tokens);
		Assert.True(caseId >= 0);
	}

	private static object[] BuildCase(int caseId, IReadOnlyList<(string Path, ContentVariant Variant)> entries)
	{
		var files = entries
			.Select(entry => new ContentFileMetrics(
				Path: entry.Path,
				SizeBytes: entry.Variant.SizeBytes,
				LineCount: entry.Variant.LineCount,
				CharCount: entry.Variant.CharCount,
				IsEmpty: entry.Variant.IsEmpty,
				IsWhitespaceOnly: entry.Variant.IsWhitespaceOnly,
				TrailingNewlineChars: entry.Variant.TrailingNewlineChars,
				TrailingNewlineLineBreaks: entry.Variant.TrailingNewlineLineBreaks))
			.ToList();

		var expectedText = RenderExpectedText(entries);
		return new object[] { caseId, files, expectedText };
	}

	private static string RenderExpectedText(IReadOnlyList<(string Path, ContentVariant Variant)> entries)
	{
		var firstByPath = new Dictionary<string, ContentVariant>(PathComparer.Default);
		foreach (var entry in entries)
		{
			if (!firstByPath.ContainsKey(entry.Path))
				firstByPath[entry.Path] = entry.Variant;
		}

		var ordered = firstByPath
			.OrderBy(pair => pair.Key, PathComparer.Default)
			.ToList();

		if (ordered.Count == 0)
			return string.Empty;

		var sb = new StringBuilder();
		var anyWritten = false;
		foreach (var (path, variant) in ordered)
		{
			if (anyWritten)
			{
				sb.AppendLine(ClipboardBlankLine);
				sb.AppendLine(ClipboardBlankLine);
			}

			anyWritten = true;
			sb.AppendLine($"{path}:");
			sb.AppendLine(ClipboardBlankLine);

			if (variant.IsEmpty)
			{
				sb.AppendLine(NoContentMarker);
			}
			else if (variant.IsWhitespaceOnly)
			{
				sb.AppendLine($"{WhitespaceMarkerPrefix}{variant.SizeBytes}{WhitespaceMarkerSuffix}");
			}
			else
			{
				sb.AppendLine(variant.RenderedText);
			}
		}

		return sb.ToString().TrimEnd('\r', '\n');
	}

	private static List<ContentVariant> GetVariants() =>
		new()
		{
			ContentVariant.FromRaw("empty", string.Empty),
			ContentVariant.FromRaw("spaces", "   "),
			ContentVariant.FromRaw("single", "abc"),
			ContentVariant.FromRaw("lf", "a\nb"),
			ContentVariant.FromRaw("lf_trailing", "a\nb\n"),
			ContentVariant.FromRaw("crlf_trailing", "a\r\nb\r\n"),
			ContentVariant.FromRaw("newline_only", "\n"),
			ContentVariant.FromRaw("mixed", "x\r\ny\nz")
		};

	private sealed record ContentVariant(
		string Name,
		long SizeBytes,
		int LineCount,
		int CharCount,
		bool IsEmpty,
		bool IsWhitespaceOnly,
		int TrailingNewlineChars,
		int TrailingNewlineLineBreaks,
		string RenderedText)
	{
		public static ContentVariant FromRaw(string name, string rawContent)
		{
			var charCount = rawContent.Length;
			var lineCount = rawContent.Length == 0 ? 0 : 1 + CountLineBreaks(rawContent);
			var isEmpty = rawContent.Length == 0;
			var isWhitespaceOnly = rawContent.Length > 0 && string.IsNullOrWhiteSpace(rawContent);
			var trailing = CountTrailingNewlineInfo(rawContent);
			var rendered = isEmpty || isWhitespaceOnly
				? string.Empty
				: rawContent.TrimEnd('\r', '\n');

			return new ContentVariant(
				name,
				SizeBytes: charCount,
				LineCount: lineCount,
				CharCount: charCount,
				IsEmpty: isEmpty,
				IsWhitespaceOnly: isWhitespaceOnly,
				TrailingNewlineChars: trailing.Chars,
				TrailingNewlineLineBreaks: trailing.LineBreaks,
				RenderedText: rendered);
		}

		private static int CountLineBreaks(string text)
		{
			var count = 0;
			foreach (var c in text.AsSpan())
			{
				if (c == '\n')
					count++;
			}

			return count;
		}

		private static (int Chars, int LineBreaks) CountTrailingNewlineInfo(string text)
		{
			var chars = 0;
			var lineBreaks = 0;
			for (var i = text.Length - 1; i >= 0; i--)
			{
				var c = text[i];
				if (c is not ('\r' or '\n'))
					break;

				chars++;
				if (c == '\n')
					lineBreaks++;
			}

			return (chars, lineBreaks);
		}
	}
}
