using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class SecretRedactionSessionWarmUpTests
{
	[Fact]
	public async Task BeginWarmUp_AfterTransientFailure_RetriesAndRetainsSuccessfulInitialization()
	{
		var detector = new FailOnceWarmUpDetector();
		using var session = new SecretRedactionSession(detector);

		await Assert.ThrowsAsync<IOException>(() => session.BeginWarmUp());

		var retry = session.BeginWarmUp();
		Assert.Same(retry, session.BeginWarmUp());
		await retry.WaitAsync(TestContext.Current.CancellationToken);

		await session.BeginWarmUp().WaitAsync(TestContext.Current.CancellationToken);
		Assert.Equal(2, detector.WarmUpCalls);
	}

	private sealed class FailOnceWarmUpDetector : ISecretDetector
	{
		private int _warmUpCalls;

		public int WarmUpCalls => Volatile.Read(ref _warmUpCalls);

		public void WarmUp(CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (Interlocked.Increment(ref _warmUpCalls) == 1)
				throw new IOException("Transient detector initialization failure.");
		}

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) => [];
	}
}
