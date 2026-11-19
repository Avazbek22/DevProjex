namespace DevProjex.Tests.Terminal;

public sealed partial class CliInputContractProcessTests
{
	[Theory]
	[InlineData("--plain=false", "--plain")]
	[InlineData("--plain:false", "--plain")]
	[InlineData("-q=false", "-q")]
	[InlineData("--dry-run=false", "--dry-run")]
	public void InlineValueOnAFlagFailsBeforeAnyOutput(string token, string option)
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);

		var result = RunCli(
			workspace,
			"export", "context", project, "--format", "json", "--git-mode", "none", token);

		AssertUsageFailure(
			result,
			"DPX-CLI-INVALID-SYNTAX",
			$"The option does not accept a value: {option}");
	}

	[Fact]
	public void OnOffToggleStillAcceptsAnInlineValue()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);

		var result = RunCli(
			workspace,
			"export", "context", project, "--format", "json", "--git-mode", "none",
			"--hide-secrets=on", "--compress-code=off", "-o", "-");

		Assert.True(result.ExitCode == 0, result.StandardError);
		using var document = JsonDocument.Parse(result.StandardOutput);
		Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
	}
}
