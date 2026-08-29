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

	[Fact]
	public async Task ArbitraryChunkBoundariesPreserveEverythingExceptTheFinalLineEndingRun()
	{
		const string alphabet = "ab\r\n\t\u754C";
		var random = new Random(0x5EED);
		for (var scenario = 0; scenario < 200; scenario++)
		{
			var content = string.Create(
				128 + scenario % 31,
				random,
				static (buffer, source) =>
				{
					for (var index = 0; index < buffer.Length; index++)
						buffer[index] = alphabet[source.Next(alphabet.Length)];
				});
			content += (scenario % 3) switch
			{
				0 => "\r",
				1 => "\n\r\n",
				_ => string.Empty
			};
			using var output = new StringWriter(CultureInfo.InvariantCulture);
			var writer = new TrailingLineEndingTextWriter(output);

			var offset = 0;
			while (offset < content.Length)
			{
				var count = Math.Min(random.Next(1, 18), content.Length - offset);
				await writer.WriteAsync(
					content.AsMemory(offset, count),
					TestContext.Current.CancellationToken);
				offset += count;
			}
			await writer.CompleteAsync(TestContext.Current.CancellationToken);

			Assert.Equal(content.TrimEnd('\r', '\n'), output.ToString());
			Assert.Equal(content.Length, writer.Length);
		}
	}
}
