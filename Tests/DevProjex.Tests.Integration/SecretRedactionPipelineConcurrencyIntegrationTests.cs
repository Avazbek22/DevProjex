using DevProjex.Application.Compression;
using DevProjex.Application.Diagnostics;
using DevProjex.Application.Secrets;
using DevProjex.Application.Services;
using System.Reflection;

namespace DevProjex.Tests.Integration;

public sealed class SecretRedactionPipelineConcurrencyIntegrationTests
{
	[Theory(Timeout = 15_000)]
	[InlineData("00.txt")]
	[InlineData("07.txt")]
	public async Task PrepareAsync_WorkerFailureCancelsEveryParticipantAndReleasesRetainedBytes(
		string failingFileName)
	{
		using var temporary = new TemporaryDirectory();
		var projectRoot = temporary.CreateDirectory("project");
		var paths = CreateFiles(temporary, 16);
		var detector = new ThrowingDetector(failingFileName);
		using var session = new SecretRedactionSession(detector);
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());
		using var diagnostics = ContentPipelineDiagnostics.BeginMeasurement();

		var exception = await Assert.ThrowsAsync<PipelineProbeException>(() => preparer.PrepareAsync(
			new ContentTransformationContext(
				Compression: null,
				new SecretRedactionContext(projectRoot, session)),
			paths,
			TestContext.Current.CancellationToken));

		Assert.Equal(failingFileName, exception.Message);
		Assert.Equal(0, detector.ActiveCalls);
		Assert.Equal(0, ReadInFlightBytes(diagnostics));
	}

	[Fact(Timeout = 15_000)]
	public async Task PrepareAsync_ConsumerFailureWithAFullWindowDrainsWorkersAndReservations()
	{
		using var temporary = new TemporaryDirectory();
		var projectRoot = temporary.CreateDirectory("project");
		var paths = CreateFiles(temporary, 16);
		using var detector = new FillWindowBeforeFirstDetector(requiredLaterDetections: 7);
		using var session = new SecretRedactionSession(detector);
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());
		using var diagnostics = ContentPipelineDiagnostics.BeginMeasurement();

		var exception = await Assert.ThrowsAsync<PipelineProbeException>(() => preparer.PrepareAsync(
			new ContentTransformationContext(
				Compression: null,
				new SecretRedactionContext(projectRoot, session)),
			paths,
			new ThrowingProgress(),
			TestContext.Current.CancellationToken));

		Assert.Equal("consumer", exception.Message);
		Assert.Equal(0, detector.ActiveCalls);
		Assert.Equal(0, ReadInFlightBytes(diagnostics));
	}

	[Fact(Timeout = 15_000)]
	public async Task PrepareAsync_ExternalCancellationWithAFullWindowDrainsWorkersAndReservations()
	{
		using var temporary = new TemporaryDirectory();
		var projectRoot = temporary.CreateDirectory("project");
		var paths = CreateFiles(temporary, 16);
		using var detector = new HoldFirstAfterWindowFillsDetector(requiredLaterDetections: 7);
		using var session = new SecretRedactionSession(detector);
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		using var diagnostics = ContentPipelineDiagnostics.BeginMeasurement();
		var operation = preparer.PrepareAsync(
			new ContentTransformationContext(
				Compression: null,
				new SecretRedactionContext(projectRoot, session)),
			paths,
			cancellation.Token);

		await detector.WindowFilled.WaitAsync(TestContext.Current.CancellationToken);
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

		Assert.Equal(0, detector.ActiveCalls);
		Assert.Equal(0, ReadInFlightBytes(diagnostics));
	}

	[Fact(Timeout = 15_000)]
	public async Task PrepareAsync_FileDisappearingAfterSchedulingReleasesItsReservations()
	{
		using var temporary = new TemporaryDirectory();
		var projectRoot = temporary.CreateDirectory("project");
		var paths = CreateFiles(temporary, 12);
		var disappearing = paths[5];
		var analyzer = new DeleteBeforeReadAnalyzer(new FileContentAnalyzer(), disappearing);
		using var session = new SecretRedactionSession(new ExactSecretDetector("secret"));
		var preparer = new SecretRedactionOutputPreparer(analyzer);
		using var diagnostics = ContentPipelineDiagnostics.BeginMeasurement();

		await Assert.ThrowsAnyAsync<IOException>(() => preparer.PrepareAsync(
			new ContentTransformationContext(
				Compression: null,
				new SecretRedactionContext(projectRoot, session)),
			paths,
			TestContext.Current.CancellationToken));

		Assert.False(File.Exists(disappearing));
		Assert.Equal(0, ReadInFlightBytes(diagnostics));
	}

	[Fact]
	public async Task PrepareAsync_LargeSelectionUsesOneImmutableSnapshotWithoutChangingContent()
	{
		const string secret = "snapshot-secret-value-42";
		using var temporary = new TemporaryDirectory();
		var projectRoot = temporary.CreateDirectory("project");
		var paths = Enumerable.Range(0, 256)
			.Select(index => temporary.CreateFile(
				$"project/{index:D3}.txt",
				$"line-{index:D3}\r\n{secret}\n"))
			.ToArray();
		using var session = new SecretRedactionSession(new ExactSecretDetector(secret));
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());

		await using var prepared = await preparer.PrepareAsync(
			new ContentTransformationContext(
				Compression: null,
				new SecretRedactionContext(projectRoot, session)),
			paths,
			TestContext.Current.CancellationToken);

		var contentPaths = paths.Select(path => prepared.GetFile(path).ContentPath).Distinct().ToArray();
		Assert.Single(contentPaths);
		Assert.EndsWith("prepared-content.snapshot", contentPaths[0], StringComparison.Ordinal);
		var analyzer = preparer.CreatePreparedAnalyzer(prepared);
		for (var index = 0; index < paths.Length; index++)
		{
			var metrics = await analyzer.GetTextFileMetricsAsync(
				paths[index],
				TestContext.Current.CancellationToken);
			var content = await analyzer.TryReadAsTextAsync(
				paths[index],
				TestContext.Current.CancellationToken);

			Assert.NotNull(metrics);
			Assert.NotNull(content);
			Assert.Equal(content.Content.Length, metrics.CharCount);
			Assert.Equal(3, metrics.LineCount);
			Assert.Equal(1, metrics.CrLfPairCount);
			Assert.DoesNotContain(secret, content.Content, StringComparison.Ordinal);
			Assert.Contains("DEVPROJEX_REDACTED[snapshot-test#1]", content.Content, StringComparison.Ordinal);
		}
	}

	[Fact]
	public async Task PrepareAsync_EarliestBlockedEntryRetainsCapacityAndCompletesInSelectionOrder()
	{
		if (Environment.ProcessorCount < 2)
			Assert.Skip("The bounded parallel pipeline requires at least two processors for this contention test.");

		using var temporary = new TemporaryDirectory();
		var projectRoot = temporary.CreateDirectory("project");
		var paths = Enumerable.Range(0, 4)
			.Select(index => temporary.CreateFile(
				$"project/{index:D2}.txt",
				$"secret-{index:D2}" + new string('x', 4 * 1024 * 1024)))
			.ToArray();
		using var detector = new BlockEarliestUntilLaterDetector(requiredLaterDetections: 2);
		using var session = new SecretRedactionSession(detector);
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(20));

		await using var prepared = await preparer.PrepareAsync(
			new ContentTransformationContext(
				Compression: null,
				new SecretRedactionContext(projectRoot, session)),
			paths,
			captureEffectiveFindings: true,
			timeout.Token);

		var analyzer = preparer.CreatePreparedAnalyzer(prepared);
		for (var index = 0; index < paths.Length; index++)
		{
			var content = await analyzer.TryReadAsTextAsync(paths[index], timeout.Token);
			Assert.NotNull(content);
			Assert.StartsWith(
				$"DEVPROJEX_REDACTED[parallel-test#{index + 1}]",
				content.Content,
				StringComparison.Ordinal);
		}
	}

	private sealed class ExactSecretDetector(string value) : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) =>
			Detect(repositoryRelativePath, content.AsSpan(), cancellationToken);

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			ReadOnlySpan<char> content,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var start = content.IndexOf(value.AsSpan(), StringComparison.Ordinal);
			return start < 0
				? []
				: [new DetectedSecret("snapshot-test", start, value.Length, value, 1)];
		}
	}

	private sealed class BlockEarliestUntilLaterDetector(int requiredLaterDetections) : ISecretDetector, IDisposable
	{
		private readonly ManualResetEventSlim laterDetectionsCompleted = new(false);
		private int laterDetectionCount;

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) =>
			Detect(repositoryRelativePath, content.AsSpan(), cancellationToken);

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			ReadOnlySpan<char> content,
			CancellationToken cancellationToken = default)
		{
			if (repositoryRelativePath.EndsWith("00.txt", StringComparison.Ordinal))
			{
				laterDetectionsCompleted.Wait(cancellationToken);
			}
			else if (Interlocked.Increment(ref laterDetectionCount) >= requiredLaterDetections)
			{
				laterDetectionsCompleted.Set();
			}

			const int valueLength = 9;
			return [new DetectedSecret("parallel-test", 0, valueLength, content[..valueLength].ToString(), 0)];
		}

		public void Dispose() => laterDetectionsCompleted.Dispose();
	}

	private static string[] CreateFiles(TemporaryDirectory temporary, int count) =>
		Enumerable.Range(0, count)
			.Select(index => temporary.CreateFile($"project/{index:D2}.txt", $"secret-{index:D2}\n"))
			.ToArray();

	private static long ReadInFlightBytes(ContentPipelineMeasurement measurement)
	{
		var state = typeof(ContentPipelineMeasurement)
			.GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(measurement)!;
		return (long)state.GetType()
			.GetField("InFlightBytes", BindingFlags.Instance | BindingFlags.Public)!
			.GetValue(state)!;
	}

	private sealed class PipelineProbeException(string message) : Exception(message);

	private sealed class ThrowingDetector(string failingFileName) : ISecretDetector
	{
		private int activeCalls;

		public int ActiveCalls => Volatile.Read(ref activeCalls);

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) =>
			Detect(repositoryRelativePath, content.AsSpan(), cancellationToken);

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			ReadOnlySpan<char> content,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref activeCalls);
			try
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (repositoryRelativePath.EndsWith(failingFileName, StringComparison.Ordinal))
					throw new PipelineProbeException(failingFileName);
				return [];
			}
			finally
			{
				Interlocked.Decrement(ref activeCalls);
			}
		}
	}

	private sealed class FillWindowBeforeFirstDetector(int requiredLaterDetections) : ISecretDetector, IDisposable
	{
		private readonly ManualResetEventSlim releaseFirst = new(false);
		private int activeCalls;
		private int laterDetections;

		public int ActiveCalls => Volatile.Read(ref activeCalls);

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) =>
			Detect(repositoryRelativePath, content.AsSpan(), cancellationToken);

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			ReadOnlySpan<char> content,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref activeCalls);
			try
			{
				if (repositoryRelativePath.EndsWith("00.txt", StringComparison.Ordinal))
					releaseFirst.Wait(cancellationToken);
				else if (Interlocked.Increment(ref laterDetections) >= requiredLaterDetections)
					releaseFirst.Set();
				return [];
			}
			finally
			{
				Interlocked.Decrement(ref activeCalls);
			}
		}

		public void Dispose() => releaseFirst.Dispose();
	}

	private sealed class HoldFirstAfterWindowFillsDetector(int requiredLaterDetections) : ISecretDetector, IDisposable
	{
		private readonly ManualResetEventSlim cancellationGate = new(false);
		private readonly TaskCompletionSource windowFilled = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int activeCalls;
		private int laterDetections;

		public Task WindowFilled => windowFilled.Task;
		public int ActiveCalls => Volatile.Read(ref activeCalls);

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) =>
			Detect(repositoryRelativePath, content.AsSpan(), cancellationToken);

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			ReadOnlySpan<char> content,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref activeCalls);
			try
			{
				if (repositoryRelativePath.EndsWith("00.txt", StringComparison.Ordinal))
				{
					cancellationGate.Wait(cancellationToken);
				}
				else if (Interlocked.Increment(ref laterDetections) >= requiredLaterDetections)
				{
					windowFilled.TrySetResult();
				}
				return [];
			}
			finally
			{
				Interlocked.Decrement(ref activeCalls);
			}
		}

		public void Dispose() => cancellationGate.Dispose();
	}

	private sealed class ThrowingProgress : IProgress<ProjectCopyExportProgress>
	{
		public void Report(ProjectCopyExportProgress value) => throw new PipelineProbeException("consumer");
	}

	private sealed class DeleteBeforeReadAnalyzer(
		IFileContentAnalyzer inner,
		string pathToDelete) : IFileContentAnalyzer
	{
		private int deleted;

		public FileContentClassification? ClassifyWithoutReading(string path) =>
			inner.ClassifyWithoutReading(path);

		public ValueTask<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default) =>
			inner.IsTextFileAsync(path, cancellationToken);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.GetTextFileMetricsAsync(path, cancellationToken);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.TryReadAsTextAsync(path, cancellationToken);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			inner.TryReadAsTextAsync(path, maxSizeForFullRead, cancellationToken);

		public ValueTask<ContentReadFact> ReadFactAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default)
		{
			if (ProjectTreePathIdentity.CanonicalComparer.Equals(path, pathToDelete) &&
			    Interlocked.Exchange(ref deleted, 1) == 0)
			{
				File.Delete(path);
			}
			return inner.ReadFactAsync(path, maxSizeForFullRead, cancellationToken);
		}
	}
}
