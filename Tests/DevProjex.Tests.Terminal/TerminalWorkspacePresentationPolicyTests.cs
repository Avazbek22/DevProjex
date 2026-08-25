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
}
