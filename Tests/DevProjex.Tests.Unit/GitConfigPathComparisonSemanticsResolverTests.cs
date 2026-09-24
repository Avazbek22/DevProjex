using System.Diagnostics;

namespace DevProjex.Tests.Unit;

public sealed class GitConfigPathComparisonSemanticsResolverTests
{
	/// <summary>
	/// A repository whose settings cannot be read backs off rather than being probed on every
	/// call, and is read again once the back-off has passed.
	/// </summary>
	/// <remarks>
	/// The read is attempted twice before an unreadable answer is believed, so the counts here
	/// are in pairs. That is the point of the pair: a single miss is not what puts a repository
	/// out of reach.
	/// </remarks>
	[Fact]
	public void Resolve_RetriesUnavailableRepositorySemanticsAfterBackoff()
	{
		using var workspace = new TemporaryDirectory();
		var repositoryRoot = workspace.CreateFolder("repository");
		workspace.CreateFolder("repository/.git");
		var now = new DateTime(2026, 8, 20, 1, 0, 0, DateTimeKind.Utc);
		var resolutionCount = 0;
		var resolver = new GitConfigPathComparisonSemanticsResolver(
			(_, _) => ++resolutionCount <= 2
				? new GitPathComparisonSemantics(
					IgnoreCase: true,
					NormalizeUnicode: true,
					IsAuthoritative: false)
				: new GitPathComparisonSemantics(
					IgnoreCase: false,
					NormalizeUnicode: false),
			() => now,
			TimeSpan.FromMinutes(1));

		var unavailable = resolver.Resolve(repositoryRoot);
		var coalesced = resolver.Resolve(repositoryRoot);
		now = now.AddMinutes(1);
		var recovered = resolver.Resolve(repositoryRoot);
		var cached = resolver.Resolve(repositoryRoot);

		Assert.False(unavailable.IsAuthoritative);
		Assert.False(coalesced.IsAuthoritative);
		Assert.True(recovered.IsAuthoritative);
		Assert.False(recovered.IgnoreCase);
		Assert.Equal(recovered, cached);
		// Two reads to disbelieve the first miss, one to recover after the back-off. The call
		// between them and the call after recovery are served from the cache and read nothing.
		Assert.Equal(3, resolutionCount);
	}

	/// <summary>
	/// One miss does not put a repository out of reach: the next call sees the settings.
	/// </summary>
	[Fact]
	public void Resolve_DoesNotBelieveASingleFailedRead()
	{
		using var workspace = new TemporaryDirectory();
		var repositoryRoot = workspace.CreateFolder("repository");
		workspace.CreateFolder("repository/.git");
		var now = new DateTime(2026, 8, 20, 1, 0, 0, DateTimeKind.Utc);
		var resolutionCount = 0;
		var resolver = new GitConfigPathComparisonSemanticsResolver(
			(_, _) => ++resolutionCount == 1
				? new GitPathComparisonSemantics(
					IgnoreCase: true,
					NormalizeUnicode: true,
					IsAuthoritative: false)
				: new GitPathComparisonSemantics(
					IgnoreCase: false,
					NormalizeUnicode: false),
			() => now,
			TimeSpan.FromMinutes(1));

		var first = resolver.Resolve(repositoryRoot);
		var second = resolver.Resolve(repositoryRoot);

		// The miss is not returned to anyone and never reaches the cache, so the settings the
		// second read found are what both calls see.
		Assert.True(first.IsAuthoritative);
		Assert.False(first.IgnoreCase);
		Assert.Equal(first, second);
		Assert.Equal(2, resolutionCount);
	}

	/// <summary>
	/// A repository that fails every read is read twice and then left alone until the back-off
	/// passes, rather than being probed by every caller.
	/// </summary>
	[Fact]
	public void Resolve_StopsReadingARepositoryThatAlwaysFails()
	{
		using var workspace = new TemporaryDirectory();
		var repositoryRoot = workspace.CreateFolder("repository");
		workspace.CreateFolder("repository/.git");
		var now = new DateTime(2026, 8, 20, 1, 0, 0, DateTimeKind.Utc);
		var resolutionCount = 0;
		var resolver = new GitConfigPathComparisonSemanticsResolver(
			(_, _) =>
			{
				resolutionCount++;
				return new GitPathComparisonSemantics(
					IgnoreCase: true,
					NormalizeUnicode: true,
					IsAuthoritative: false);
			},
			() => now,
			TimeSpan.FromMinutes(1));

		var first = resolver.Resolve(repositoryRoot);
		var readsAfterFirstCall = resolutionCount;
		for (var call = 0; call < 8; call++)
			_ = resolver.Resolve(repositoryRoot);

		Assert.False(first.IsAuthoritative);
		// The retry is one extra read, not a read for every caller: eight further calls inside
		// the back-off read nothing at all.
		Assert.Equal(2, readsAfterFirstCall);
		Assert.Equal(2, resolutionCount);
	}

	/// <summary>
	/// The counts the cases above rest on are counts of something. A repository that is read
	/// successfully is read once, so a resolver that had stopped reading at all would fail here
	/// rather than quietly satisfy every assertion that a count is small.
	/// </summary>
	[Fact]
	public void Resolve_ReadsOnceWhenTheFirstReadSucceeds()
	{
		using var workspace = new TemporaryDirectory();
		var repositoryRoot = workspace.CreateFolder("repository");
		workspace.CreateFolder("repository/.git");
		var resolutionCount = 0;
		var resolver = new GitConfigPathComparisonSemanticsResolver(
			(_, _) =>
			{
				resolutionCount++;
				return new GitPathComparisonSemantics(IgnoreCase: false, NormalizeUnicode: false);
			},
			static () => new DateTime(2026, 8, 20, 1, 0, 0, DateTimeKind.Utc),
			TimeSpan.FromMinutes(1));

		var resolved = resolver.Resolve(repositoryRoot);

		Assert.True(resolved.IsAuthoritative);
		Assert.Equal(1, resolutionCount);
	}

	[Fact]
	public void Resolve_KeepsAuthoritativeRepositorySemanticsCached()
	{
		using var workspace = new TemporaryDirectory();
		var repositoryRoot = workspace.CreateFolder("repository");
		workspace.CreateFolder("repository/.git");
		var now = new DateTime(2026, 8, 20, 1, 0, 0, DateTimeKind.Utc);
		var resolutionCount = 0;
		var expected = new GitPathComparisonSemantics(
			IgnoreCase: true,
			NormalizeUnicode: OperatingSystem.IsMacOS());
		var resolver = new GitConfigPathComparisonSemanticsResolver(
			(_, _) =>
			{
				resolutionCount++;
				return expected;
			},
			() => now,
			TimeSpan.FromSeconds(1));

		Assert.Equal(expected, resolver.Resolve(repositoryRoot));
		now = now.AddDays(1);
		Assert.Equal(expected, resolver.Resolve(repositoryRoot));
		Assert.Equal(1, resolutionCount);
	}

	[Fact]
	public void Resolve_DoesNotShareCacheEntriesBetweenCaseDistinctRepositoryRoots()
	{
		using var workspace = new TemporaryDirectory();
		var upperRoot = workspace.CreateFolder("Repo");
		workspace.CreateFolder("Repo/.git");
		var lowerRoot = workspace.CreateFolder("repo");
		workspace.CreateFolder("repo/.git");
		var physicalRoots = Directory.GetDirectories(workspace.Path);
		if (physicalRoots.Length != 2)
		{
			Assert.Skip("The temporary file system does not support case-distinct sibling directories.");
			return;
		}

		var resolutionCount = 0;
		var resolver = new GitConfigPathComparisonSemanticsResolver(
			(repositoryRoot, _) =>
			{
				Interlocked.Increment(ref resolutionCount);
				return new GitPathComparisonSemantics(
					IgnoreCase: repositoryRoot.EndsWith("Repo", StringComparison.Ordinal),
					NormalizeUnicode: false);
			},
			static () => new DateTime(2026, 8, 30, 1, 0, 0, DateTimeKind.Utc),
			TimeSpan.FromMinutes(1));

		var upper = resolver.Resolve(upperRoot);
		var lower = resolver.Resolve(lowerRoot);

		Assert.True(upper.IgnoreCase);
		Assert.False(lower.IgnoreCase);
		Assert.Equal(upper, resolver.Resolve(upperRoot));
		Assert.Equal(lower, resolver.Resolve(lowerRoot));
		Assert.Equal(2, resolutionCount);
	}

	[Fact]
	public async Task Invalidate_DuringResolution_PreventsLateResultFromRepopulatingCache()
	{
		using var workspace = new TemporaryDirectory();
		var repositoryRoot = workspace.CreateFolder("repository");
		workspace.CreateFolder("repository/.git");
		using var firstResolutionStarted = new ManualResetEventSlim();
		using var releaseFirstResolution = new ManualResetEventSlim();
		var resolutionCount = 0;
		var stale = new GitPathComparisonSemantics(IgnoreCase: true, NormalizeUnicode: false);
		var fresh = new GitPathComparisonSemantics(IgnoreCase: false, NormalizeUnicode: false);
		var resolver = new GitConfigPathComparisonSemanticsResolver(
			(_, _) =>
			{
				var resolution = Interlocked.Increment(ref resolutionCount);
				if (resolution == 1)
				{
					firstResolutionStarted.Set();
					Assert.True(releaseFirstResolution.Wait(TimeSpan.FromSeconds(30)));
					return stale;
				}

				return fresh;
			},
			static () => new DateTime(2026, 8, 20, 1, 0, 0, DateTimeKind.Utc),
			TimeSpan.FromMinutes(1));

		var lateResolution = Task.Run(() => resolver.Resolve(repositoryRoot));
		Assert.True(firstResolutionStarted.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
		resolver.Invalidate(repositoryRoot);
		releaseFirstResolution.Set();

		Assert.Equal(stale, await lateResolution);
		Assert.Equal(fresh, resolver.Resolve(repositoryRoot));
		Assert.Equal(fresh, resolver.Resolve(repositoryRoot));
		Assert.Equal(2, resolutionCount);
	}

	[Fact]
	public async Task ConcurrentResolution_NewerStartCannotBeOverwrittenByOlderCompletion()
	{
		using var workspace = new TemporaryDirectory();
		var repositoryRoot = workspace.CreateFolder("repository");
		workspace.CreateFolder("repository/.git");
		using var olderResolutionStarted = new ManualResetEventSlim();
		using var releaseOlderResolution = new ManualResetEventSlim();
		var resolutionCount = 0;
		var older = new GitPathComparisonSemantics(IgnoreCase: true, NormalizeUnicode: false);
		var newer = new GitPathComparisonSemantics(IgnoreCase: false, NormalizeUnicode: true);
		var resolver = new GitConfigPathComparisonSemanticsResolver(
			(_, _) =>
			{
				var resolution = Interlocked.Increment(ref resolutionCount);
				if (resolution == 1)
				{
					olderResolutionStarted.Set();
					Assert.True(releaseOlderResolution.Wait(TimeSpan.FromSeconds(30)));
					return older;
				}

				return newer;
			},
			static () => new DateTime(2026, 8, 20, 1, 0, 0, DateTimeKind.Utc),
			TimeSpan.FromMinutes(1));

		var olderResolution = Task.Run(() => resolver.Resolve(repositoryRoot));
		Assert.True(olderResolutionStarted.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
		var newerResolution = await Task.Run(() => resolver.Resolve(repositoryRoot));
		releaseOlderResolution.Set();

		Assert.Equal(older, await olderResolution);
		Assert.Equal(newer, newerResolution);
		Assert.Equal(newer, resolver.Resolve(repositoryRoot));
		Assert.Equal(2, resolutionCount);
	}

	[Fact]
	public void TryRunGit_DescendantHoldingRedirectedPipesCannotStallTheProbe()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("The inherited Unix pipe scenario requires a POSIX shell script.");
			return;
		}

		using var workspace = new TemporaryDirectory();
		var repositoryRoot = workspace.CreateFolder("repository");
		var childPidPath = Path.Combine(workspace.Path, "child.pid");
		var executable = workspace.CreateFile(
			"git-probe",
			$"#!/bin/sh\n(sleep 30) &\necho $! > '{childPidPath.Replace("'", "'\\''", StringComparison.Ordinal)}'\n" +
			"printf 'local core.repositoryformatversion true\\n'\nexit 0\n");
		File.SetUnixFileMode(
			executable,
			UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

		try
		{
			var stopwatch = Stopwatch.StartNew();
			var succeeded = GitConfigPathComparisonSemanticsResolver.TryRunGit(
				repositoryRoot,
				out _,
				out _,
				executable);

			Assert.False(succeeded);
			Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Probe took {stopwatch.Elapsed}.");
		}
		finally
		{
			if (File.Exists(childPidPath) &&
			    int.TryParse(File.ReadAllText(childPidPath), out var processId))
			{
				try
				{
					using var process = Process.GetProcessById(processId);
					if (!process.HasExited)
						process.Kill(entireProcessTree: true);
				}
				catch (Exception exception) when (
					exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
				{
				}
			}
		}
	}
}
