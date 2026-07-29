namespace DevProjex.Tests.Terminal;

public sealed class CompletionCommandContractTests
{
	[Theory]
	[InlineData("bash", "complete -F _devprojex_complete devprojex")]
	[InlineData("zsh", "#compdef devprojex")]
	[InlineData("fish", "complete -c devprojex")]
	[InlineData("powershell", "Register-ArgumentCompleter -Native -CommandName devprojex")]
	public void ScriptsAreGeneratedFromThePublicCommandTree(
		string shell,
		string shellMarker)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();

		var script = CompletionScriptGenerator.Generate(root, shell);

		Assert.Contains(shellMarker, script, StringComparison.Ordinal);
		foreach (var command in new[]
		         {
			         "analyze",
			         "completion",
			         "doctor",
			         "export",
			         "open",
			         "profile",
			         "tui",
			         "ui"
		         })
		{
			Assert.Contains(command, script, StringComparison.Ordinal);
		}

		Assert.Contains("--git-mode", script, StringComparison.Ordinal);
		Assert.Contains("--exclude", script, StringComparison.Ordinal);
		Assert.Contains("-v", script, StringComparison.Ordinal);
		Assert.DoesNotContain("--no-ui", script, StringComparison.Ordinal);
		Assert.DoesNotContain("--report", script, StringComparison.Ordinal);
		Assert.DoesNotContain("--copy", script, StringComparison.Ordinal);
		Assert.DoesNotContain("benchmark", script, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("bash")]
	[InlineData("zsh")]
	[InlineData("fish")]
	[InlineData("powershell")]
	public async Task CompletionCommandWritesOnlyTheRequestedScript(string shell)
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["completion", shell],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.NotEmpty(environment.StandardOutput);
		Assert.Empty(environment.StandardError);
		Assert.DoesNotContain("\u001b", environment.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public void UnsupportedShellIsRejectedByTheProductionParser()
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();

		var result = root.Parse(["completion", "cmd"]);

		Assert.NotEmpty(result.Errors);
	}
}
