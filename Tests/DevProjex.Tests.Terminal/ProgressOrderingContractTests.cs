namespace DevProjex.Tests.Terminal;

/// <summary>
/// The ordering check that the large-manifest progress test relies on, driven directly.
/// </summary>
/// <remarks>
/// That test waits for the terminal notification before reading what arrived, which is what stops
/// it failing on delivery. Waiting, on its own, would also pass a server that wrote the result
/// first — the notification would simply turn up later. So the order the server wrote in is checked
/// separately, and these cases are what show that check can tell the two apart. Without them it
/// could quietly stop distinguishing anything and every run would still be green.
/// </remarks>
public sealed class ProgressOrderingContractTests
{
	private const string TerminalProgress =
		"""{"jsonrpc":"2.0","method":"notifications/progress","params":{"progressToken":"throttle","progress":100,"total":100}}""";

	private const string EarlierProgress =
		"""{"jsonrpc":"2.0","method":"notifications/progress","params":{"progressToken":"throttle","progress":5,"total":100}}""";

	private const string CallResult =
		"""{"jsonrpc":"2.0","id":2,"result":{"content":[],"isError":false}}""";

	private const string InitializeResult =
		"""{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05"}}""";

	[Fact]
	public void TheRecordedOrderIsAcceptedWhenProgressPrecedesTheResult() =>
		McpServerProcessTests.AssertFinalProgressPrecedesResultForContract(
			string.Join('\n', InitializeResult, EarlierProgress, TerminalProgress, CallResult));

	[Fact]
	public void AResultWrittenAheadOfTheTerminalProgressIsRejected()
	{
		var reordered = string.Join('\n', InitializeResult, EarlierProgress, CallResult, TerminalProgress);

		var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			McpServerProcessTests.AssertFinalProgressPrecedesResultForContract(reordered));

		Assert.Contains("ahead of the terminal progress", failure.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ASequenceWithoutATerminalProgressIsRejected()
	{
		var truncated = string.Join('\n', InitializeResult, EarlierProgress, CallResult);

		var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			McpServerProcessTests.AssertFinalProgressPrecedesResultForContract(truncated));

		Assert.Contains("never wrote a terminal progress notification", failure.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ASequenceWithoutAResultIsRejected()
	{
		var unfinished = string.Join('\n', EarlierProgress, TerminalProgress);

		var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
			McpServerProcessTests.AssertFinalProgressPrecedesResultForContract(unfinished));

		Assert.Contains("never wrote a result", failure.Message, StringComparison.Ordinal);
	}
}
