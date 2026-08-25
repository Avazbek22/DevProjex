namespace DevProjex.Tests.Unit;

public sealed class PathUtilityTests
{
	[Fact]
	public void Normalize_TrimsTrailingSeparators_AndPreservesRoot()
	{
		using var temp = new TemporaryDirectory();
		var folderPath = temp.CreateFolder("repo");
		var withSeparator = folderPath + Path.DirectorySeparatorChar;
		var withLegacySeparator = folderPath + '\\';

		Assert.Equal(folderPath, PathUtility.Normalize(withSeparator));
		Assert.Equal(
			OperatingSystem.IsWindows() ? folderPath : withLegacySeparator,
			PathUtility.Normalize(withLegacySeparator));

		var rootPath = Path.GetPathRoot(Path.GetTempPath())!;
		Assert.Equal(rootPath, PathUtility.Normalize(rootPath));
	}

	[Fact]
	public void NormalizeForCacheKey_CaseVariantBehavior_MatchesPlatform()
	{
		using var temp = new TemporaryDirectory();
		var path = temp.CreateFolder("RepoCase");
		var alteredCasePath = path.Replace("RepoCase", "rePOcAse", StringComparison.Ordinal);

		var first = PathUtility.NormalizeForCacheKey(path);
		var second = PathUtility.NormalizeForCacheKey(alteredCasePath);

		Assert.Equal(OperatingSystem.IsWindows(), string.Equals(first, second, StringComparison.Ordinal));
	}

	[Fact]
	public void Normalize_PreservesARealTrailingBackslashInUnixNames()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("Windows treats a backslash as a directory separator.");

		using var temp = new TemporaryDirectory();
		var ordinary = temp.CreateFolder("project");
		var withBackslash = temp.CreateFolder("project\\");

		Assert.NotEqual(PathUtility.Normalize(ordinary), PathUtility.Normalize(withBackslash));
		Assert.Equal(withBackslash, PathUtility.Normalize(withBackslash));
		Assert.False(PathUtility.IsPathInside(withBackslash, ordinary));
	}

	[Fact]
	public void PortableRelativePathsPreserveUnixBackslashesAsNameCharacters()
	{
		using var temp = new TemporaryDirectory();
		var project = temp.CreateFolder("project");
		var relative = OperatingSystem.IsWindows()
			? Path.Combine("folder", "name.txt")
			: "literal\\name.txt";
		var path = Path.Combine(project, relative);

		Assert.Equal(
			OperatingSystem.IsWindows() ? "folder/name.txt" : "literal\\name.txt",
			PathUtility.GetPortableRelativePath(project, path));
	}

	[Fact]
	public void IsPathInside_ReturnsTrue_ForRootAndDescendant()
	{
		using var temp = new TemporaryDirectory();
		var cacheRoot = temp.CreateFolder("RepoCache");
		var child = temp.CreateFolder(Path.Combine("RepoCache", "repo"));

		Assert.True(PathUtility.IsPathInside(cacheRoot, cacheRoot));
		Assert.True(PathUtility.IsPathInside(child, cacheRoot));
	}

	[Fact]
	public void IsPathInside_FileSystemRootContainsDescendant()
	{
		var rootPath = Path.GetPathRoot(Path.GetTempPath())!;
		var descendant = Path.Combine(rootPath, "DevProjex", "workspace");

		Assert.True(PathUtility.IsPathInside(descendant, rootPath));
	}

	[Fact]
	public void IsPathInside_ReturnsFalse_ForPrefixTrapSibling()
	{
		using var temp = new TemporaryDirectory();
		var cacheRoot = temp.CreateFolder("RepoCache");
		var sibling = temp.CreateFolder("RepoCache2");

		Assert.False(PathUtility.IsPathInside(sibling, cacheRoot));
	}

	[Fact]
	public void IsPathInside_CaseVariantBehavior_MatchesPlatform()
	{
		using var temp = new TemporaryDirectory();
		var cacheRoot = temp.CreateFolder("RepoCache");
		var descendant = temp.CreateFolder(Path.Combine("RepoCache", "RepoA"));
		var alteredCaseRoot = cacheRoot.Replace("RepoCache", "rePOcAche", StringComparison.Ordinal);

		Assert.Equal(OperatingSystem.IsWindows(), PathUtility.IsPathInside(descendant, alteredCaseRoot));
	}

	[Fact]
	public void IsPathInside_TreatsBackslashAsASeparatorOnlyOnWindows()
	{
		using var temp = new TemporaryDirectory();
		var cacheRoot = temp.CreateFolder("RepoCache");
		var child = temp.CreateFolder(Path.Combine("RepoCache", "repo"));
		var legacyRoot = cacheRoot + '\\';

		Assert.Equal(OperatingSystem.IsWindows(), PathUtility.IsPathInside(child, legacyRoot));
		Assert.True(PathUtility.IsPathInside(legacyRoot, legacyRoot));
	}
}
