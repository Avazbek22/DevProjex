using System.Drawing;
using DevProjex.Application.Preview;
using DevProjex.Application.Secrets;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspacePresentationPolicyTests
{
	[Theory]
	[InlineData(TerminalColorMode.Auto, false, false, false)]
	[InlineData(TerminalColorMode.Auto, false, true, true)]
	[InlineData(TerminalColorMode.Always, false, true, false)]
	[InlineData(TerminalColorMode.Never, false, false, true)]
	[InlineData(TerminalColorMode.Always, true, false, true)]
	public void Resolve_UsesExpectedMonochromePolicy(
		TerminalColorMode color,
		bool plain,
		bool noColor,
		bool expectedMonochrome)
	{
		var environment = new TestTerminalEnvironment
		{
			IsNoColor = noColor
		};

		var result = TerminalWorkspacePresentationPolicy.Resolve(
			color,
			plain,
			environment);

		Assert.Equal(expectedMonochrome, result.UseMonochromeScheme);
		Assert.Equal(
			expectedMonochrome
				? TerminalWorkspacePresentationPolicy.MonochromeSchemeName
				: null,
			result.SchemeName);
		Assert.Equal(plain ? LineStyle.None : LineStyle.Single, result.BorderStyle);
		Assert.Equal(!plain, result.AllowMotion);
	}

	[Fact]
	public void PlainTextNormalizationRemovesTerminalDecorations()
	{
		var value = TerminalPlainText.Normalize("↑↓ ←/→ Action · Value… — ready");

		Assert.Equal("j/k h/l Action | Value... - ready", value);
		Assert.DoesNotContain(value, static character =>
			"↑↓←→·…—".Contains(character));
	}

	[Fact]
	public void PlainOverlayButtonsHaveNoTerminalDecorations()
	{
		var button = new Button { Text = "Apply" };

		TerminalWorkspacePresentationPolicy.ConfigureOverlayButton(
			button,
			plain: true);

		Assert.True(button.NoDecorations);
		Assert.True(button.NoPadding);
		Assert.Equal(ShadowStyles.None, button.ShadowStyle);
	}

	[Fact]
	public void PlainPreviewDoesNotCreateUnicodeScrollBars()
	{
		using var view = new TerminalVirtualizedPreviewView(
			useUnicode: false,
			showScrollBars: false);

		Assert.False(view.ViewportSettings.HasFlag(
			ViewportSettingsFlags.HasScrollBars));
	}

	[Fact]
	public void PreviewRedactionNavigation_TogglesOnlyTheActiveOccurrence()
	{
		const string firstOccurrence = "occurrence-a";
		const string secondOccurrence = "occurrence-b";
		using var document = new InMemoryPreviewTextDocument(
			"DEVPROJEX_REDACTED[github-pat#1]\nDEVPROJEX_REDACTED[aws-access-token#1]",
			redactions:
			[
				new PreviewRedactionSpan(
					firstOccurrence,
					"github-pat",
					1,
					0,
					35,
					SecretPreviewSpanState.Redacted),
				new PreviewRedactionSpan(
					secondOccurrence,
					"aws-access-token",
					2,
					0,
					41,
					SecretPreviewSpanState.Redacted)
			]);
		using var view = new TerminalVirtualizedPreviewView();
		var toggled = new List<string>();
		view.RedactionToggleRequested += (_, eventArgs) => toggled.Add(eventArgs.OccurrenceId);
		view.SetDocument(document, preserveViewport: false);

		Assert.True(view.MoveActiveRedaction(reverse: false));
		Assert.True(view.TryToggleActiveRedaction());
		Assert.True(view.MoveActiveRedaction(reverse: false));
		Assert.True(view.TryToggleActiveRedaction());

		Assert.Equal([firstOccurrence, secondOccurrence], toggled);
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, true)]
	public void LocalizedTextUsesAsciiWhenPlainOrUnicodeIsUnavailable(
		bool plain,
		bool supportsUnicode)
	{
		var value = TerminalWorkspaceSession.NormalizeLocalizedText(
			"↑/↓ Action…",
			plain,
			supportsUnicode);

		Assert.Equal("k/j Action...", value);
	}

	[Fact]
	public void PreviewNavigationProjectsWidePrefixesToTerminalColumns()
	{
		const string occurrence = "wide-prefix";
		using var document = new InMemoryPreviewTextDocument(
			"界界DEVPROJEX_REDACTED[secret#1]",
			redactions:
			[
				new PreviewRedactionSpan(
					occurrence,
					"secret",
					1,
					2,
					30,
					SecretPreviewSpanState.Redacted)
			]);
		using var view = new TerminalVirtualizedPreviewView();
		view.SetDocument(document, preserveViewport: false);

		Assert.Equal(4, view.GetDisplayColumn(0, 2));
		Assert.True(view.MoveActiveRedaction(reverse: false));

		Assert.Equal(2, view.HorizontalOffset);
		Assert.True(view.MaxLineLength > document.MaxLineLength);
	}

	[Fact]
	public void RepeatedPreviewLayout_ReusesDecodedDocumentLine()
	{
		using var document = new CountingPreviewTextDocument("界ABC");
		using var view = new TerminalVirtualizedPreviewView(showScrollBars: false);
		view.SetDocument(document, preserveViewport: false);

		for (var iteration = 0; iteration < 100; iteration++)
			Assert.Equal(5, view.GetDisplayColumn(0, 4));

		Assert.Equal(1, document.LineReadCount);

		using var replacement = new CountingPreviewTextDocument("日Z");
		view.SetDocument(replacement, preserveViewport: true);
		Assert.Equal(3, view.GetDisplayColumn(0, 2));
		Assert.Equal(1, replacement.LineReadCount);
		Assert.Equal(1, document.LineReadCount);
	}

	[Fact]
	public void VisiblePreviewLinesUseOneRangeVisitAndRemainCached()
	{
		using var document = new CountingRangePreviewTextDocument(40);
		using var view = new TerminalVirtualizedPreviewView(showScrollBars: false)
		{
			Frame = new Rectangle(0, 0, 80, 20)
		};
		view.SetDocument(document, preserveViewport: false);

		view.PrimeVisibleLineCache();
		for (var lineNumber = 1; lineNumber <= 20; lineNumber++)
			Assert.Equal($"line-{lineNumber}", view.GetDisplayLine(lineNumber));
		view.PrimeVisibleLineCache();

		Assert.Equal(1, document.RangeVisitCount);
		Assert.Equal(0, document.LineReadCount);
	}

	[Theory]
	[InlineData("界AB", 1, 2, " A")]
	[InlineData("界AB", 2, 2, "AB")]
	[InlineData("e\u0301x", 0, 2, "e\u0301x")]
	public void PreviewHorizontalSlicePreservesTerminalColumns(
		string value,
		int startColumn,
		int width,
		string expected)
	{
		Assert.Equal(expected, TerminalVirtualizedPreviewView.SliceColumns(
			value,
			startColumn,
			width));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void PreviewScrollRaisesOneVisibleRangeChange(bool showScrollBars)
	{
		using var document = new InMemoryPreviewTextDocument("first\nsecond\nthird");
		using var view = new TerminalVirtualizedPreviewView(showScrollBars: showScrollBars)
		{
			Frame = new Rectangle(0, 0, 20, 1)
		};
		view.SetDocument(document, preserveViewport: false);
		var notifications = 0;
		view.VisibleRangeChanged += (_, _) => notifications++;

		view.ScrollTo(1, 0);

		Assert.Equal(1, notifications);
	}

	private sealed class CountingPreviewTextDocument(string line) : IPreviewTextDocument
	{
		public int LineReadCount { get; private set; }
		public int LineCount => 1;
		public int MaxLineLength => line.Length;
		public long CharacterCount => line.Length;
		public IReadOnlyList<PreviewDocumentSection> Sections => [];
		public IReadOnlyList<PreviewRedactionSpan> Redactions => [];
		public string GetFullText() => line;
		public string GetLineText(int lineNumber)
		{
			LineReadCount++;
			return line;
		}

		public string GetLineRangeText(int firstLine, int lastLine) => line;
		public ValueTask WriteToAsync(Stream destination, CancellationToken cancellationToken = default) =>
			ValueTask.CompletedTask;
		public void Dispose()
		{
		}
	}

	private sealed class CountingRangePreviewTextDocument(int lineCount) : IPreviewTextDocument
	{
		public int RangeVisitCount { get; private set; }
		public int LineReadCount { get; private set; }
		public int LineCount => lineCount;
		public int MaxLineLength => 16;
		public long CharacterCount => lineCount * 8L;
		public IReadOnlyList<PreviewDocumentSection> Sections => [];
		public string GetFullText() => string.Empty;
		public string GetLineText(int lineNumber)
		{
			LineReadCount++;
			return $"line-{lineNumber}";
		}
		public string GetLineRangeText(int firstLine, int lastLine) => string.Empty;
		public void VisitLines(
			int firstLine,
			int lastLine,
			PreviewTextLineVisitor visitor,
			CancellationToken cancellationToken = default)
		{
			RangeVisitCount++;
			for (var lineNumber = firstLine; lineNumber <= lastLine; lineNumber++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!visitor(lineNumber, $"line-{lineNumber}"))
					break;
			}
		}
		public ValueTask WriteToAsync(Stream destination, CancellationToken cancellationToken = default) =>
			ValueTask.CompletedTask;
		public void Dispose()
		{
		}
	}

	[Fact]
	public void PreviewWordWrapUsesVisualRowsAndRestoresHorizontalGeometry()
	{
		using var document = new InMemoryPreviewTextDocument(
			"0123456789\nabcdefghij\nklmnopqrst");
		using var view = new TerminalVirtualizedPreviewView(showScrollBars: false)
		{
			Frame = new Rectangle(0, 0, 5, 2)
		};
		view.SetDocument(document, preserveViewport: false);
		var maximumWidth = view.MaxLineLength;

		Assert.True(view.ToggleWordWrap());
		Assert.Equal(6, view.ContentRowCount);
		Assert.Equal(6, view.GetContentSize().Height);
		Assert.True(view.HasVerticalOverflow);
		Assert.False(view.HasHorizontalOverflow);

		view.ScrollToContentRow(view.ContentRowCount - 1, 0);

		Assert.Equal(4, view.FirstVisibleContentRow);
		Assert.Equal(2, view.FirstVisibleLine);
		Assert.Equal(3, view.VisibleLastLine);

		Assert.False(view.ToggleWordWrap());
		Assert.Equal(maximumWidth, view.MaxLineLength);
		Assert.Equal(document.LineCount, view.ContentRowCount);
		Assert.Equal(maximumWidth, view.GetContentSize().Width);
		Assert.True(view.HasHorizontalOverflow);
	}

	[Fact]
	public void PreviewWordWrapMapsSearchNavigationToWrappedCoordinates()
	{
		using var document = new InMemoryPreviewTextDocument("012345secret-tail");
		using var view = new TerminalVirtualizedPreviewView(showScrollBars: false)
		{
			Frame = new Rectangle(0, 0, 5, 1)
		};
		view.SetDocument(document, preserveViewport: false);
		view.ToggleWordWrap();

		var match = Assert.NotNull(view.SetSearchQuery("secret", 0, -1));
		view.ScrollTo(match.Line, view.GetDisplayColumn(match.Line, match.Column));

		var position = view.ResolveDocumentPosition(view.FirstVisibleContentRow);
		Assert.Equal(0, position.Line);
		Assert.Equal(5, position.DisplayColumn);
		Assert.InRange(match.Column, position.DisplayColumn, position.DisplayColumn + view.VisibleTextWidth);
	}
}
