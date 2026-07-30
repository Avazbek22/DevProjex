using System.CommandLine;
using Terminal.Gui.Text;

namespace DevProjex.Tests.Terminal;

public sealed class HelpReleaseRegressionTests
{
	[Fact]
	public async Task AnalyzeHelpDescribesValuesDefaultsAndRepeatability()
	{
		var help = await RenderHelpAsync(80, "en", "analyze");

		Assert.Contains("--format <text|json>", help, StringComparison.Ordinal);
		Assert.Contains("Default: text.", help, StringComparison.Ordinal);
		Assert.Contains("--root <PATH>", help, StringComparison.Ordinal);
		Assert.Contains("Repeatable.", help, StringComparison.Ordinal);
		Assert.Contains("--language <en|ru|de|fr|it|es|pt|pt-pt|kk|tg|uz>", help, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ProjectExportHelpMarksRequiredChoiceAndDestination()
	{
		var help = await RenderHelpAsync(80, "en", "export", "project");

		Assert.Contains("--as <folder|zip>", help, StringComparison.Ordinal);
		Assert.Contains("Required.", help, StringComparison.Ordinal);
		Assert.Contains("-o, --output <PATH>", help, StringComparison.Ordinal);
	}

	[Fact]
	public async Task HelpShowsEffectiveInteractiveDefaultsAndProfileScopes()
	{
		var tui = await RenderHelpAsync(120, "en", "tui");
		var open = await RenderHelpAsync(120, "en", "open");
		var analyze = await RenderHelpAsync(120, "en", "analyze");

		Assert.Contains(
			"--profile <auto|standard|local|FILE>",
			tui,
			StringComparison.Ordinal);
		Assert.Contains(
			"Terminal screen mode. Default: stored setting, then auto.",
			tui,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"Terminal screen mode. Default: stored setting, then auto. Default: auto.",
			tui,
			StringComparison.Ordinal);
		Assert.Contains(
			"--profile <auto|standard|local|FILE>",
			open,
			StringComparison.Ordinal);
		Assert.Contains(
			"--profile <standard|local|FILE>",
			analyze,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"--profile <auto|standard|local|FILE>",
			analyze,
			StringComparison.Ordinal);
	}

	[Fact]
	public void CellWidthWrappingPreservesCjkCombiningAndEmojiTextElements()
	{
		const string text = "A界e\u0301🙂B";

		var lines = TerminalCellWidth.Wrap(text, 3);

		Assert.Equal(text, string.Concat(lines));
		Assert.All(lines, static line => Assert.True(line.GetColumns() <= 3));
	}

	[Fact]
	public async Task EveryPublicHelpFitsTerminalCellWidthAcrossLocales()
	{
		string[] languages =
		[
			"en", "ru", "de", "fr", "it", "es", "pt", "pt-pt", "kk", "tg", "uz"
		];
		int[] widths = [60, 80, 120];
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		foreach (var language in languages)
		{
			foreach (var width in widths)
			{
				foreach (var path in EnumeratePublicCommandPaths(root))
				{
					var help = await RenderHelpAsync(width, language, path.ToArray());
					Assert.All(
						help.Split(Environment.NewLine),
						line => Assert.True(
							line.GetColumns() <= width,
							$"{language} help exceeds {width} cells for " +
							$"`devprojex {string.Join(' ', path)}`: {line}"));
					Assert.DoesNotContain("<VALUE>", help, StringComparison.Ordinal);
					Assert.DoesNotContain("[[", help, StringComparison.Ordinal);
				}
			}
		}
	}

	private static async Task<string> RenderHelpAsync(
		int width,
		string language,
		params string[] commandPath)
	{
		var environment = new TestTerminalEnvironment { Width = width };
		var arguments = commandPath
			.Concat(["--language", language, "--help"])
			.ToArray();
		var exitCode = await new TerminalApplication(environment).RunAsync(
			arguments,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardError);
		Assert.DoesNotContain("\u001b", environment.StandardOutput, StringComparison.Ordinal);
		return environment.StandardOutput;
	}

	private static IEnumerable<IReadOnlyList<string>> EnumeratePublicCommandPaths(
		RootCommand root)
	{
		yield return [];
		var stack = new Stack<(Command Command, IReadOnlyList<string> Path)>();
		foreach (var child in root.Subcommands.Where(static command => !command.Hidden))
			stack.Push((child, [child.Name]));
		while (stack.Count > 0)
		{
			var (command, path) = stack.Pop();
			yield return path;
			foreach (var child in command.Subcommands.Where(static child => !child.Hidden))
				stack.Push((child, [.. path, child.Name]));
		}
	}
}
