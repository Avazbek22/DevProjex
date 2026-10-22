using DevProjex.Avalonia.Services;
using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit;

public sealed class FlatpakPortalFailureTests
{
	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData(" ")]
	public async Task MissingBusNeverCreatesAConnection(string? address)
	{
		Assert.Null(await FlatpakDocumentPortal.QueryHostPathAsync(address, "abc",
			_ => throw new InvalidOperationException(), TestContext.Current.CancellationToken));
	}

	[Theory]
	[InlineData(0u)]
	[InlineData(4u)]
	[InlineData(5u)]
	public async Task VersionGateAndDisposalAreEnforced(uint version)
	{
		var session = new PortalSession { Version = version, HostPath = "/mnt/project" };
		var result = await FlatpakDocumentPortal.QueryHostPathAsync("test", "abc", _ => session,
			TestContext.Current.CancellationToken);
		Assert.Equal(version >= 5 ? "/mnt/project" : null, result);
		Assert.Equal(version >= 5 ? 1 : 0, session.PathQueries);
		Assert.True(session.Disposed);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData(" ")]
	public async Task EmptyResponseFallsBackWithoutInventingAHostPath(string? host)
	{
		var session = new PortalSession { HostPath = host };
		Assert.Null(await FlatpakDocumentPortal.QueryHostPathAsync("test", "abc", _ => session,
			TestContext.Current.CancellationToken));
		Assert.True(session.Disposed);
	}

	[Fact]
	public async Task TimeoutDisposesTheConnectionAndReturnsFallback()
	{
		var session = new PortalSession { Hang = true };
		Assert.Null(await FlatpakDocumentPortal.QueryHostPathAsync("test", "abc", _ => session,
			TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(25)));
		Assert.True(session.Disposed);
	}

	[Fact]
	public async Task CallerCancellationIsNotReportedAsAPortalFailure()
	{
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		var session = new PortalSession { Hang = true };
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FlatpakDocumentPortal.QueryHostPathAsync(
			"test", "abc", _ => session, cancellation.Token));
		Assert.True(session.Disposed);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ConnectionAndMethodFailuresAreBounded(bool factoryThrows)
	{
		var session = new PortalSession { Fail = true };
		Assert.Null(await FlatpakDocumentPortal.QueryHostPathAsync("test", "abc",
			_ => factoryThrows ? throw new IOException("unavailable") : session,
			TestContext.Current.CancellationToken));
		Assert.Equal(!factoryThrows, session.Disposed);
	}

	[Theory]
	[InlineData("/home/person/project")]
	[InlineData("/run/user/not-a-uid/doc/abc/project")]
	[InlineData("/run/user/1000/not-doc/abc/project")]
	[InlineData("/run/user/1000/doc/abc/project/../outside")]
	public async Task OrdinaryPathsNeverQueryThePortal(string path)
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, "io.github.Avazbek22.DevProjex"));
		var resolver = new FlatpakHostPathResolver((_, _) => throw new InvalidOperationException());
		Assert.Equal(path, await resolver.ResolveAsync(path, TestContext.Current.CancellationToken));
	}

	private sealed class PortalSession : IFlatpakDocumentPortalSession
	{
		public uint Version { get; init; } = 5;
		public string? HostPath { get; init; }
		public bool Hang { get; init; }
		public bool Fail { get; init; }
		public bool Disposed { get; private set; }
		public int PathQueries { get; private set; }
		public Task ConnectAsync() => Fail ? Task.FromException(new IOException("unavailable")) :
			Hang ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task : Task.CompletedTask;
		public Task<uint> GetVersionAsync() => Task.FromResult(Version);
		public Task<string?> GetHostPathAsync(string id) { PathQueries++; return Task.FromResult(HostPath); }
		public void Dispose() => Disposed = true;
	}
}
