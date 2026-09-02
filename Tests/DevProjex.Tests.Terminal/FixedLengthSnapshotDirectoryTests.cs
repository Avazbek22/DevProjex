namespace DevProjex.Tests.Terminal;

public sealed class FixedLengthSnapshotDirectoryTests
{
	[Fact]
	public void BuilderKeepsTotalPathAndLeafStableAcrossDifferentTemporaryRoots()
	{
		var shortRoot = Path.Combine(Path.GetTempPath(), "u");
		var longRoot = Path.Combine(Path.GetTempPath(), "runneradmin");
		const string projectName = "0123456789abcdef0123456789abcdef";
		const int totalPathLength = 160;

		var shortPath = FixedLengthSnapshotDirectory.BuildPath(
			shortRoot,
			totalPathLength,
			projectName,
			new string('a', 32));
		var longPath = FixedLengthSnapshotDirectory.BuildPath(
			longRoot,
			totalPathLength,
			projectName,
			new string('b', 32));

		Assert.Equal(totalPathLength, shortPath.Path.Length);
		Assert.Equal(totalPathLength, longPath.Path.Length);
		Assert.Equal(projectName, Path.GetFileName(shortPath.Path));
		Assert.Equal(projectName, Path.GetFileName(longPath.Path));
		Assert.Equal(shortPath.OwnedRoot.Length, longPath.OwnedRoot.Length);
		Assert.NotEqual(
			Path.GetFileName(shortPath.OwnedRoot).Length,
			Path.GetFileName(longPath.OwnedRoot).Length);
	}

	[Theory]
	[InlineData("/tmp")]
	[InlineData("/private/tmp")]
	public void BuilderKeepsSnapshotBaselineLengthForUnixPhysicalRoots(
		string temporaryRoot)
	{
		const int totalPathLength = 91;
		const string projectName = "0123456789abcdef0123456789abcdef";

		var result = FixedLengthSnapshotDirectory.BuildPath(
			temporaryRoot,
			totalPathLength,
			projectName,
			new string('a', 32));

		Assert.Equal(totalPathLength, result.Path.Length);
		Assert.Equal(projectName, Path.GetFileName(result.Path));
		Assert.StartsWith(
			Path.GetFullPath(temporaryRoot),
			result.Path,
			StringComparison.Ordinal);
	}

	[Fact]
	public void TemporaryRootUsesPhysicalUnixPath()
	{
		var temporaryRoot = FixedLengthSnapshotDirectory.ResolveTemporaryRoot();

		if (OperatingSystem.IsMacOS())
			Assert.Equal("/private/tmp", temporaryRoot);
		else if (OperatingSystem.IsLinux())
			Assert.Equal("/tmp", temporaryRoot);
		else
			Assert.Equal(Path.GetTempPath(), temporaryRoot);
	}

	[Fact]
	public void JsonLengthBuilderCompensatesForPlatformPathEscaping()
	{
		const int serializedValueLength = 132;
		var preservedLeafName = Path.Combine(
			new string('a', 32),
			"DevProjex-Tui-Progress-Project");

		var result = FixedLengthSnapshotDirectory.BuildPathForArgumentJsonLength(
			FixedLengthSnapshotDirectory.ResolveTemporaryRoot(),
			serializedValueLength,
			preservedLeafName,
			new string('b', 32));
		var serialized = CliArgumentVectorFormatter.Format([result.Path]);

		Assert.Equal(
			serializedValueLength,
			serialized.Length - "argv[0] = ".Length);
		Assert.Equal(
			"DevProjex-Tui-Progress-Project",
			Path.GetFileName(result.Path));
	}

	[Fact]
	public void DisposeRemovesReadOnlyGitObjectFilesOnWindows()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("Read-only file attributes affect recursive directory deletion on Windows.");

		var temporaryRoot = FixedLengthSnapshotDirectory.ResolveTemporaryRoot().TrimEnd(
			Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar);
		using var directory = new FixedLengthSnapshotDirectory(temporaryRoot.Length + 49);
		var objectDirectory = Path.Combine(directory.Path, ".git", "objects", "10");
		Directory.CreateDirectory(objectDirectory);
		var objectPath = Path.Combine(objectDirectory, new string('a', 38));
		File.WriteAllText(objectPath, "object");
		File.SetAttributes(objectPath, File.GetAttributes(objectPath) | FileAttributes.ReadOnly);

		directory.Dispose();

		Assert.False(Directory.Exists(directory.Path));
	}
}
