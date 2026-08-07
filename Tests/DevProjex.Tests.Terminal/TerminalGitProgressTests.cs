namespace DevProjex.Tests.Terminal;

public sealed class TerminalGitProgressTests
{
	[Theory]
	[InlineData(
		"Receiving objects: 42% (42/100), 1.00 MiB | 2.00 MiB/s",
		"Terminal.Tui.Clone.ReceivingObjects",
		42)]
	[InlineData(
		"Resolving deltas: 73% (73/100)",
		"Terminal.Tui.Clone.ResolvingDeltas",
		73)]
	[InlineData(
		"Checking out files: 90% (90/100)",
		"Terminal.Tui.Clone.CheckingOut",
		90)]
	public void ParserUsesOnlyMeasuredGitPercentages(
		string line,
		string expectedPhase,
		int expectedPercent)
	{
		var result = TerminalGitProgressParser.Parse(
			line,
			"Terminal.Tui.Clone.Connecting",
			currentPercent: null);

		Assert.Equal(expectedPhase, result.PhaseKey);
		Assert.Equal(expectedPercent, result.Percent);
		Assert.Equal(line, result.Detail);
	}

	[Fact]
	public void StandalonePercentagePreservesCurrentPhase()
	{
		var result = TerminalGitProgressParser.Parse(
			"55%",
			"Terminal.Tui.Clone.ReceivingObjects",
			42);

		Assert.Equal("Terminal.Tui.Clone.ReceivingObjects", result.PhaseKey);
		Assert.Equal(55, result.Percent);
		Assert.Empty(result.Detail);
	}

	[Theory]
	[InlineData("fatal: could not read https:" + "//user:token@example.com/repository.git")]
	[InlineData("Authorization: Bearer secret")]
	public void ParserNeverRendersCredentialBearingDetails(string line)
	{
		var result = TerminalGitProgressParser.Parse(
			line,
			"Terminal.Tui.Clone.Connecting",
			currentPercent: null);

		Assert.Empty(result.Detail);
		Assert.DoesNotContain("secret", result.Detail, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("token", result.Detail, StringComparison.OrdinalIgnoreCase);
	}
}
