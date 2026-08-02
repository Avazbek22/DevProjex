namespace DevProjex.Tests.Unit;

public sealed class SmartArtifactIgnoreNegativeMatrixTests
{
	[Theory]
	[MemberData(nameof(NearMissDirectoryNames))]
	public void Default_StrongMarkerInsideNearMissNameNeverActivatesSmartIgnore(
		string directoryName,
		string markerRelativePath,
		ArtifactEntryKind markerKind)
	{
		using var temp = new TemporaryDirectory();
		var candidatePath = temp.CreateFolder(directoryName);
		CreateEntry(temp, Path.Combine(directoryName, markerRelativePath), markerKind);

		Assert.False(SmartArtifactIgnoreMatcher.Default.IsCandidateName(directoryName));
		Assert.False(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(candidatePath, directoryName));
		Assert.False(SmartArtifactIgnoreMatcher.Default.IsPortableIgnoredDirectory(candidatePath, directoryName));
	}

	[Theory]
	[MemberData(nameof(WrongEntryKindSignatures))]
	public void Default_MarkerWithWrongEntryKindDoesNotProveArtifact(
		string directoryName,
		string markerRelativePath,
		ArtifactEntryKind wrongKind)
	{
		using var temp = new TemporaryDirectory();
		var candidatePath = temp.CreateFolder(directoryName);
		CreateEntry(temp, Path.Combine(directoryName, markerRelativePath), wrongKind);

		Assert.True(SmartArtifactIgnoreMatcher.Default.IsCandidateName(directoryName));
		Assert.False(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(candidatePath, directoryName));
	}

	[Theory]
	[MemberData(nameof(MisplacedStrongSignatures))]
	public void Default_StrongMarkerBelowRequiredLevelDoesNotLeakIntoCandidate(
		string directoryName,
		string nestedMarkerRelativePath)
	{
		using var temp = new TemporaryDirectory();
		var candidatePath = temp.CreateFolder(directoryName);
		temp.CreateFile(Path.Combine(directoryName, nestedMarkerRelativePath), "source-owned marker name");

		Assert.True(SmartArtifactIgnoreMatcher.Default.IsCandidateName(directoryName));
		Assert.False(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(candidatePath, directoryName));
	}

	[Theory]
	[InlineData("nuget-backup/packages", "packages")]
	[InlineData("m2-backup/repository", "repository")]
	[InlineData("cargo-backup/registry", "registry")]
	[InlineData("stores/.nuget-backup/packages", "packages")]
	[InlineData("stores/.m2-backup/repository", "repository")]
	[InlineData("stores/.cargo-backup/registry", "registry")]
	public void Default_NearMissPortableStoreParentDoesNotMatchPathSuffix(
		string relativePath,
		string directoryName)
	{
		using var temp = new TemporaryDirectory();
		var directoryPath = temp.CreateFolder(relativePath);

		Assert.True(SmartArtifactIgnoreMatcher.Default.IsCandidateName(directoryName));
		Assert.False(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(directoryPath, directoryName));
		Assert.False(SmartArtifactIgnoreMatcher.Default.IsPortableIgnoredDirectory(directoryPath, directoryName));
	}

	[Theory]
	[InlineData("Project.csproj.user.backup")]
	[InlineData("Project.csproj.user.shared")]
	[InlineData("Project.user.csproj")]
	[InlineData("Solution.sln.DotSettings.user.template")]
	[InlineData("Solution.sln.DotSettings.users")]
	[InlineData("csproj.user")]
	public void Default_UserStateSuffixNearMissRemainsVisible(string fileName)
	{
		Assert.False(SmartArtifactIgnoreMatcher.Default.IsIgnoredFile(fileName));
	}

	[Fact]
	public void Default_OneCompleteLegacyPackagePlusManySourceChildrenRemainsVisible()
	{
		using var temp = new TemporaryDirectory();
		var packagesPath = temp.CreateFolder("packages");
		temp.CreateFile("packages/Alpha.1.0.0/Alpha.1.0.0.nupkg", "package");
		temp.CreateFolder("packages/Alpha.1.0.0/lib");
		for (var index = 0; index < 24; index++)
			temp.CreateFile($"packages/source-{index:D2}/package.json", "{}");

		Assert.False(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(packagesPath, "packages"));
		Assert.False(SmartArtifactIgnoreMatcher.Default.IsPortableIgnoredDirectory(packagesPath, "packages"));
	}

	[Fact]
	public void HasConfirmedArtifactDirectory_NearMissAndIncompleteCandidatesDoNotBorrowSiblingEvidence()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("obj-backup/project.assets.json", "{}");
		temp.CreateFile("packages/Alpha/Alpha.nupkg", "package");
		temp.CreateFolder("packages/Alpha/lib");
		temp.CreateFile("repository/source/module.pom", "<project />");
		var facts = new ProjectRootFactsProvider(cacheLimit: 0).Get(temp.Path);

		Assert.True(SmartArtifactIgnoreMatcher.Default.HasCandidateDirectory(facts));
		Assert.False(SmartArtifactIgnoreMatcher.Default.HasConfirmedArtifactDirectory(facts));
	}

	public static TheoryData<string, string, ArtifactEntryKind> NearMissDirectoryNames() => new()
	{
		{ "obj-backup", "project.assets.json", ArtifactEntryKind.File },
		{ "node_modules_backup", ".bin", ArtifactEntryKind.Directory },
		{ "build-cache", "CMakeCache.txt", ArtifactEntryKind.File },
		{ "vendorized", "autoload.php", ArtifactEntryKind.File },
		{ "targeting", "debug", ArtifactEntryKind.Directory },
		{ "LibrarySource", "ArtifactDB", ArtifactEntryKind.File },
		{ "packages-source", "repositories.config", ArtifactEntryKind.File },
		{ "cmake-build", "CMakeCache.txt", ArtifactEntryKind.File },
		{ "xcmake-build-debug", "CMakeCache.txt", ArtifactEntryKind.File }
	};

	public static TheoryData<string, string, ArtifactEntryKind> WrongEntryKindSignatures() => new()
	{
		{ "obj", "project.assets.json", ArtifactEntryKind.Directory },
		{ "build", "CMakeFiles", ArtifactEntryKind.File },
		{ "Library", "ArtifactDB", ArtifactEntryKind.Directory },
		{ "node_modules", ".bin", ArtifactEntryKind.File },
		{ "cache", "CACHEDIR.TAG", ArtifactEntryKind.Directory },
		{ "bower_components", "jquery/bower.json", ArtifactEntryKind.Directory },
		{ "xcuserdata", "Session.xcuserstate", ArtifactEntryKind.Directory }
	};

	public static TheoryData<string, string> MisplacedStrongSignatures() => new()
	{
		{ "obj", "nested/project.assets.json" },
		{ "build", "docs/CMakeCache.txt" },
		{ "vendor", "src/autoload.php" },
		{ "cache", "docs/CACHEDIR.TAG" },
		{ "bin", "nested/App.dll" },
		{ "xcuserdata", "nested/Session.xcuserstate" },
		{ ".serverless", "archives/function.zip" }
	};

	private static void CreateEntry(
		TemporaryDirectory temp,
		string relativePath,
		ArtifactEntryKind kind)
	{
		if (kind == ArtifactEntryKind.Directory)
		{
			temp.CreateFolder(relativePath);
			return;
		}

		temp.CreateFile(relativePath, "marker");
	}

	public enum ArtifactEntryKind
	{
		File,
		Directory
	}
}
