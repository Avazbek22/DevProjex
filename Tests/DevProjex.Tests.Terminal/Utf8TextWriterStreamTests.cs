using DevProjex.Terminal.Execution;

namespace DevProjex.Tests.Terminal;

public sealed class Utf8TextWriterStreamTests
{
	[Fact]
	public async Task SplitMultibyteWritesPreserveExactText()
	{
		const string expected = "prefix 🙂 日本語 suffix";
		var bytes = Encoding.UTF8.GetBytes(expected);
		var split = Array.IndexOf(bytes, (byte)0xF0) + 2;
		using var writer = new StringWriter();
		await using var stream = new Utf8TextWriterStream(
			writer,
			TestContext.Current.CancellationToken);

		await stream.WriteAsync(bytes.AsMemory(0, split), TestContext.Current.CancellationToken);
		await stream.WriteAsync(bytes.AsMemory(split), TestContext.Current.CancellationToken);
		await stream.CompleteAsync(TestContext.Current.CancellationToken);

		Assert.Equal(expected, writer.ToString());
	}
}
