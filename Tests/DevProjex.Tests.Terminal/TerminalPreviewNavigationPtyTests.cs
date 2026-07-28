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
		Assert.Contains("> PROJECT TREE", initial, StringComparison.Ordinal);
		Assert.Contains("Tab/F6 Preview", initial, StringComparison.Ordinal);
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
		var before = focusedPreview;
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
			"Tab or Shift+Tab",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Tab or Shift+Tab",
			cancellationToken: TestContext.Current.CancellationToken);
		var afterHelp = await WaitForStableScreenAsync(
			terminal,
			TestContext.Current.CancellationToken);
		Assert.Contains("> CONTEXT PREVIEW", afterHelp, StringComparison.Ordinal);
		Assert.Equal(markersBeforeHelp, GetVisibleMarkers(afterHelp));

		await terminal.SendAsync("M", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Choose exactly one mode",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Choose exactly one mode",
			cancellationToken: TestContext.Current.CancellationToken);
		var afterGitRefresh = await terminal.WaitForScreenAsync(
			"Git filtering: No Git filtering",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("> CONTEXT PREVIEW", afterGitRefresh, StringComparison.Ordinal);
		await WaitForStableScreenAsync(
			terminal,
			TestContext.Current.CancellationToken);

		await terminal.SendAsync("x", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Toggle all changes only this section",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendSpaceAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Toggle all changes only this section",
			cancellationToken: TestContext.Current.CancellationToken);
		var afterExclusionsRefresh = await terminal.WaitForScreenAsync(
			"Files 62",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("> CONTEXT PREVIEW", afterExclusionsRefresh, StringComparison.Ordinal);
		Assert.Contains("No Git filtering", afterExclusionsRefresh, StringComparison.Ordinal);
		await WaitForStableScreenAsync(
			terminal,
			TestContext.Current.CancellationToken);

		await terminal.SendAsync("A", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Fingerprint",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Fingerprint",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.ResizeAsync(80, 24, TestContext.Current.CancellationToken);
		var compact = await terminal.WaitForScreenAsync(
			"Tab/F6 Tree   ? Help",
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
			"1/2/3 View",
			cancellationToken: TestContext.Current.CancellationToken);
		var restored = await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("PROJECT TREE", restored, StringComparison.Ordinal);
		await terminal.SendShiftF6Async(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);

		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
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
