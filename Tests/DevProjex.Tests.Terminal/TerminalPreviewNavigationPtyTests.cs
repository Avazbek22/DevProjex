using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed partial class TerminalPreviewNavigationPtyTests
{
	[Fact(Timeout = 90_000)]
	public async Task PreviewViewportAndFocusSurviveKeyboardNavigationOverlaysAndResize()
	{
		using var project = CreateScrollableProject();
		await using var terminal = await TerminalPtyHarness.StartAsync(
			project.Path,
			[
				"tui",
				project.Path,
				"--profile",
				"standard",
				"--screen",
				"inline",
				"--no-mouse",
				"--language",
				"en"
			],
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		var initial = await terminal.WaitForScreenAsync(
			"CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		initial = await terminal.WaitForScreenAsync(
			"Ctrl+A/U Select all/none",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("> PROJECT TREE", initial, StringComparison.Ordinal);
		Assert.Contains("Ctrl+A/U Select all/none", initial, StringComparison.Ordinal);
		await terminal.SendAsync("2", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"ContentMarker001",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		var focusedPreview = await terminal.WaitForScreenAsync(
			"j/k Scroll",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("> CONTEXT PREVIEW", focusedPreview, StringComparison.Ordinal);
		Assert.Contains("j/k Scroll", focusedPreview, StringComparison.Ordinal);
		await terminal.SendAsync("/", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Find text across the complete context:",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("ContentMarker", TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var firstSearchMatch = await terminal.WaitForScreenAsync(
			"1/60",
			cancellationToken: TestContext.Current.CancellationToken);
		var firstSearchMarker = GetMaximumVisibleMarker(firstSearchMatch);
		await terminal.SendAsync("nnnnnnnn", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"9/60",
			cancellationToken: TestContext.Current.CancellationToken);
		var nextSearchMatch = await WaitForStableScreenAsync(
			terminal,
			TestContext.Current.CancellationToken);
		Assert.Contains("9/60", nextSearchMatch, StringComparison.Ordinal);
		Assert.True(
			GetMaximumVisibleMarker(nextSearchMatch) > firstSearchMarker,
			"Preview Search next did not move the visible viewport.");
		await terminal.SendAsync("N", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"8/60",
			cancellationToken: TestContext.Current.CancellationToken);
		var previousSearchMatch = await WaitForStableScreenAsync(
			terminal,
			TestContext.Current.CancellationToken);
		Assert.Contains("8/60", previousSearchMatch, StringComparison.Ordinal);
		Assert.True(
			GetMaximumVisibleMarker(previousSearchMatch) <=
			GetMaximumVisibleMarker(nextSearchMatch),
			"Preview Search previous did not move back through the document.");
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"/ContentMarker",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		var before = await WaitForStableScreenAsync(
			terminal,
			TestContext.Current.CancellationToken);
		await terminal.SendAsync("j", TestContext.Current.CancellationToken);
		var afterLine = await WaitForScreenChangeAsync(
			terminal,
			before,
			TestContext.Current.CancellationToken);
		Assert.NotEqual(before, afterLine);
		Assert.Contains("> CONTEXT PREVIEW", afterLine, StringComparison.Ordinal);

		await terminal.SendPageDownAsync(TestContext.Current.CancellationToken);
		var afterPage = await WaitForScreenChangeAsync(
			terminal,
			afterLine,
			TestContext.Current.CancellationToken);
		Assert.NotEqual(afterLine, afterPage);
		Assert.True(
			GetMaximumVisibleMarker(afterPage) > GetMaximumVisibleMarker(afterLine),
			$"Page Down did not advance the visible preview content.\nBefore:\n{afterLine}\nAfter:\n{afterPage}");

		await terminal.SendEndAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"ContentMarker060",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendShiftTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendF6Async(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendShiftF6Async(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendF6Async(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);

		var beforeHome = terminal.CaptureScreen();
		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		var atStart = await WaitForScreenChangeAsync(
			terminal,
			beforeHome,
			TestContext.Current.CancellationToken);
		await terminal.SendPageDownAsync(TestContext.Current.CancellationToken);
		var beforeHelp = await WaitForScreenChangeAsync(
			terminal,
			atStart,
			TestContext.Current.CancellationToken);
		var markersBeforeHelp = GetVisibleMarkers(beforeHelp);
		await terminal.SendAsync("?", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Parameters; Shift+Tab/Shift+F6",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Parameters; Shift+Tab/Shift+F6",
			cancellationToken: TestContext.Current.CancellationToken);
		var afterHelp = await WaitForStableScreenAsync(
			terminal,
			TestContext.Current.CancellationToken);
		Assert.Contains("> CONTEXT PREVIEW", afterHelp, StringComparison.Ordinal);
		Assert.Equal(markersBeforeHelp, GetVisibleMarkers(afterHelp));

		await terminal.SendAsync("M", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"DPX-GIT-TRACKED-INDEX-UNAVAILABLE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"[x] Use .gitignore",
			cancellationToken: TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(
			terminal,
			TestContext.Current.CancellationToken);
		await terminal.SendShiftTabAsync(TestContext.Current.CancellationToken);
		var afterGitRefresh = await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("> CONTEXT PREVIEW", afterGitRefresh, StringComparison.Ordinal);
		await WaitForStableScreenAsync(
			terminal,
			TestContext.Current.CancellationToken);
		await terminal.SendAsync("x", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"[x] Use .gitignore",
			cancellationToken: TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(
			terminal,
			TestContext.Current.CancellationToken);
		await terminal.SendShiftTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync("x", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PARAMETERS",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		await terminal.SendSpaceAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"[x] All",
			cancellationToken: TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(
			terminal,
			TestContext.Current.CancellationToken);
		await terminal.SendShiftTabAsync(TestContext.Current.CancellationToken);
		var afterExclusionsRefresh = await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("> CONTEXT PREVIEW", afterExclusionsRefresh, StringComparison.Ordinal);
		await WaitForStableScreenAsync(
			terminal,
			TestContext.Current.CancellationToken);

		await terminal.SendAsync("A", TestContext.Current.CancellationToken);
		var analysis = await terminal.WaitForScreenAsync(
			"Estimated tokens:",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("Files:", analysis, StringComparison.Ordinal);
		Assert.DoesNotContain("Fingerprint", analysis, StringComparison.Ordinal);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Files:",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.ResizeAsync(80, 24, TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		var compact = await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("> CONTEXT PREVIEW", compact, StringComparison.Ordinal);
		Assert.DoesNotContain("> PROJECT TREE", compact, StringComparison.Ordinal);
		await terminal.SendAsync("j", TestContext.Current.CancellationToken);
		var compactScrolled = await WaitForScreenChangeAsync(
			terminal,
			compact,
			TestContext.Current.CancellationToken);
		Assert.Contains("> CONTEXT PREVIEW", compactScrolled, StringComparison.Ordinal);

		await terminal.ResizeAsync(120, 30, TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"W Wrap",
			cancellationToken: TestContext.Current.CancellationToken);
		var restored = await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("> CONTEXT PREVIEW", restored, StringComparison.Ordinal);
		await terminal.SendShiftF6Async(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);

		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	private static int GetMaximumVisibleMarker(string screen)
	{
		var markers = GetVisibleMarkers(screen);
		return markers.Count == 0 ? 0 : markers.Max();
	}

	private static IReadOnlyList<int> GetVisibleMarkers(string screen) =>
		ContentMarkerPattern()
			.Matches(screen)
			.Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
			.Distinct()
			.Order()
			.ToArray();

	private static TemporaryDirectory CreateScrollableProject()
	{
		var project = new TemporaryDirectory();
		project.WriteFile("global.json", "{}");
		for (var index = 1; index <= 60; index++)
		{
			project.WriteFile(
				$"src/Feature{index:D3}.cs",
				$"internal sealed class ContentMarker{index:D3} {{ }}");
		}
		project.WriteFile("node_modules/noise.js", "generated dependency noise");

		return project;
	}

	private static async Task<string> WaitForScreenChangeAsync(
		TerminalPtyHarness terminal,
		string previous,
		CancellationToken cancellationToken)
	{
		var timeout = Stopwatch.StartNew();
		while (timeout.Elapsed < TimeSpan.FromSeconds(5))
		{
			var current = terminal.CaptureScreen();
			if (!string.Equals(previous, current, StringComparison.Ordinal))
			{
				await Task.Delay(120, cancellationToken);
				var settled = terminal.CaptureScreen();
				await Task.Delay(60, cancellationToken);
				if (string.Equals(settled, terminal.CaptureScreen(), StringComparison.Ordinal))
					return settled;
			}
			await Task.Delay(40, cancellationToken);
		}

		throw new Xunit.Sdk.XunitException(
			$"Terminal viewport did not change.\n{terminal.CaptureScreen()}");
	}

	private static async Task<string> WaitForStableScreenAsync(
		TerminalPtyHarness terminal,
		CancellationToken cancellationToken)
	{
		var timeout = Stopwatch.StartNew();
		var stable = Stopwatch.StartNew();
		var previous = terminal.CaptureScreen();
		while (timeout.Elapsed < TimeSpan.FromSeconds(10))
		{
			await Task.Delay(75, cancellationToken);
			var current = terminal.CaptureScreen();
			if (!string.Equals(previous, current, StringComparison.Ordinal))
			{
				previous = current;
				stable.Restart();
				continue;
			}
			if (stable.Elapsed >= TimeSpan.FromMilliseconds(375))
				return current;
		}

		throw new Xunit.Sdk.XunitException(
			$"Terminal screen did not settle.\n{terminal.CaptureScreen()}");
	}

	[GeneratedRegex(@"ContentMarker(\d{3})")]
	private static partial Regex ContentMarkerPattern();
}
