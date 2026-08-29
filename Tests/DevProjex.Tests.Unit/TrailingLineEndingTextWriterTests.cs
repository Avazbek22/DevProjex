using DevProjex.Application.Services;

namespace DevProjex.Tests.Unit;

public sealed class TrailingLineEndingTextWriterTests
{
	[Fact]
	public async Task FlushesPendingLineEndingsExactlyAndDropsOnlyTheFinalRun()
	{
		using var output = new StringWriter(CultureInfo.InvariantCulture);
		var writer = new TrailingLineEndingTextWriter(output);

		await writer.WriteAsync("alpha\r\n".AsMemory(), TestContext.Current.CancellationToken);
		await writer.WriteAsync("\rbravo\n".AsMemory(), TestContext.Current.CancellationToken);
		await writer.WriteAsync("\r\n".AsMemory(), TestContext.Current.CancellationToken);
		await writer.CompleteAsync(TestContext.Current.CancellationToken);

		Assert.Equal("alpha\r\n\rbravo", output.ToString());
		Assert.Equal(0, writer.BufferedLineEndingCount);
	}

	[Fact]
	public async Task StoresLongPendingLineEndingRunsInOneBitPerCharacter()
	{
		const int characterCount = 128 * 1024;
		var lineEndings = string.Create(
			characterCount,
			state: 0,
			static (buffer, _) =>
			{
				for (var index = 0; index < buffer.Length; index++)
					buffer[index] = (index & 1) == 0 ? '\r' : '\n';
			});
		var writer = new TrailingLineEndingTextWriter(TextWriter.Null);

		await writer.WriteAsync(lineEndings.AsMemory(), TestContext.Current.CancellationToken);

		Assert.Equal(characterCount, writer.BufferedLineEndingCount);
		Assert.InRange(writer.BufferedStorageCapacityBytes, 1, characterCount / 8);
		await writer.CompleteAsync(TestContext.Current.CancellationToken);
	}
}
