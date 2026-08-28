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
		Assert.Contains("--strip-blank-lines", help, StringComparison.Ordinal);
		Assert.Contains("Remove blank lines from supported source files.", help, StringComparison.Ordinal);
		Assert.Contains("--language", help, StringComparison.Ordinal);
		Assert.Contains("--language <CODE>", help, StringComparison.Ordinal);
		Assert.Equal(
			2,
			help.Split("Enables secret detection automatically.", StringSplitOptions.None).Length - 1);
	}

	[Fact]
	public async Task ProjectExportHelpMarksRequiredChoiceAndDestination()
	{
		var help = await RenderHelpAsync(80, "en", "export", "project");

		Assert.Contains("--as <folder|zip>", help, StringComparison.Ordinal);
		Assert.Contains("Required.", help, StringComparison.Ordinal);
		Assert.Contains("-o, --output <PATH>", help, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("remove")]
	[InlineData("clear")]
	public async Task CacheCleanupHelpDoesNotMarkForceRequiredWhenDryRunIsAvailable(
		string action)
	{
		var help = await RenderHelpAsync(120, "en", "cache", action);

		Assert.Contains("--force", help, StringComparison.Ordinal);
		Assert.Contains("--dry-run", help, StringComparison.Ordinal);
		Assert.DoesNotContain("Required.", help, StringComparison.Ordinal);
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

	[Theory]
	[InlineData("profile show", "devprojex profile show .")]
	[InlineData("profile export", "devprojex profile export . --profile standard -o ../devprojex-profile.json")]
	[InlineData("profile import", "devprojex profile import ../devprojex-profile.json .")]
	[InlineData("profile validate", "devprojex profile validate ../devprojex-profile.json")]
	[InlineData("profile reset", "devprojex profile reset .")]
	[InlineData("profile save", "devprojex profile save . --root src --extension .cs")]
	[InlineData("cache path", "devprojex cache path")]
	[InlineData("cache list", "devprojex cache list --format json")]
	[InlineData("cache remove", "devprojex cache remove https://github.com/owner/repo --force")]
	[InlineData("cache clear", "devprojex cache clear --force")]
	[InlineData("cache update", "devprojex cache update https://github.com/owner/repo")]
	[InlineData("ui list", "devprojex ui list --format json")]
	[InlineData("ui status", "devprojex ui status --project .")]
	[InlineData("ui activate", "devprojex ui activate --project .")]
	[InlineData("ui preview open", "devprojex ui preview open --view tree-content --project .")]
	[InlineData("ui preview close", "devprojex ui preview close --project .")]
	[InlineData("ui preview set-view", "devprojex ui preview set-view tree-content --project .")]
	[InlineData("ui tree set-format", "devprojex ui tree set-format json --project .")]
	[InlineData("ui filter set", "devprojex ui filter set Program --project .")]
	[InlineData("ui filter clear", "devprojex ui filter clear --project .")]
	[InlineData("ui search set", "devprojex ui search set TODO --project .")]
	[InlineData("ui search next", "devprojex ui search next --project .")]
	[InlineData("ui search previous", "devprojex ui search previous --project .")]
	[InlineData("ui search clear", "devprojex ui search clear --project .")]
	public async Task PolishedLeafHelpUsesRealExamples(
		string commandPath,
		string expectedExample)
	{
		var path = commandPath.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		var help = await RenderHelpAsync(120, "en", path);

		Assert.Contains(expectedExample, help, StringComparison.Ordinal);
		Assert.DoesNotContain(
			$"devprojex {commandPath} --help",
			help,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task ProfileParentHelpShowsExportBeforePortableProfileUse()
	{
		var help = await RenderHelpAsync(120, "en", "profile");
		var export = help.IndexOf(
			"devprojex profile export . --profile standard -o ../devprojex-profile.json",
			StringComparison.Ordinal);
		var validate = help.IndexOf(
			"devprojex profile validate ../devprojex-profile.json",
			StringComparison.Ordinal);
		var show = help.IndexOf(
			"devprojex profile show . --profile ../devprojex-profile.json --format json",
			StringComparison.Ordinal);

		Assert.True(export >= 0, "The profile export example is missing.");
		Assert.True(validate > export, "Profile validation must follow profile export.");
		Assert.True(show > validate, "Portable profile use must follow profile export and validation.");
	}

	[Theory]
	[InlineData("recent", "0,1,2,130")]
	[InlineData("cache path", "0,1,2,130")]
	[InlineData("cache list", "0,1,2,3,130")]
	[InlineData("profile validate", "0,1,2,130")]
	[InlineData("profile export", "0,1,2,3,4,130")]
	[InlineData("ui list", "0,1,2,5,130")]
	[InlineData("ui status", "0,1,2,3,4,5,130")]
	public void LeafHelpListsOnlyReachableExitCodes(string commandPath, string expectedCodes)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var command = commandPath
			.Split(' ', StringSplitOptions.RemoveEmptyEntries)
			.Aggregate(
				(Command)root,
				static (parent, name) => parent.Subcommands.Single(child => child.Name == name));

		Assert.Equal(
			expectedCodes.Split(',').Select(int.Parse),
			CommandHelpRenderer.ResolveExitCodes(command));
	}

	[Fact]
	public void RootHelpRetainsTheCompleteExitCodeTable()
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();

		Assert.Equal([0, 1, 2, 3, 4, 5, 130], CommandHelpRenderer.ResolveExitCodes(root));
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
		int[] widths = [60, 80, 120];
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		foreach (var language in CliChoiceSets.Language.Tokens)
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
