namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspaceCommandResultTests
{
	[Fact]
	public void NormalizeCommandResultPreservesMetricsAndEscapesTerminalControls()
	{
		var result = TerminalWorkspaceSession.NormalizeCommandResult(
			"Characters: 12\r\nTokens: 3\n\rforged\tsegment\u001b]8;;https://example.invalid\u0007");

		Assert.Equal(
			"Characters: 12 · Tokens: 3 · forged\\tsegment\\u001B]8;;https://example.invalid\\u0007",
			result);
		Assert.DoesNotContain('\u001b', result);
		Assert.DoesNotContain('\u0007', result);
	}
}
