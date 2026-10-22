using DevProjex.Avalonia.Services;
using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit;

public sealed class FlatpakPortalStageTests
{
	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	public async Task EveryAsyncStageIsNonBlockingCancelableAndDisposedExactlyOnce(int stage)
	{
		var session = new Session(stage);
		using var cancellation = new CancellationTokenSource();
		var pending = FlatpakDocumentPortal.QueryHostPathAsync("test", "abc", _ => session, cancellation.Token, Timeout.InfiniteTimeSpan);
		await session.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
		Assert.False(pending.IsCompleted);
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
		Assert.Equal(1, session.Disposals);
		session.Release.TrySetResult();
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	public async Task EveryAsyncStageHasABoundedDeadlineAndReleasesTheConnection(int stage)
	{
		var session = new Session(stage);
		Assert.Null(await FlatpakDocumentPortal.QueryHostPathAsync("test", "abc", _ => session,
			TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(30)).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
		Assert.True(session.Entered.Task.IsCompletedSuccessfully);
		Assert.Equal(1, session.Disposals);
		session.Release.TrySetResult();
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	public async Task EveryAsyncStageFallsBackOnIoFailureWithoutLeakingTheSession(int stage)
	{
		var session = new Session(stage);
		var pending = FlatpakDocumentPortal.QueryHostPathAsync("test", "abc", _ => session, TestContext.Current.CancellationToken, Timeout.InfiniteTimeSpan);
		await session.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
		Assert.False(pending.IsCompleted);
		session.Release.SetException(new IOException("unavailable"));
		Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
		Assert.Equal(1, session.Disposals);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("relative")]
	[InlineData("/home/a\0b")]
	public async Task MissingOrUnusableHostEntryKeepsTheOriginalDocumentPath(string? host)
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, "test.app"));
		const string document = "/run/user/1000/doc/abc/project/file.cs";
		var resolver = new FlatpakHostPathResolver((id, _) => { Assert.Equal("abc", id); return Task.FromResult(host); });
		Assert.Equal(document, await resolver.ResolveAsync(document, TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task VersionFourNeverCallsTheUnavailableMethod()
	{
		var session = new Session(-1) { Version = 4 };
		Assert.Null(await FlatpakDocumentPortal.QueryHostPathAsync("test", "abc", _ => session, TestContext.Current.CancellationToken));
		Assert.Equal(0, session.PathQueries);
		Assert.Equal(1, session.Disposals);
	}

	private sealed class Session(int blockedStage) : IFlatpakDocumentPortalSession
	{
		public uint Version { get; init; } = 5;
		public int Disposals { get; private set; }
		public int PathQueries { get; private set; }
		public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public Task ConnectAsync() => Stage(0);
		public async Task<uint> GetVersionAsync() { await Stage(1); return Version; }
		public async Task<string?> GetHostPathAsync(string id) { Assert.Equal("abc", id); PathQueries++; await Stage(2); return "/home/project"; }
		public void Dispose() => Disposals++;
		private Task Stage(int stage)
		{
			if (stage != blockedStage) return Task.CompletedTask;
			Entered.SetResult();
			return Release.Task;
		}
	}
}
