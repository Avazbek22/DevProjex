using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DevProjex.Application.Services;
using Xunit;

namespace DevProjex.Tests.Unit;

public sealed class TextFileExportServiceTests
{
	[Fact]
	public async Task WriteAsync_WritesUtf8WithoutBom()
	{
		var service = new TextFileExportService();
		await using var stream = new MemoryStream();

		await service.WriteAsync(stream, "ASCII");

		var bytes = stream.ToArray();
		Assert.False(StartsWithUtf8Bom(bytes));
		Assert.Equal("ASCII", Encoding.UTF8.GetString(bytes));
	}

	[Fact]
	public async Task WriteAsync_TruncatesSeekableStreamBeforeWriting()
	{
		var service = new TextFileExportService();
		await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("very long stale content"));

		await service.WriteAsync(stream, "ok");

		Assert.Equal("ok", Encoding.UTF8.GetString(stream.ToArray()));
	}

	[Fact]
	public async Task WriteAsync_PreservesUnixLineEndings()
	{
		var service = new TextFileExportService();
		await using var stream = new MemoryStream();
		const string content = "line1\nline2\nline3";

		await service.WriteAsync(stream, content);

		Assert.Equal(content, Encoding.UTF8.GetString(stream.ToArray()));
	}

	[Fact]
	public async Task WriteAsync_PreservesWindowsLineEndings()
	{
		var service = new TextFileExportService();
		await using var stream = new MemoryStream();
		const string content = "line1\r\nline2\r\nline3";

		await service.WriteAsync(stream, content);

		Assert.Equal(content, Encoding.UTF8.GetString(stream.ToArray()));
	}

	[Fact]
	public async Task WriteAsync_WritesUnicodeContent()
	{
		var service = new TextFileExportService();
		await using var stream = new MemoryStream();
		const string content = "Привет, мир! こんにちは";

		await service.WriteAsync(stream, content);

		Assert.Equal(content, Encoding.UTF8.GetString(stream.ToArray()));
	}

	[Fact]
	public async Task WriteAsync_LeavesStreamOpenAfterWrite()
	{
		var service = new TextFileExportService();
		await using var stream = new MemoryStream();

		await service.WriteAsync(stream, "hello");
		stream.WriteByte((byte)'!');

		Assert.Equal("hello!", Encoding.UTF8.GetString(stream.ToArray()));
	}

	[Fact]
	public async Task WriteAsync_WritesToNonSeekableStream()
	{
		var service = new TextFileExportService();
		using var nonSeekable = new NonSeekableWriteStream();

		await service.WriteAsync(nonSeekable, "content");

		Assert.Equal("content", nonSeekable.GetWrittenText());
	}

	[Fact]
	public async Task WriteAsync_ThrowsForReadOnlyStream()
	{
		var service = new TextFileExportService();
		await using var readOnly = new MemoryStream(new byte[16], writable: false);

		await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteAsync(readOnly, "data"));
	}

	[Fact]
	public async Task WriteAsync_ThrowsForNullStream()
	{
		var service = new TextFileExportService();

		await Assert.ThrowsAsync<ArgumentNullException>(() => service.WriteAsync(null!, "data"));
	}

	[Fact]
	public async Task WriteAsync_ThrowsForNullContent()
	{
		var service = new TextFileExportService();
		await using var stream = new MemoryStream();

		await Assert.ThrowsAsync<ArgumentNullException>(() => service.WriteAsync(stream, null!));
	}

	[Fact]
	public async Task WriteAsync_HonorsCancellationToken()
	{
		var service = new TextFileExportService();
		await using var stream = new MemoryStream();
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => service.WriteAsync(stream, new string('a', 1024), cts.Token));
	}

	private static bool StartsWithUtf8Bom(byte[] bytes)
	{
		return bytes.Length >= 3 &&
		       bytes[0] == 0xEF &&
		       bytes[1] == 0xBB &&
		       bytes[2] == 0xBF;
	}

	private sealed class NonSeekableWriteStream : Stream
	{
		private readonly MemoryStream _inner = new();

		public override bool CanRead => false;
		public override bool CanSeek => false;
		public override bool CanWrite => true;
		public override long Length => _inner.Length;
		public override long Position
		{
			get => _inner.Position;
			set => throw new NotSupportedException();
		}

		public override void Flush() => _inner.Flush();
		public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

		public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
			=> _inner.WriteAsync(buffer, cancellationToken);

		public string GetWrittenText() => Encoding.UTF8.GetString(_inner.ToArray());

		protected override void Dispose(bool disposing)
		{
			if (disposing)
				_inner.Dispose();
			base.Dispose(disposing);
		}
	}
}
