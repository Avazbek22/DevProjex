using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit;

public sealed class TreeBuilderEmptyFilesTests
{
	[Fact]
	public void Build_WhenIgnoreEmptyFilesDisabled_IncludesZeroByteFiles()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("empty.txt", string.Empty);
		temp.CreateFile("filled.txt", "content");

		var builder = new TreeBuilder();

		var result = builder.Build(temp.Path, CreateOptions([".txt"], [], ignoreEmptyFiles: false, ignoreEmptyFolders: false), cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(2, result.Root.Children.Count);
		Assert.Contains(result.Root.Children, child => child.Name == "empty.txt");
		Assert.Contains(result.Root.Children, child => child.Name == "filled.txt");
	}

	[Fact]
	public void Build_WhenIgnoreEmptyFilesEnabled_ExcludesZeroByteFilesButKeepsNonEmptySiblings()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("empty.txt", string.Empty);
		temp.CreateFile("filled.txt", "content");

		var builder = new TreeBuilder();

		var result = builder.Build(temp.Path, CreateOptions([".txt"], [], ignoreEmptyFiles: true, ignoreEmptyFolders: false), cancellationToken: TestContext.Current.CancellationToken);

		Assert.Single(result.Root.Children);
		Assert.Equal("filled.txt", result.Root.Children[0].Name);
	}

	[Fact]
	public void Build_WhenIgnoreEmptyFilesEnabled_KeepsDirectoryWithoutVisibleChildren_WhenIgnoreEmptyFoldersDisabled()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "empty.txt"), string.Empty);

		var builder = new TreeBuilder();

		var result = builder.Build(temp.Path, CreateOptions([".txt"], ["src"], ignoreEmptyFiles: true, ignoreEmptyFolders: false), cancellationToken: TestContext.Current.CancellationToken);

		var src = Assert.Single(result.Root.Children);
		Assert.Equal("src", src.Name);
		Assert.Empty(src.Children);
	}

	[Fact]
	public void Build_WhenIgnoreEmptyFilesEnabled_PrunesDirectoryWithoutVisibleChildren_WhenIgnoreEmptyFoldersEnabled()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "empty.txt"), string.Empty);

		var builder = new TreeBuilder();

		var result = builder.Build(temp.Path, CreateOptions([".txt"], ["src"], ignoreEmptyFiles: true, ignoreEmptyFolders: true), cancellationToken: TestContext.Current.CancellationToken);

		Assert.Empty(result.Root.Children);
	}

	private static TreeFilterOptions CreateOptions(
		IReadOnlyCollection<string> allowedExtensions,
		IReadOnlyCollection<string> allowedRootFolders,
		bool ignoreEmptyFiles,
		bool ignoreEmptyFolders)
	{
		return new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(allowedExtensions, StringComparer.OrdinalIgnoreCase),
			AllowedRootFolders: new HashSet<string>(allowedRootFolders, PathComparer.Default),
			IgnoreRules: new IgnoreRules(
				IgnoreHiddenFolders: false,
				IgnoreHiddenFiles: false,
				IgnoreDotFolders: false,
				IgnoreDotFiles: false,
				SmartIgnoredFolders: new HashSet<string>(),
				SmartIgnoredFiles: new HashSet<string>())
			{
				IgnoreEmptyFiles = ignoreEmptyFiles,
				IgnoreEmptyFolders = ignoreEmptyFolders
			});
	}
}
