namespace DevProjex.Tests.Integration;

public sealed class FileSystemEntryEnumeratorContractIntegrationTests
{
	[Fact]
	public void EnumerateEntries_ProjectsStableMetadataAndForwardSlashRelativePaths()
	{
		using var temp = new TemporaryDirectory();
		var srcPath = temp.CreateDirectory("src");
		var filePath = temp.CreateFile("src/app.cs", "class App {}");
		var docsPath = temp.CreateDirectory("src/docs");

		var entries = FileSystemEntryEnumerator
			.EnumerateEntries(srcPath, "src")
			.OrderBy(static entry => entry.Name, StringComparer.Ordinal)
			.ToArray();

		var file = Assert.Single(entries, static entry => entry.Name == "app.cs");
		Assert.False(file.IsDirectory);
		Assert.Equal(NormalizeFullPath(filePath), NormalizeFullPath(file.FullPath));
		Assert.Equal("src/app.cs", file.RelativePath);
		Assert.Equal(new FileInfo(filePath).Length, file.Length);

		var docs = Assert.Single(entries, static entry => entry.Name == "docs");
		Assert.True(docs.IsDirectory);
		Assert.Equal(NormalizeFullPath(docsPath), NormalizeFullPath(docs.FullPath));
		Assert.Equal("src/docs", docs.RelativePath);
		Assert.Equal(0, docs.Length);
	}

	[Fact]
	public void EnumerateDirectoriesAndFiles_KeepSingleKindContracts()
	{
		using var temp = new TemporaryDirectory();
		var srcPath = temp.CreateDirectory("src");
		var filePath = temp.CreateFile("src/app.cs", "class App {}");
		var docsPath = temp.CreateDirectory("src/docs");

		var directories = FileSystemEntryEnumerator.EnumerateDirectories(srcPath, "src").ToArray();
		var files = FileSystemEntryEnumerator.EnumerateFiles(srcPath, "src").ToArray();

		var directory = Assert.Single(directories);
		Assert.Equal("docs", directory.Name);
		Assert.Equal(NormalizeFullPath(docsPath), NormalizeFullPath(directory.FullPath));
		Assert.Equal("src/docs", directory.RelativePath);

		var file = Assert.Single(files);
		Assert.Equal("app.cs", file.Name);
		Assert.Equal(NormalizeFullPath(filePath), NormalizeFullPath(file.FullPath));
		Assert.Equal("src/app.cs", file.RelativePath);
		Assert.Equal(new FileInfo(filePath).Length, file.Length);
	}

	[Fact]
	public void EnumerateEntries_ExcludesDirectoryAndFileReparsePoints()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("target/app.cs", "class App {}");

		var directoryLink = Path.Combine(temp.Path, "linked-target");
		var fileLink = Path.Combine(temp.Path, "linked-app.cs");
		if (!TryCreateDirectorySymlink(directoryLink, Path.Combine(temp.Path, "target")) ||
		    !TryCreateFileSymlink(fileLink, Path.Combine(temp.Path, "target", "app.cs")))
		{
			return;
		}

		var entries = FileSystemEntryEnumerator.EnumerateEntries(temp.Path).ToArray();
		var directories = FileSystemEntryEnumerator.EnumerateDirectories(temp.Path).ToArray();
		var files = FileSystemEntryEnumerator.EnumerateFiles(temp.Path).ToArray();

		Assert.DoesNotContain(entries, static entry => entry.Name == "linked-target" || entry.Name == "linked-app.cs");
		Assert.DoesNotContain(directories, static entry => entry.Name == "linked-target");
		Assert.DoesNotContain(files, static entry => entry.Name == "linked-app.cs");
	}

	[Fact]
	public void EnumerateEntries_MissingDirectoryDoesNotHideFilesystemFailure()
	{
		using var temp = new TemporaryDirectory();
		var missingPath = Path.Combine(temp.Path, "missing");

		Assert.Throws<DirectoryNotFoundException>(() => FileSystemEntryEnumerator.EnumerateEntries(missingPath).ToArray());
	}

	private static string NormalizeFullPath(string path)
	{
		return Path.GetFullPath(path)
			.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
	}

	private static bool TryCreateDirectorySymlink(string linkPath, string targetPath)
	{
		try
		{
			Directory.CreateSymbolicLink(linkPath, targetPath);
			return Directory.Exists(linkPath) &&
			       File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			return false;
		}
	}

	private static bool TryCreateFileSymlink(string linkPath, string targetPath)
	{
		try
		{
			File.CreateSymbolicLink(linkPath, targetPath);
			return File.Exists(linkPath) &&
			       File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			return false;
		}
	}
}
