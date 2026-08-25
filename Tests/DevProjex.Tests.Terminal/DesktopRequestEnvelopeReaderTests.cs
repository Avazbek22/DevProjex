using DevProjex.Terminal.DesktopControl;

namespace DevProjex.Tests.Terminal;

public sealed class DesktopRequestEnvelopeReaderTests
{
	[Fact]
	public void BoundedReaderRejectsContentBeyondAStaleReportedLength()
	{
		using var content = new MemoryStream(
			new byte[DesktopProtocol.MaximumMessageBytes + 1]);
		using var misleading = new MisreportedLengthStream(content, reportedLength: 1);
		using var bounded = DesktopRequestEnvelopeReader.CreateBounded(misleading);

		Assert.Equal(1, misleading.Length);
		Assert.Throws<IOException>(() => bounded.CopyTo(Stream.Null));
	}

	private sealed class MisreportedLengthStream(Stream inner, long reportedLength) : Stream
	{
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => reportedLength;
		public override long Position
		{
			get => inner.Position;
			set => throw new NotSupportedException();
		}

		public override int Read(byte[] buffer, int offset, int count) =>
			inner.Read(buffer, offset, count);

		public override int Read(Span<byte> buffer) => inner.Read(buffer);

		public override ValueTask<int> ReadAsync(
			Memory<byte> buffer,
			CancellationToken cancellationToken = default) =>
			inner.ReadAsync(buffer, cancellationToken);

		public override void Flush() => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}
}
