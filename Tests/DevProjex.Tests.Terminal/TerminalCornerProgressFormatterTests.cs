using Terminal.Gui.Text;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalCornerProgressFormatterTests
{
	[Fact]
	public void VisibilityStartsAtTheDelayBoundaryAndNeverInTooSmallMode()
	{
		var before = TerminalCornerProgressFormatter.ShowDelay - TimeSpan.FromTicks(1);

		Assert.False(TerminalCornerProgressFormatter.ShouldShow(true, before, tooSmall: false));
		Assert.True(TerminalCornerProgressFormatter.ShouldShow(
			true,
			TerminalCornerProgressFormatter.ShowDelay,
			tooSmall: false));
		Assert.False(TerminalCornerProgressFormatter.ShouldShow(
			true,
			TerminalCornerProgressFormatter.ShowDelay,
			tooSmall: true));
		Assert.False(TerminalCornerProgressFormatter.ShouldShow(
			false,
			TimeSpan.FromSeconds(1),
			tooSmall: false));
	}

	[Theory]
	[InlineData(false, true, null, "⠋ Обновление…")]
	[InlineData(false, true, 0.42, "⠋ Обновление… 42%")]
	[InlineData(true, true, 0.42, "Обновление… 42%")]
	[InlineData(false, false, null, "| Updating...")]
	public void FormatHandlesSpinnerPercentageAndPlainMode(
		bool plain,
		bool useUnicode,
		double? fraction,
		string expected)
	{
		var result = TerminalCornerProgressFormatter.Format(
			useUnicode ? "Обновление…" : "Updating...",
			fraction,
			spinnerFrame: 0,
			maximumColumns: TerminalCornerProgressView.MaximumWidth,
			plain,
			useUnicode);

		Assert.Equal(expected, result);
	}

	[Fact]
	public void FormatPreservesPercentageWhileTruncatingByDisplayColumns()
	{
		var result = TerminalCornerProgressFormatter.Format(
			"非常に長い更新処理のラベル",
			0.75,
			spinnerFrame: 3,
			maximumColumns: 18,
			plain: false,
			useUnicode: true);

		Assert.EndsWith(" 75%", result, StringComparison.Ordinal);
		Assert.True(result.GetColumns() <= 18);
		Assert.Contains('…', result);
	}
}
