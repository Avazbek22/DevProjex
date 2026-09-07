using DevProjex.Application.Compression;
using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Integration;

public sealed class SecretRedactionPipelineConcurrencyIntegrationTests
{
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
}
