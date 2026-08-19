using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit;

public sealed class ProjectTreeInventoryScannerFailureTests
{
	[Fact]
	public void Read_WhenRootEnumerationHasTransientIoFailure_ReturnsIncompletePartialInventory()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("src/app.cs", "class App {}\n");

		var snapshot = Read(project.Path, (point, _) =>
		{
			if (point == FileSystemScanEnumerationPoint.RootDirectories)
				throw new IOException("Simulated root enumeration failure.");
		});

		Assert.True(snapshot.HadScanFailure);
		Assert.False(snapshot.RootAccessDenied);
		Assert.False(snapshot.HadAccessDenied);
		Assert.Single(snapshot.Entries);
	}

	[Fact]
	public void Read_WhenSubtreeEnumerationHasTransientIoFailure_KeepsOtherSubtreesAndMarksIncomplete()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("failed/lost.cs", "class Lost {}\n");
		project.CreateFile("healthy/kept.cs", "class Kept {}\n");
		var failedPath = Path.Combine(project.Path, "failed");

		var snapshot = Read(project.Path, (point, path) =>
		{
			if (point == FileSystemScanEnumerationPoint.DirectoryDiscovery &&
			    PathComparer.Default.Equals(path, failedPath))
			{
				throw new IOException("Simulated subtree enumeration failure.");
			}
		});

		Assert.True(snapshot.HadScanFailure);
		Assert.False(snapshot.RootAccessDenied);
		Assert.False(snapshot.HadAccessDenied);
		Assert.Contains(snapshot.Entries, static entry => entry.RelativePath.EndsWith("kept.cs", StringComparison.Ordinal));
		Assert.DoesNotContain(snapshot.Entries, static entry => entry.RelativePath.EndsWith("lost.cs", StringComparison.Ordinal));
	}

	[Fact]
	public void Read_WhenEnumerationThrowsUnexpectedException_Propagates()
	{
		using var project = new TemporaryDirectory();

		var exception = Assert.Throws<InvalidOperationException>(() => Read(project.Path, (point, _) =>
		{
			if (point == FileSystemScanEnumerationPoint.RootDirectories)
				throw new InvalidOperationException("Unexpected inventory failure.");
		}));

		Assert.Equal("Unexpected inventory failure.", exception.Message);
	}

	[Fact]
	public void Read_WhenRootEnumerationIsUnauthorized_PreservesAccessDeniedSemantics()
	{
		using var project = new TemporaryDirectory();

		var snapshot = Read(project.Path, (point, _) =>
		{
			if (point == FileSystemScanEnumerationPoint.RootDirectories)
				throw new UnauthorizedAccessException("Simulated access denial.");
		});

		Assert.True(snapshot.RootAccessDenied);
		Assert.True(snapshot.HadAccessDenied);
		Assert.False(snapshot.HadScanFailure);
		Assert.True(snapshot.GetEntry(0).IsAccessDenied);
	}

	private static ProjectTreeInventorySnapshot Read(
		string rootPath,
		Action<FileSystemScanEnumerationPoint, string> beforeEnumeration) =>
		ProjectTreeInventoryScanner.Read(
			rootPath,
			ProjectTreeGitIgnoreContexts.Disabled,
			static (_, _, _) => true,
			TestContext.Current.CancellationToken,
			beforeEnumeration);
}
