using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit;

public sealed class FileSystemScannerEmptyFilesTests
{
	[Fact]
	public void GetExtensionsWithIgnoreOptionCounts_WhenIgnoreEmptyFilesDisabled_IncludesEmptyFileExtensionsAndCountsThem()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("empty.txt", string.Empty);
		temp.CreateFile(Path.Combine("docs", "empty.md"), string.Empty);
		temp.CreateFile(Path.Combine("src", "filled.cs"), "class C {}");

		var scanner = new FileSystemScanner();

		var result = scanner.GetExtensionsWithIgnoreOptionCounts(temp.Path, CreateRules(ignoreEmptyFiles: false), cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(result.Value.Extensions.SetEquals([".txt", ".md", ".cs"]));
		Assert.Equal(2, result.Value.IgnoreOptionCounts.EmptyFiles);
	}

	[Fact]
	public void GetExtensionsWithIgnoreOptionCounts_WhenIgnoreEmptyFilesEnabled_ExcludesEmptyFileExtensionsButPreservesCounts()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("empty.txt", string.Empty);
		temp.CreateFile(Path.Combine("docs", "empty.md"), string.Empty);
		temp.CreateFile(Path.Combine("src", "filled.cs"), "class C {}");

		var scanner = new FileSystemScanner();

		var result = scanner.GetExtensionsWithIgnoreOptionCounts(temp.Path, CreateRules(ignoreEmptyFiles: true), cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(result.Value.Extensions.SetEquals([".cs"]));
		Assert.Equal(2, result.Value.IgnoreOptionCounts.EmptyFiles);
	}

	[Fact]
	public void GetRootFileExtensionsWithIgnoreOptionCounts_EmptyExtensionlessRootFile_IncrementsEmptyAndExtensionlessCounters()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("README", string.Empty);
		temp.CreateFile("filled.cs", "class C {}");

		var scanner = new FileSystemScanner();

		var result = scanner.GetRootFileExtensionsWithIgnoreOptionCounts(temp.Path, CreateRules(ignoreEmptyFiles: true), cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(result.Value.Extensions.SetEquals([".cs"]));
		Assert.Equal(1, result.Value.IgnoreOptionCounts.EmptyFiles);
		Assert.Equal(1, result.Value.IgnoreOptionCounts.ExtensionlessFiles);
	}

	private static IgnoreRules CreateRules(bool ignoreEmptyFiles)
	{
		return new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>())
		{
			IgnoreEmptyFiles = ignoreEmptyFiles
		};
	}
}
