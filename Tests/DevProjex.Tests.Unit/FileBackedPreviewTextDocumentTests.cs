using DevProjex.Application.Preview;

namespace DevProjex.Tests.Unit;

public sealed class FileBackedPreviewTextDocumentTests
{
    [Fact]
    public void GetLineText_ClampsIndexesAndTrimsLineTerminators()
    {
        using var temp = new TemporaryDirectory();
        using var document = CreateDocument(
            temp,
            ("alpha\r", "alpha"),
            ("", string.Empty),
            ("gamma", "gamma"));

        Assert.Equal(3, document.LineCount);
        Assert.Equal("alpha", document.GetLineText(1));
        Assert.Equal(string.Empty, document.GetLineText(2));
        Assert.Equal("gamma", document.GetLineText(3));
        Assert.Equal("alpha", document.GetLineText(0));
        Assert.Equal("gamma", document.GetLineText(99));
    }

    [Fact]
    public void GetLineRangeText_AndDispose_PreserveContentAndCleanUpStorage()
    {
        using var temp = new TemporaryDirectory();
        var (document, storagePath) = CreateDocumentWithPath(
            temp,
            ("alpha", "alpha"),
            ("", string.Empty),
            ("gamma", "gamma"));

        Assert.Equal("alpha\n\ngamma", document.GetLineRangeText(1, 99));

        document.Dispose();

        Assert.False(File.Exists(storagePath));
        Assert.Throws<ObjectDisposedException>(() => document.GetLineText(1));
    }

	[Fact]
	public void VisitLines_StreamsUnicodeAcrossChunksAndStopsAtVisitorBoundary()
	{
		using var temp = new TemporaryDirectory();
		var lines = Enumerable.Range(0, 2_050)
			.Select(static index =>
			{
				var visible = $"文書-{index:D4}";
				return (RawLine: index % 2 == 0 ? visible : visible + "\r", VisibleLine: visible);
			})
			.ToArray();
		using var document = CreateDocument(temp, lines);
		var visited = new List<(int Line, string Text)>();

		document.VisitLines(
			2,
			document.LineCount,
			(lineNumber, line) =>
			{
				visited.Add((lineNumber, line.ToString()));
				return lineNumber < 1_500;
			},
			TestContext.Current.CancellationToken);

		Assert.Equal(1_499, visited.Count);
		Assert.Equal((2, "文書-0001"), visited[0]);
		Assert.Equal((1_500, "文書-1499"), visited[^1]);
	}

	[Fact]
	public void VisitLines_EmptyDocument_VisitsTheLogicalEmptyLine()
	{
		using var temp = new TemporaryDirectory();
		using var document = CreateDocument(temp);
		var visited = new List<(int Line, string Text)>();

		document.VisitLines(
			1,
			1,
			(lineNumber, line) =>
			{
				visited.Add((lineNumber, line.ToString()));
				return true;
			},
			TestContext.Current.CancellationToken);

		Assert.Equal([(1, string.Empty)], visited);
	}

	[Fact]
	public void VisitLines_NonIntersectingOrInvertedRange_DoesNotVisitLines()
	{
		using var temp = new TemporaryDirectory();
		using var document = CreateDocument(temp, ("alpha", "alpha"));
		var visits = 0;

		document.VisitLines(
			2,
			1,
			(_, _) =>
			{
				visits++;
				return true;
			},
			TestContext.Current.CancellationToken);
		document.VisitLines(
			2,
			3,
			(_, _) =>
			{
				visits++;
				return true;
			},
			TestContext.Current.CancellationToken);

		Assert.Equal(0, visits);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CanceledTraversal_DoesNotWaitForConcurrentExportToFinish(bool searchDocument)
	{
		using var directory = new TemporaryDirectory();
		using var document = CreateDocument(directory, ("alpha", "alpha"), ("beta", "beta"));
		await using var destination = new GatedWriteStream();
		using var cancellation = new CancellationTokenSource();
		var export = document.WriteToAsync(destination, TestContext.Current.CancellationToken).AsTask();
		var completion = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
		var visits = 0;
		var reader = new Thread(() =>
		{
			try
			{
				if (searchDocument)
				{
					_ = PreviewTextDocumentSearch.Find(document, "alpha", cancellation.Token);
				}
				else
				{
					document.VisitLines(1, document.LineCount, (_, _) =>
					{
						visits++;
						return true;
					}, cancellation.Token);
				}
				completion.TrySetResult(null);
			}
			catch (Exception exception)
			{
				completion.TrySetResult(exception);
			}
		}) { IsBackground = true };
		var readerStarted = false;

		try
		{
			await destination.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
			reader.Start();
			readerStarted = true;
			Assert.True(SpinWait.SpinUntil(
				() => completion.Task.IsCompleted || (reader.ThreadState & ThreadState.WaitSleepJoin) != 0,
				TimeSpan.FromSeconds(5)));
			Assert.False(completion.Task.IsCompleted);

			cancellation.Cancel();
			var failure = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
			var canceled = Assert.IsAssignableFrom<OperationCanceledException>(failure);
			Assert.Equal(cancellation.Token, canceled.CancellationToken);
			Assert.Equal(0, visits);
			Assert.False(export.IsCompleted);
		}
		finally
		{
			destination.ReleaseWrite.TrySetResult();
			await export.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
			if (readerStarted)
				Assert.True(reader.Join(TimeSpan.FromSeconds(5)));
		}

		Assert.Equal("alpha\nbeta\n", destination.GetWrittenText());
		Assert.True(destination.CanWrite);
		Assert.Equal("alpha", document.GetLineText(1));
		Assert.Equal("beta", document.GetLineText(2));
	}

	private sealed class GatedWriteStream : Stream
	{
		private readonly MemoryStream _written = new();
		public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public override bool CanRead => false;
		public override bool CanSeek => false;
		public override bool CanWrite => _written.CanWrite;
		public override long Length => _written.Length;
		public override long Position { get => _written.Position; set => throw new NotSupportedException(); }
		public override void Flush() => _written.Flush();
		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
		{
			WriteStarted.TrySetResult();
			await ReleaseWrite.Task.WaitAsync(cancellationToken);
			await _written.WriteAsync(buffer, cancellationToken);
		}
		public string GetWrittenText() => Encoding.UTF8.GetString(_written.ToArray());
		protected override void Dispose(bool disposing)
		{
			if (disposing)
				_written.Dispose();
			base.Dispose(disposing);
		}
	}

    private static FileBackedPreviewTextDocument CreateDocument(
        TemporaryDirectory temp,
        params (string RawLine, string VisibleLine)[] lines)
        => CreateDocumentWithPath(temp, lines).Document;

    private static (FileBackedPreviewTextDocument Document, string StoragePath) CreateDocumentWithPath(
        TemporaryDirectory temp,
        params (string RawLine, string VisibleLine)[] lines)
    {
        var storagePath = Path.Combine(temp.Path, $"{Guid.NewGuid():N}.preview.txt");
        var lineOffsets = new long[lines.Length];
        long currentOffset = 0;

        using (var stream = new FileStream(storagePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            for (var i = 0; i < lines.Length; i++)
            {
                lineOffsets[i] = currentOffset;
                var bytes = Encoding.UTF8.GetBytes(lines[i].RawLine);
                stream.Write(bytes, 0, bytes.Length);
                stream.WriteByte((byte)'\n');
                currentOffset += bytes.Length + 1;
            }
        }

        var document = new FileBackedPreviewTextDocument(
            storagePath,
            lineOffsets,
            currentOffset,
            lines.Length == 0 ? 0 : lines.Max(static line => line.VisibleLine.Length),
            lines.Sum(static line => line.RawLine.Length + 1L));

        return (document, storagePath);
    }
}
