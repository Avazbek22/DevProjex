using DevProjex.Application.Context;

namespace DevProjex.Tests.Unit;

public sealed class CancellationBoundWriteStreamTests
{
	[Fact]
	public void LargeSynchronousSpanWriteIsChunkedWithoutChangingBytes()
	{
		var expected = Enumerable.Range(0, 1024 * 1024)
			.Select(index => (byte)(index % 251))
			.ToArray();
		using var destination = new RecordingWriteStream();
		using var stream = new CancellationBoundWriteStream(
			destination,
			TestContext.Current.CancellationToken);

		stream.Write(expected.AsSpan());

		Assert.Equal(expected, destination.ToArray());
		Assert.InRange(destination.MaximumWriteSize, 1, 64 * 1024);
	}

	private sealed class RecordingWriteStream : MemoryStream
	{
		public int MaximumWriteSize { get; private set; }

		public override ValueTask WriteAsync(
			ReadOnlyMemory<byte> buffer,
			CancellationToken cancellationToken = default)
		{
			MaximumWriteSize = Math.Max(MaximumWriteSize, buffer.Length);
			return base.WriteAsync(buffer, cancellationToken);
		}
	}
}
