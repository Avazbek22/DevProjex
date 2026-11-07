using DevProjex.Application.Diff;

namespace DevProjex.Tests.Unit;

public sealed class LineDiffTests
{
	[Fact]
	public void EmptyAndIdenticalVersionsAreUnchanged()
	{
		Assert.Equal(LineDiffOutcome.Unchanged, LineDiff.Compare("", "", 3).Outcome);
		Assert.Equal(LineDiffOutcome.Unchanged, LineDiff.Compare("a\nb\n", "a\nb\n", 3).Outcome);
	}

	[Fact]
	public void AddingToAnEmptyFileStartsBothRangesAtTheTop()
	{
		var result = LineDiff.Compare("", "a\nb\n", 3);

		Assert.Equal(LineDiffOutcome.Changed, result.Outcome);
		Assert.Equal((2, 0), (result.Added, result.Removed));
		var hunk = Assert.Single(result.Hunks);
		Assert.Equal("@@ -0,0 +1,2 @@\n+a\n+b\n", UnifiedDiffFormatter.FormatHunk(hunk));
	}

	[Fact]
	public void RemovingEveryLineEndsTheNewRangeAtZero()
	{
		var result = LineDiff.Compare("a\nb\n", "", 3);

		Assert.Equal((0, 2), (result.Added, result.Removed));
		Assert.Equal("@@ -1,2 +0,0 @@\n-a\n-b\n", UnifiedDiffFormatter.FormatHunk(Assert.Single(result.Hunks)));
	}

	[Fact]
	public void ReplacingEveryLineRemovesBeforeItAdds()
	{
		var result = LineDiff.Compare("a\nb\n", "c\nd\n", 3);

		Assert.Equal("@@ -1,2 +1,2 @@\n-a\n-b\n+c\n+d\n", UnifiedDiffFormatter.FormatHunk(Assert.Single(result.Hunks)));
	}

	[Fact]
	public void ThreeContextLinesSurroundAChangeLikeGit()
	{
		var result = LineDiff.Compare(Lines(1, 10), Lines(1, 10).Replace("line5\n", "LINE5\n"), 3);

		Assert.Equal(
			"@@ -2,7 +2,7 @@\n line2\n line3\n line4\n-line5\n+LINE5\n line6\n line7\n line8\n",
			UnifiedDiffFormatter.FormatHunk(Assert.Single(result.Hunks)));
	}

	[Fact]
	public void ZeroContextOmitsCountsOfOne()
	{
		var result = LineDiff.Compare(Lines(1, 10), Lines(1, 10).Replace("line5\n", "LINE5\n"), 0);

		Assert.Equal("@@ -5 +5 @@\n-line5\n+LINE5\n", UnifiedDiffFormatter.FormatHunk(Assert.Single(result.Hunks)));
	}

	[Fact]
	public void ZeroContextInsertionNamesTheLineBefore()
	{
		var result = LineDiff.Compare("a\nb\nc\n", "a\nb\nX\nc\n", 0);

		Assert.Equal("@@ -2,0 +3 @@\n+X\n", UnifiedDiffFormatter.FormatHunk(Assert.Single(result.Hunks)));
	}

	[Fact]
	public void TwentyContextLinesAreClampedToTheFile()
	{
		var result = LineDiff.Compare(Lines(1, 10), Lines(1, 10).Replace("line5\n", "LINE5\n"), 20);

		var hunk = Assert.Single(result.Hunks);
		Assert.Equal((1, 10, 1, 10), (hunk.OldStart, hunk.OldLines, hunk.NewStart, hunk.NewLines));
	}

	[Fact]
	public void HunksWhoseContextTouchesAreMerged()
	{
		var old = Lines(1, 20);
		var touching = old.Replace("line2\n", "LINE2\n").Replace("line9\n", "LINE9\n");
		var separate = old.Replace("line2\n", "LINE2\n").Replace("line10\n", "LINE10\n");

		Assert.Single(LineDiff.Compare(old, touching, 3).Hunks);
		var hunks = LineDiff.Compare(old, separate, 3).Hunks;
		Assert.Equal(2, hunks.Count);
		Assert.Equal("@@ -1,5 +1,5 @@", UnifiedDiffFormatter.FormatHeader(hunks[0]));
		Assert.Equal("@@ -7,7 +7,7 @@", UnifiedDiffFormatter.FormatHeader(hunks[1]));
	}

	[Fact]
	public void ALastLineWithoutNewlineCarriesTheMarker()
	{
		var result = LineDiff.Compare("a\nb", "a\nc", 3);

		Assert.Equal(
			"@@ -1,2 +1,2 @@\n a\n-b\n\\ No newline at end of file\n+c\n\\ No newline at end of file\n",
			UnifiedDiffFormatter.FormatHunk(Assert.Single(result.Hunks)));
	}

	[Fact]
	public void AddingTheFinalNewlineChangesTheLastLine()
	{
		var result = LineDiff.Compare("a\nb", "a\nb\n", 3);

		Assert.Equal((1, 1), (result.Added, result.Removed));
		Assert.Equal(
			"@@ -1,2 +1,2 @@\n a\n-b\n\\ No newline at end of file\n+b\n",
			UnifiedDiffFormatter.FormatHunk(Assert.Single(result.Hunks)));
	}

	[Fact]
	public void AnUnchangedUnterminatedLastLineIsContextWithTheMarker()
	{
		var result = LineDiff.Compare("a\nb", "A\nb", 3);

		Assert.Equal(
			"@@ -1,2 +1,2 @@\n-a\n+A\n b\n\\ No newline at end of file\n",
			UnifiedDiffFormatter.FormatHunk(Assert.Single(result.Hunks)));
	}

	[Theory]
	[InlineData("a\r\nb\r\n", "a\nb\n")]
	[InlineData("﻿a\nb\n", "a\nb\n")]
	[InlineData("a\r\nb", "a\nb")]
	public void LineEndingAndByteOrderMarkDifferencesAreNotHunks(string oldText, string newText)
	{
		var result = LineDiff.Compare(oldText, newText, 3);

		Assert.Equal(LineDiffOutcome.LineEndingsOnly, result.Outcome);
		Assert.Empty(result.Hunks);
	}

	[Fact]
	public void ACrlfWorkingFileShowsOnlyTheRealChange()
	{
		var result = LineDiff.Compare("a\nb\nc\n", "a\r\nB\r\nc\r\n", 3);

		Assert.Equal("@@ -1,3 +1,3 @@\n a\n-b\n+B\n c\n", UnifiedDiffFormatter.FormatHunk(Assert.Single(result.Hunks)));
	}

	[Fact]
	public void AnInsertedBlockKeepsItsRemovalsFirst()
	{
		var result = LineDiff.Compare("a\nb\nc\n", "a\nX\nY\nc\n", 3);

		Assert.Equal("@@ -1,3 +1,4 @@\n a\n-b\n+X\n+Y\n c\n", UnifiedDiffFormatter.FormatHunk(Assert.Single(result.Hunks)));
	}

	[Fact]
	public void AVersionAboveTheLineLimitIsTooLarge()
	{
		var oversized = string.Concat(Enumerable.Repeat("x\n", LineDiff.MaximumLines + 1));

		Assert.Equal(LineDiffOutcome.TooLarge, LineDiff.Compare("x\n", oversized, 3).Outcome);
		Assert.Equal(LineDiffOutcome.TooLarge, LineDiff.Compare(oversized, "x\n", 3).Outcome);
	}

	[Fact]
	public void ALargeFileWithOneChangeYieldsOneHunk()
	{
		var old = Lines(1, 40_000);
		var updated = old.Replace("line20000\n", "changed\n");

		var result = LineDiff.Compare(old, updated, 3, TestContext.Current.CancellationToken);

		var hunk = Assert.Single(result.Hunks);
		Assert.Equal("@@ -19997,7 +19997,7 @@", UnifiedDiffFormatter.FormatHeader(hunk));
		Assert.Equal((1, 1), (result.Added, result.Removed));
	}

	[Fact]
	public void AnInterleavedChangeAboveTheEditDistanceLimitIsTooLarge()
	{
		var count = LineDiff.MaximumEditDistance * 2;
		var old = string.Concat(Enumerable.Range(0, count).Select(index => $"x{index}\n"));
		var updated = string.Concat(Enumerable.Range(0, count).Select(index =>
			index % 2 == 0 ? $"x{index}\n" : $"y{index}\n"));

		var result = LineDiff.Compare(old, updated, 3, TestContext.Current.CancellationToken);

		Assert.Equal(LineDiffOutcome.TooLarge, result.Outcome);
		Assert.Empty(result.Hunks);
	}

	[Fact]
	public void AReplacementWithNoCommonLineIsNotLimitedByEditDistance()
	{
		var count = LineDiff.MaximumEditDistance * 2;
		var old = string.Concat(Enumerable.Range(0, count).Select(index => $"a{index}\n"));
		var updated = string.Concat(Enumerable.Range(0, count).Select(index => $"b{index}\n"));

		var result = LineDiff.Compare(old, updated, 3, TestContext.Current.CancellationToken);

		Assert.Equal(LineDiffOutcome.Changed, result.Outcome);
		Assert.Equal((count, count), (result.Added, result.Removed));
	}

	[Fact]
	public void TheEditScriptIsMinimalAndReproducesBothVersions()
	{
		var random = new Random(4659);
		for (var round = 0; round < 400; round++)
		{
			var old = RandomLines(random);
			var updated = RandomLines(random);
			var oldText = string.Concat(old.Select(static line => line + "\n"));
			var newText = string.Concat(updated.Select(static line => line + "\n"));

			var result = LineDiff.Compare(oldText, newText, LineDiff.MaximumContextLines);

			if (oldText == newText)
			{
				Assert.Equal(LineDiffOutcome.Unchanged, result.Outcome);
				continue;
			}
			var expectedEdits = old.Length + updated.Length - 2 * LongestCommonSubsequence(old, updated);
			Assert.Equal(expectedEdits, result.Added + result.Removed);
			var hunk = Assert.Single(result.Hunks);
			Assert.Equal(old, hunk.Lines.Where(static line => line.Kind != UnifiedDiffLineKind.Added).Select(static line => line.Text));
			Assert.Equal(updated, hunk.Lines.Where(static line => line.Kind != UnifiedDiffLineKind.Removed).Select(static line => line.Text));
		}
	}

	[Fact]
	public void MeasureMatchesTheWrittenHunk()
	{
		var result = LineDiff.Compare("a\nb", "a\nc\nd", 1);
		var hunk = Assert.Single(result.Hunks);

		Assert.Equal(UnifiedDiffFormatter.FormatHunk(hunk).Length, UnifiedDiffFormatter.MeasureHunk(hunk));
	}

	[Fact]
	public void CancellationStopsTheComparison()
	{
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();

		Assert.ThrowsAny<OperationCanceledException>(() =>
			LineDiff.Compare(Lines(1, 5000), Lines(2, 5001), 3, cancellation.Token));
	}

	[Fact]
	public void ContextOutsideTheSupportedRangeIsRejected()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => LineDiff.Compare("a", "b", -1));
		Assert.Throws<ArgumentOutOfRangeException>(() => LineDiff.Compare("a", "b", LineDiff.MaximumContextLines + 1));
	}

	private static string Lines(int first, int last) =>
		string.Concat(Enumerable.Range(first, last - first + 1).Select(index => $"line{index}\n"));

	private static string[] RandomLines(Random random) =>
		Enumerable.Range(0, random.Next(0, 24))
			.Select(_ => ((char)('a' + random.Next(0, 4))).ToString())
			.ToArray();

	private static int LongestCommonSubsequence(string[] left, string[] right)
	{
		var lengths = new int[left.Length + 1, right.Length + 1];
		for (var i = 1; i <= left.Length; i++)
		{
			for (var j = 1; j <= right.Length; j++)
			{
				lengths[i, j] = left[i - 1] == right[j - 1]
					? lengths[i - 1, j - 1] + 1
					: Math.Max(lengths[i - 1, j], lengths[i, j - 1]);
			}
		}
		return lengths[left.Length, right.Length];
	}
}

public sealed class DiffTextLinesTests
{
	[Theory]
	[InlineData("", new string[0], true)]
	[InlineData("﻿", new string[0], true)]
	[InlineData("\n", new[] { "" }, true)]
	[InlineData("a", new[] { "a" }, false)]
	[InlineData("a\r\nb", new[] { "a", "b" }, false)]
	[InlineData("a\r\nb\r\n", new[] { "a", "b" }, true)]
	[InlineData("﻿a\n\nb\n", new[] { "a", "", "b" }, true)]
	[InlineData("a\rb\n", new[] { "a\rb" }, true)]
	public void SplitsOnNewlineAndRemovesOneCarriageReturn(string text, string[] expected, bool finalNewline)
	{
		var lines = DiffTextLines.Split(text);

		Assert.Equal(expected, lines.Lines);
		Assert.Equal(finalNewline, lines.HasFinalNewline);
		Assert.Equal(expected.Length, DiffTextLines.CountLines(text));
	}

	[Fact]
	public void OnlyTheLastLineOfAnUnterminatedTextIsUnterminated()
	{
		var lines = DiffTextLines.Split("a\nb");

		Assert.False(lines.IsUnterminated(0));
		Assert.True(lines.IsUnterminated(1));
		Assert.False(DiffTextLines.Split("a\nb\n").IsUnterminated(1));
	}
}
