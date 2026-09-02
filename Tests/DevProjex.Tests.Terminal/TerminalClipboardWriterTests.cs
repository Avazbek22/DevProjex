namespace DevProjex.Tests.Terminal;

public sealed class TerminalClipboardWriterTests
{
	[Fact]
	public void Osc52EncodesTheCompleteUtf8Payload()
	{
		const string payload = "hello Привет";

		var sequence = TerminalClipboardWriter.EncodeOsc52(payload);

		Assert.Equal(
			$"\u001b]52;c;{Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))}\a",
			sequence);
	}

	[Fact]
	public void UnavailableClipboardReturnsAnExplicitFailureWithoutWriting()
	{
		var rawWrites = 0;
		var writer = new TerminalClipboardWriter(
			static () => null,
			_ =>
			{
				rawWrites++;
				return true;
			},
			static () => false);

		var result = writer.Write("payload");

		Assert.Equal(TerminalClipboardWriteStatus.Unavailable, result.Status);
		Assert.False(result.IsSuccess);
		Assert.Equal(0, rawWrites);
	}

	[Fact]
	public void OversizedOsc52PayloadFailsWithoutSilentTruncation()
	{
		var rawWrites = 0;
		var writer = new TerminalClipboardWriter(
			static () => null,
			_ =>
			{
				rawWrites++;
				return true;
			},
			static () => true);

		var result = writer.Write(new string('x', TerminalClipboardWriter.MaximumOsc52SequenceLength));

		Assert.Equal(TerminalClipboardWriteStatus.PayloadTooLarge, result.Status);
		Assert.Equal(0, rawWrites);
	}
}
