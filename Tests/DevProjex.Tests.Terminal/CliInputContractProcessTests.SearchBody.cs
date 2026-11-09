namespace DevProjex.Tests.Terminal;

public sealed partial class CliInputContractProcessTests
{
	[Theory]
	[InlineData("OFF")]
	[InlineData("Off")]
	public void SearchAcceptsOffInAnyCase(string value)
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);

		var result = RunCli(
			workspace,
			"search", "App", project, "--symbols", "--format", "json",
			"--git-mode", "none", "--search-body-chars", value);

		Assert.True(result.ExitCode == 0, result.StandardError);
		Assert.NotEmpty(ReadMatches(result.StandardOutput));
	}
}
