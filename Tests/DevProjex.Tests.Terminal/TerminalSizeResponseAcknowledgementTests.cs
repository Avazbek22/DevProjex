namespace DevProjex.Tests.Terminal;

public sealed class TerminalSizeResponseAcknowledgementTests
{
	[Theory]
	[InlineData("\u001b[8;30;120t")]
	[InlineData("\u001b[8;80;24t")]
	[InlineData("\u001b[8;24;80")]
	[InlineData("\u001b[1;1R")]
	[InlineData("PROJECT TREE")]
	public async Task UnrelatedOrStaleOutputCannotAcknowledgeAResize(string response)
	{
		var acknowledgement = new TerminalSizeResponseAcknowledgement(80, 24);
		Assert.False(acknowledgement.Completion.IsCompleted);
		acknowledgement.ObserveWrittenResponse(response);
		Assert.False(acknowledgement.Completion.IsCompleted);
		acknowledgement.ObserveWrittenResponse("\u001b[8;24;80t");
		Assert.True(acknowledgement.Completion.IsCompletedSuccessfully);
		await acknowledgement.Completion.WaitAsync(TestContext.Current.CancellationToken);
	}

	[Fact]
	public async Task ReturningToTheOriginalSizeRequiresItsOwnResponse()
	{
		var compact = new TerminalSizeResponseAcknowledgement(80, 24);
		compact.ObserveWrittenResponse("\u001b[8;24;80t");
		Assert.True(compact.Completion.IsCompletedSuccessfully);
		await compact.Completion.WaitAsync(TestContext.Current.CancellationToken);
		var restored = new TerminalSizeResponseAcknowledgement(120, 30);
		restored.ObserveWrittenResponse("\u001b[8;24;80t");
		Assert.False(restored.Completion.IsCompleted);
		restored.ObserveWrittenResponse("\u001b[8;30;120t");
		restored.ObserveWrittenResponse("\u001b[8;30;120t");
		Assert.True(restored.Completion.IsCompletedSuccessfully);
		await restored.Completion.WaitAsync(TestContext.Current.CancellationToken);
	}
}
