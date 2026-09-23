using DevProjex.Application.Compression;

namespace DevProjex.Tests.Unit;

public sealed class FileContentAnalyzerCoherenceTests
{
	[Theory]
	[InlineData(ReadOperation.Metrics)]
	[InlineData(ReadOperation.Fact)]
	[InlineData(ReadOperation.BudgetedFact)]
	[InlineData(ReadOperation.CompleteBuffer)]
	public async Task MutationDuringRead_PreservesResultButDoesNotPublishStableIdentity(ReadOperation operation)
	{
		using var directory = new TemporaryDirectory();
		var path = directory.CreateFile("changing.txt", new string('a', 2048));
		var initialTimestamp = File.GetLastWriteTimeUtc(path);
		var changed = false;
		var analyzer = new FileContentAnalyzer((filePath, _, _, _) =>
			new CallbackReadFileStream(filePath, () =>
			{
				File.WriteAllText(filePath, new string('b', 2048));
				File.SetLastWriteTimeUtc(filePath, initialTimestamp.AddHours(1));
				changed = true;
			}));

		var result = await ReadAsync(analyzer, path, operation, TestContext.Current.CancellationToken);

		Assert.True(changed);
		Assert.Equal(FileContentClassification.Text, result.Classification);
		Assert.Null(result.Identity);
		Assert.Equal(Capture(path), result.ObservedIdentity);
	}

	[Theory]
	[InlineData(ReadOperation.Metrics)]
	[InlineData(ReadOperation.Fact)]
	[InlineData(ReadOperation.BudgetedFact)]
	[InlineData(ReadOperation.CompleteBuffer)]
	public async Task AtomicReplacement_IdentityStillDescribesTheReadHandleNotTheNewPath(ReadOperation operation)
	{
		using var directory = new TemporaryDirectory();
		var path = directory.CreateFile("original.txt", new string('a', 2048));
		var replacement = directory.CreateFile("replacement.txt", "replacement");
		var originalIdentity = Capture(path);
		var replaced = false;
		var analyzer = new FileContentAnalyzer((filePath, _, _, _) =>
			new CallbackReadFileStream(filePath, () =>
			{
				File.Replace(replacement, filePath, destinationBackupFileName: null);
				replaced = true;
			}));

		var result = await ReadAsync(analyzer, path, operation, TestContext.Current.CancellationToken);

		Assert.True(replaced);
		Assert.Equal(FileContentClassification.Text, result.Classification);
		Assert.Equal(originalIdentity, result.Identity);
		Assert.False(result.Identity!.Value.IsCurrent(path));
	}

	[Theory]
	[InlineData(ReadOperation.Metrics)]
	[InlineData(ReadOperation.Fact)]
	[InlineData(ReadOperation.BudgetedFact)]
	[InlineData(ReadOperation.CompleteBuffer)]
	public async Task StableReads_PreserveEmptyEncodingBinaryAndEstimatedResults(ReadOperation operation)
	{
		using var directory = new TemporaryDirectory();
		var empty = directory.CreateFile("empty.txt", string.Empty);
		var utf8 = directory.CreateBinaryFile("utf8.txt", [0xef, 0xbb, 0xbf, 0x61, 0x0a]);
		var utf16 = directory.CreateBinaryFile("utf16.txt", [0xff, 0xfe, 0x61, 0x00, 0x0a, 0x00]);
		var binary = directory.CreateBinaryFile("binary.txt", [0x61, 0x00, 0x62]);
		var malformed = directory.CreateBinaryFile("malformed.txt", [0xff, 0x61]);
		var large = directory.CreateFile("large.txt", new string('a', 1024));
		using (var stream = new FileStream(large, FileMode.Open, FileAccess.Write, FileShare.Read))
			stream.SetLength(11 * 1024 * 1024);
		var analyzer = new FileContentAnalyzer();

		foreach (var (path, expectedClassification) in new[]
		         {
			         (empty, FileContentClassification.Text),
			         (utf8, FileContentClassification.Text),
			         (utf16, FileContentClassification.Text),
			         (binary, FileContentClassification.Binary),
			         (large, FileContentClassification.TooLarge)
		         })
		{
			var result = await ReadAsync(analyzer, path, operation, TestContext.Current.CancellationToken);
			Assert.Equal(expectedClassification, result.Classification);
			Assert.Equal(Capture(path), result.Identity);
			Assert.True(result.Identity!.Value.IsCurrent(path));
		}

		var unsupported = await ReadAsync(analyzer, malformed, operation, TestContext.Current.CancellationToken);
		Assert.Equal(FileContentClassification.UnsupportedEncoding, unsupported.Classification);
		Assert.Null(unsupported.Identity);
	}

	[Theory]
	[InlineData(ReadOperation.Metrics)]
	[InlineData(ReadOperation.Fact)]
	[InlineData(ReadOperation.BudgetedFact)]
	[InlineData(ReadOperation.CompleteBuffer)]
	public async Task KnownBinaryExtension_DoesNotOpenContentForAnIdentity(ReadOperation operation)
	{
		using var directory = new TemporaryDirectory();
		var path = directory.CreateBinaryFile("image.png", [0x61, 0x62]);
		var opens = 0;
		var analyzer = new FileContentAnalyzer((_, _, _, _) =>
		{
			opens++;
			throw new InvalidOperationException("Known binary content must not be opened.");
		});

		var result = await ReadAsync(analyzer, path, operation, TestContext.Current.CancellationToken);

		Assert.Equal(FileContentClassification.Binary, result.Classification);
		Assert.Null(result.Identity);
		Assert.Equal(0, opens);
	}

	[Theory]
	[InlineData(ReadOperation.Metrics)]
	[InlineData(ReadOperation.Fact)]
	[InlineData(ReadOperation.BudgetedFact)]
	[InlineData(ReadOperation.CompleteBuffer)]
	public async Task CancellationDuringRead_PropagatesAndClosesTheContentHandle(ReadOperation operation)
	{
		using var directory = new TemporaryDirectory();
		var path = directory.CreateFile("canceled.txt", new string('a', 2048));
		using var cancellation = new CancellationTokenSource();
		CallbackReadFileStream? observedStream = null;
		var analyzer = new FileContentAnalyzer((filePath, _, _, _) =>
			observedStream = new CallbackReadFileStream(filePath, cancellation.Cancel));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
			await ReadAsync(analyzer, path, operation, cancellation.Token));

		Assert.NotNull(observedStream);
		Assert.True(observedStream.WasDisposed);
	}

	[Fact]
	public async Task BudgetedRead_MutationWhileWaitingForDecodeGateDoesNotPublishStableIdentity()
	{
		using var directory = new TemporaryDirectory();
		var path = directory.CreateFile("waiting.txt", new string('a', 2048));
		var initialTimestamp = File.GetLastWriteTimeUtc(path);
		var analyzer = new FileContentAnalyzer((filePath, _, _, _) =>
			new CallbackReadFileStream(filePath, static () => { }));
		using var byteBudget = new WeightedByteBudget(16 * 1024);
		using var decodeGate = new SemaphoreSlim(0, 1);
		var reading = ((IPrewarmFileContentAnalyzer)analyzer).ReadFactWithBudgetAsync(
			path, 4096, byteBudget, decodeGate, TestContext.Current.CancellationToken);
		Assert.False(reading.IsCompleted);
		try
		{
			File.WriteAllText(path, new string('b', 2048));
			File.SetLastWriteTimeUtc(path, initialTimestamp.AddHours(1));
		}
		finally
		{
			decodeGate.Release();
		}

		var result = await reading;
		using (result.Lease)
		{
			Assert.Equal(FileContentClassification.Text, result.Fact.Classification);
			Assert.Equal(new string('b', 2048), result.Fact.Content);
			Assert.Null(result.StableIdentity);
			Assert.Equal(Capture(path), result.Identity);
		}
		Assert.Equal(1, decodeGate.CurrentCount);
	}

	[Fact]
	public void AfterReadIdentityAlone_DoesNotImplyReadStability()
	{
		var identity = new FileContentIdentity(1, 2);
		var metrics = new IdentifiedFileContentMetricsResult(
			new FileContentMetricsResult(FileContentClassification.Text), identity);
		var fact = new ContentReadFact(null, FileContentClassification.Text, null, null);

		Assert.Null(metrics.StableIdentity);
		Assert.Null(new IdentifiedContentReadFact(fact, identity).StableIdentity);
		Assert.Null(new BudgetedContentReadResult(fact, identity, null).StableIdentity);
	}

	private static FileContentIdentity Capture(string path)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		return FileContentIdentity.TryCapture(stream)!.Value;
	}

	private static async Task<ObservedRead> ReadAsync(
		FileContentAnalyzer analyzer,
		string path,
		ReadOperation operation,
		CancellationToken cancellationToken)
	{
		switch (operation)
		{
			case ReadOperation.Metrics:
				var metrics = await ((IPrewarmFileContentAnalyzer)analyzer)
					.GetClassifiedMetricsWithIdentityAsync(path, cancellationToken);
				return new ObservedRead(metrics.Result.Classification, metrics.StableIdentity, metrics.Identity);
			case ReadOperation.Fact:
				var fact = await ((ICoherentFileContentAnalyzer)analyzer)
					.ReadFactWithIdentityAsync(path, 4096, cancellationToken);
				return new ObservedRead(fact.Fact.Classification, fact.StableIdentity, fact.Identity);
			case ReadOperation.BudgetedFact:
				using (var byteBudget = new WeightedByteBudget(16 * 1024))
				using (var decodeGate = new SemaphoreSlim(1, 1))
				{
					var budgeted = await ((IPrewarmFileContentAnalyzer)analyzer)
						.ReadFactWithBudgetAsync(path, 4096, byteBudget, decodeGate, cancellationToken);
					using (budgeted.Lease)
						return new ObservedRead(budgeted.Fact.Classification, budgeted.StableIdentity, budgeted.Identity);
				}
			case ReadOperation.CompleteBuffer:
				var complete = await ((ICoherentFileContentAnalyzer)analyzer)
					.OpenCompleteTextBufferWithIdentityAsync(path, 4096, cancellationToken);
				await using (complete.Buffer)
					return new ObservedRead(complete.Buffer.Classification, complete.StableIdentity, complete.Identity);
			default:
				throw new ArgumentOutOfRangeException(nameof(operation));
		}
	}

	public enum ReadOperation
	{
		Metrics,
		Fact,
		BudgetedFact,
		CompleteBuffer
	}

	private readonly record struct ObservedRead(
		FileContentClassification Classification,
		FileContentIdentity? Identity,
		FileContentIdentity? ObservedIdentity);

	private sealed class CallbackReadFileStream(string path, Action afterFirstRead)
		: FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1)
	{
		private bool _invoked;

		public bool WasDisposed { get; private set; }

		public override int Read(Span<byte> buffer)
		{
			var read = base.Read(buffer);
			InvokeOnce();
			return read;
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			var read = base.Read(buffer, offset, count);
			InvokeOnce();
			return read;
		}

		protected override void Dispose(bool disposing)
		{
			WasDisposed = true;
			base.Dispose(disposing);
		}

		private void InvokeOnce()
		{
			if (_invoked)
				return;
			_invoked = true;
			try
			{
				afterFirstRead();
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				throw new InvalidOperationException("The controlled read callback failed.", exception);
			}
		}
	}
}
