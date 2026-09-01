using DevProjex.Application.Diagnostics;
using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit;

public sealed class GitIgnoreMatcherLoadSessionTests
{
	[Fact]
	public async Task Load_ConcurrentRequestsForOneSource_ExecutesLoaderOnce()
	{
		using var loaderEntered = new ManualResetEventSlim();
		using var releaseLoader = new ManualResetEventSlim();
		var loaderCalls = 0;
		var sourcePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), ".gitignore");
		var session = new GitIgnoreMatcherLoadSession((_, _) =>
		{
			Interlocked.Increment(ref loaderCalls);
			loaderEntered.Set();
			releaseLoader.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
			return GitIgnoreMatcherLoadResult.NotFound;
		});

		using var measurement = IgnorePipelineDiagnostics.BeginMeasurement();
		var requests = Enumerable.Range(0, 32)
			.Select(_ => Task.Run(() => session.Load(Path.GetDirectoryName(sourcePath)!, sourcePath)))
			.ToArray();

		Assert.True(loaderEntered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
		releaseLoader.Set();
		var results = await Task.WhenAll(requests);

		Assert.All(results, result => Assert.Equal(GitIgnoreMatcherLoadStatus.NotFound, result.Status));
		Assert.Equal(1, Volatile.Read(ref loaderCalls));

		var diagnostics = measurement.Capture();
		Assert.Equal(32, diagnostics.GitIgnoreLoadRequests);
		Assert.Equal(1, diagnostics.GitIgnoreLoadExecutions);
		Assert.Equal(31, diagnostics.GitIgnoreLoadReuses);
	}

	[Fact]
	public void Load_TypedFailure_IsStableOnlyWithinOneOperationSession()
	{
		var loaderCalls = 0;
		var sourcePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), ".gitignore");
		GitIgnoreMatcherLoadResult Loader(string _, string __)
		{
			Interlocked.Increment(ref loaderCalls);
			return GitIgnoreMatcherLoadResult.ReadFailure;
		}

		var firstSession = new GitIgnoreMatcherLoadSession(Loader);
		var first = firstSession.Load(Path.GetDirectoryName(sourcePath)!, sourcePath);
		var repeated = firstSession.Load(Path.GetDirectoryName(sourcePath)!, sourcePath);
		var secondSession = new GitIgnoreMatcherLoadSession(Loader);
		var nextOperation = secondSession.Load(Path.GetDirectoryName(sourcePath)!, sourcePath);

		Assert.Equal(GitIgnoreMatcherLoadStatus.ReadFailure, first.Status);
		Assert.Equal(first, repeated);
		Assert.Equal(first, nextOperation);
		Assert.Equal(2, loaderCalls);
	}

	[Fact]
	public void Seed_PreparsedMatcher_ReusesRulesSnapshotWithoutFilesystemReload()
	{
		var scopePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
		var sourcePath = Path.Combine(scopePath, ".gitignore");
		var matcher = new ScopedGitIgnoreMatcher(
			scopePath,
			GitIgnoreMatcher.Build(scopePath, ["*.generated"]));
		var loaderCalls = 0;
		var session = new GitIgnoreMatcherLoadSession((_, _) =>
		{
			Interlocked.Increment(ref loaderCalls);
			return GitIgnoreMatcherLoadResult.ReadFailure;
		});
		session.Seed([matcher]);
		session.Seed([matcher]);

		using var measurement = IgnorePipelineDiagnostics.BeginMeasurement();
		var result = session.Load(scopePath, sourcePath);

		Assert.Equal(GitIgnoreMatcherLoadStatus.Loaded, result.Status);
		Assert.Same(matcher, result.Matcher);
		Assert.Equal(0, loaderCalls);
		Assert.Equal(0, measurement.Capture().GitIgnoreLoadExecutions);
		Assert.Equal(1, measurement.Capture().GitIgnoreLoadReuses);
	}

	[Fact]
	public void Load_CancellationTokenReachesSourceLoader()
	{
		using var cancellation = new CancellationTokenSource();
		var sourcePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), ".gitignore");
		var observedToken = default(CancellationToken);
		var session = new GitIgnoreMatcherLoadSession((_, _, cancellationToken) =>
		{
			observedToken = cancellationToken;
			cancellation.Cancel();
			cancellationToken.ThrowIfCancellationRequested();
			return GitIgnoreMatcherLoadResult.NotFound;
		});

		Assert.Throws<OperationCanceledException>(() =>
			session.LoadWithCancellation(
				Path.GetDirectoryName(sourcePath)!,
				sourcePath,
				cancellation.Token));
		Assert.Equal(cancellation.Token, observedToken);
	}

	[Fact]
	public void Load_CanceledAttempt_DoesNotPoisonSessionCache()
	{
		using var cancellation = new CancellationTokenSource();
		var sourcePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), ".gitignore");
		var loaderCalls = 0;
		var session = new GitIgnoreMatcherLoadSession((_, _, cancellationToken) =>
		{
			if (Interlocked.Increment(ref loaderCalls) == 1)
			{
				cancellation.Cancel();
				cancellationToken.ThrowIfCancellationRequested();
			}

			return GitIgnoreMatcherLoadResult.NotFound;
		});

		Assert.Throws<OperationCanceledException>(() =>
			session.LoadWithCancellation(
				Path.GetDirectoryName(sourcePath)!,
				sourcePath,
				cancellation.Token));

		var retried = session.Load(Path.GetDirectoryName(sourcePath)!, sourcePath);

		Assert.Equal(GitIgnoreMatcherLoadStatus.NotFound, retried.Status);
		Assert.Equal(2, loaderCalls);
	}

	[Fact]
	public void Load_CaseDistinctPhysicalSourcesRemainIndependent()
	{
		var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
		var upperSource = Path.Combine(root, "Repo", ".gitignore");
		var lowerSource = Path.Combine(root, "repo", ".gitignore");
		var loaderCalls = 0;
		var session = new GitIgnoreMatcherLoadSession((_, _) =>
		{
			Interlocked.Increment(ref loaderCalls);
			return GitIgnoreMatcherLoadResult.NotFound;
		});

		session.Load(Path.GetDirectoryName(upperSource)!, upperSource);
		session.Load(Path.GetDirectoryName(lowerSource)!, lowerSource);
		session.Load(Path.GetDirectoryName(upperSource)!, upperSource);

		Assert.Equal(2, loaderCalls);
	}
}
