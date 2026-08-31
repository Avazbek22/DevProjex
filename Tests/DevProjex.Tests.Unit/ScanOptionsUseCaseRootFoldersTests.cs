namespace DevProjex.Tests.Unit;

public sealed class ScanOptionsUseCaseRootFoldersTests
{
	private static IgnoreRules CreateRules() => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(),
		SmartIgnoredFiles: new HashSet<string>());

	[Fact]
	public void GetRootFolders_SortsAndPreservesFlags()
	{
		var scanner = new StubFileSystemScanner
		{
			GetRootFolderNamesHandler = (_, _) => new ScanResult<List<string>>(
				["zeta", "Alpha", "beta"],
				RootAccessDenied: true,
				HadAccessDenied: false)
		};

		var useCase = new ScanOptionsUseCase(scanner);
		var result = useCase.GetRootFolders("/root", CreateRules(), cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(["Alpha", "beta", "zeta"], result.Value);
		Assert.True(result.RootAccessDenied);
		Assert.False(result.HadAccessDenied);
	}

	[Fact]
	public void GetRootFolders_PreCanceled_Throws()
	{
		var scanner = new StubFileSystemScanner();
		var useCase = new ScanOptionsUseCase(scanner);
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		Assert.Throws<OperationCanceledException>(() =>
			useCase.GetRootFolders("/root", CreateRules(), cts.Token));
	}

	[Fact]
	public void GetRootFolders_UsesOnlyRootFolderScannerPath()
	{
		var extensionsCalls = 0;
		var rootFolderCalls = 0;

		var scanner = new StubFileSystemScanner
		{
			GetExtensionsHandler = (_, _) =>
			{
				Interlocked.Increment(ref extensionsCalls);
				throw new InvalidOperationException("Extensions scan must not be used by GetRootFolders.");
			},
			GetRootFolderNamesHandler = (_, _) =>
			{
				Interlocked.Increment(ref rootFolderCalls);
				return new ScanResult<List<string>>(["src"], false, false);
			}
		};

		var useCase = new ScanOptionsUseCase(scanner);
		var result = useCase.GetRootFolders("/root", CreateRules(), cancellationToken: TestContext.Current.CancellationToken);

		Assert.Single(result.Value);
		Assert.Equal("src", result.Value[0]);
		Assert.Equal(1, rootFolderCalls);
		Assert.Equal(0, extensionsCalls);
	}

	[Fact]
	public void GetRootFolders_WhenScannerFails_PropagatesException()
	{
		var scanner = new StubFileSystemScanner
		{
			GetRootFolderNamesHandler = (_, _) => throw new InvalidOperationException("root scan failed")
		};

		var useCase = new ScanOptionsUseCase(scanner);

		var ex = Assert.Throws<InvalidOperationException>(() =>
			useCase.GetRootFolders("/root", CreateRules(), cancellationToken: TestContext.Current.CancellationToken));
		Assert.Contains("root scan failed", ex.Message, StringComparison.OrdinalIgnoreCase);
	}
}

