namespace DevProjex.Tests.Unit;

public sealed class SmartArtifactIgnoreMatcherTests
{
	[Theory]
	[MemberData(nameof(StrongArtifactDirectories))]
	public void Default_IsIgnoredDirectory_WhenCandidateHasStrongSignature(
		string directoryName,
		string markerRelativePath,
		ArtifactMarkerKind markerKind)
	{
		using var temp = new TemporaryDirectory();
		var artifactPath = temp.CreateFolder(directoryName);
		CreateMarker(temp, $"{directoryName}/{markerRelativePath}", markerKind);

		var ignored = SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(artifactPath, directoryName);

		Assert.True(ignored);
	}

	[Theory]
	[InlineData("obj")]
	[InlineData("bin")]
	[InlineData("build")]
	[InlineData("dist")]
	[InlineData("out")]
	[InlineData("Library")]
	[InlineData("vendor")]
	[InlineData("cache")]
	[InlineData("tmp")]
	[InlineData("node_modules")]
	public void Default_DoesNotIgnoreCandidateNameWithoutArtifactSignature(string directoryName)
	{
		using var temp = new TemporaryDirectory();
		var candidatePath = temp.CreateFolder(directoryName);
		temp.CreateFile($"{directoryName}/README.md", "This folder is intentionally part of the project.");

		var ignored = SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(candidatePath, directoryName);

		Assert.False(ignored);
	}

	[Theory]
	[InlineData("cmake-build-debug")]
	[InlineData("cmake-build-release")]
	public void Default_MatchesPrefixCandidatesOnlyAfterSignatureCheck(string directoryName)
	{
		using var temp = new TemporaryDirectory();
		var candidatePath = temp.CreateFolder(directoryName);

		Assert.True(SmartArtifactIgnoreMatcher.Default.IsCandidateName(directoryName));
		Assert.False(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(candidatePath, directoryName));

		temp.CreateFile($"{directoryName}/CMakeCache.txt", "cache");

		Assert.True(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(candidatePath, directoryName));
	}

	[Fact]
	public void Empty_NeverIgnoresStrongArtifactCandidate()
	{
		using var temp = new TemporaryDirectory();
		var objPath = temp.CreateFolder("obj");
		temp.CreateFile("obj/project.assets.json", "{}");

		var ignored = SmartArtifactIgnoreMatcher.Empty.IsIgnoredDirectory(objPath, "obj");

		Assert.False(ignored);
	}

	[Fact]
	public void HasCandidateDirectory_UsesRootFactsWithoutFilesystemRescan()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFolder("src");
		temp.CreateFolder("obj");
		var facts = new ProjectRootFactsProvider(cacheLimit: 0).Get(temp.Path);

		var hasCandidate = SmartArtifactIgnoreMatcher.Default.HasCandidateDirectory(facts);

		Assert.True(hasCandidate);
	}

	[Fact]
	public void HasCandidateDirectory_IgnoresNormalRootDirectories()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFolder("src");
		temp.CreateFolder("docs");
		var facts = new ProjectRootFactsProvider(cacheLimit: 0).Get(temp.Path);

		var hasCandidate = SmartArtifactIgnoreMatcher.Default.HasCandidateDirectory(facts);

		Assert.False(hasCandidate);
	}

	public static TheoryData<string, string, ArtifactMarkerKind> StrongArtifactDirectories() => new()
	{
		{ "obj", "project.assets.json", ArtifactMarkerKind.File },
		{ "obj", "App.csproj.nuget.g.props", ArtifactMarkerKind.File },
		{ "bin", "Debug", ArtifactMarkerKind.Directory },
		{ "node_modules", ".bin", ArtifactMarkerKind.Directory },
		{ "__pycache__", "app.cpython-313.pyc", ArtifactMarkerKind.File },
		{ ".venv", "pyvenv.cfg", ArtifactMarkerKind.File },
		{ "build", "CMakeCache.txt", ArtifactMarkerKind.File },
		{ "dist", "assets", ArtifactMarkerKind.Directory },
		{ "coverage", "lcov.info", ArtifactMarkerKind.File },
		{ ".nyc_output", "coverage-final.json", ArtifactMarkerKind.File },
		{ "htmlcov", "index.html", ArtifactMarkerKind.File },
		{ ".dart_tool", "package_config.json", ArtifactMarkerKind.File },
		{ ".build", "repositories", ArtifactMarkerKind.Directory },
		{ "DerivedData", "Index.noindex", ArtifactMarkerKind.Directory },
		{ ".cxx", "CMakeCache.txt", ArtifactMarkerKind.File },
		{ "cmake-build-debug", "CMakeCache.txt", ArtifactMarkerKind.File },
		{ "vendor", "autoload.php", ArtifactMarkerKind.File },
		{ "vendor", "modules.txt", ArtifactMarkerKind.File },
		{ ".terraform", "providers", ArtifactMarkerKind.Directory },
		{ ".serverless", "cloudformation-template-update-stack.json", ArtifactMarkerKind.File },
		{ ".zig-cache", "o", ArtifactMarkerKind.Directory },
		{ ".cache", "CACHEDIR.TAG", ArtifactMarkerKind.File },
		{ "tmp", "CACHEDIR.TAG", ArtifactMarkerKind.File },
		{ "Library", "ArtifactDB", ArtifactMarkerKind.File },
		{ "Intermediate", "Build", ArtifactMarkerKind.Directory },
		{ "Saved", "Logs", ArtifactMarkerKind.Directory },
		{ "Binaries", "Win64", ArtifactMarkerKind.Directory },
		{ "xcuserdata", "UserInterfaceState.xcuserstate", ArtifactMarkerKind.File }
	};

	private static void CreateMarker(
		TemporaryDirectory temp,
		string relativePath,
		ArtifactMarkerKind markerKind)
	{
		if (markerKind == ArtifactMarkerKind.Directory)
		{
			temp.CreateFolder(relativePath);
			return;
		}

		temp.CreateFile(relativePath, "marker");
	}

	public enum ArtifactMarkerKind
	{
		File,
		Directory
	}
}
