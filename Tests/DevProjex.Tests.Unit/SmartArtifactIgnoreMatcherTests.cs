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
	[InlineData("packages")]
	[InlineData("repository")]
	[InlineData("registry")]
	[InlineData("_cacache")]
	[InlineData("modules-2")]
	public void Default_DoesNotIgnoreCandidateNameWithoutArtifactSignature(string directoryName)
	{
		using var temp = new TemporaryDirectory();
		var candidatePath = temp.CreateFolder(directoryName);
		temp.CreateFile($"{directoryName}/README.md", "This folder is intentionally part of the project.");

		var ignored = SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(candidatePath, directoryName);

		Assert.False(ignored);
	}

	[Theory]
	[MemberData(nameof(SourceLikeCandidateDirectoriesAcrossSupportedStacks))]
	public void Default_SourceLikeCandidateAcrossSupportedStacks_RemainsVisible(string directoryName)
	{
		using var temp = new TemporaryDirectory();
		var candidatePath = temp.CreateFolder(directoryName);
		temp.CreateFile($"{directoryName}/Source.txt", "user-owned source\n");

		Assert.True(SmartArtifactIgnoreMatcher.Default.IsCandidateName(directoryName));
		Assert.False(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(candidatePath, directoryName));
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

	[Theory]
	[InlineData("OBJ", "project.assets.json")]
	[InlineData("Bin", "Debug/App.dll")]
	[InlineData("Node_Modules", ".bin/vite")]
	[InlineData("CmAkE-bUiLd-Debug", "CMakeCache.txt")]
	public void Default_CandidateDirectoryNameMatching_IsCaseInsensitive(
		string directoryName,
		string markerRelativePath)
	{
		using var temp = new TemporaryDirectory();
		var candidatePath = temp.CreateFolder(directoryName);
		temp.CreateFile($"{directoryName}/{markerRelativePath}", "marker");

		Assert.True(SmartArtifactIgnoreMatcher.Default.IsCandidateName(directoryName));
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
	public void CustomMatcher_DuplicateExactNamesPreserveEverySignatureRule()
	{
		using var temp = new TemporaryDirectory();
		var outputPath = temp.CreateFolder("output");
		temp.CreateFile("output/second.marker", "marker");
		var matcher = new SmartArtifactIgnoreMatcher(
		[
			SmartArtifactIgnoreMatcher.SmartArtifactDirectoryRule.Exact("output", files: ["first.marker"]),
			SmartArtifactIgnoreMatcher.SmartArtifactDirectoryRule.Exact("output", files: ["second.marker"])
		]);

		Assert.True(matcher.IsIgnoredDirectory(outputPath, "output"));
	}

	[Fact]
	public void CustomMatcher_ExactAndPrefixRulesCanBothContributeToSameCandidate()
	{
		using var temp = new TemporaryDirectory();
		var outputPath = temp.CreateFolder("output-cache");
		temp.CreateFile("output-cache/prefix.marker", "marker");
		var matcher = new SmartArtifactIgnoreMatcher(
		[
			SmartArtifactIgnoreMatcher.SmartArtifactDirectoryRule.Exact("output-cache", files: ["exact.marker"]),
			SmartArtifactIgnoreMatcher.SmartArtifactDirectoryRule.Prefix("output-", files: ["prefix.marker"])
		]);

		Assert.True(matcher.IsCandidateName("output-cache"));
		Assert.True(matcher.IsIgnoredDirectory(outputPath, "output-cache"));
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
	public void HasCandidateDirectory_IgnoresReparsePointCandidatesFromRootFacts()
	{
		using var temp = new TemporaryDirectory();
		var facts = new ProjectRootFacts(
			temp.Path,
			exists: true,
			isAccessible: true,
			files: [],
			directories:
			[
				new ProjectRootDirectoryFact("src", Path.Combine(temp.Path, "src"), IsReparsePoint: false),
				new ProjectRootDirectoryFact("obj", Path.Combine(temp.Path, "obj"), IsReparsePoint: true)
			],
			gitIgnoreSignature: null);

		var hasCandidate = SmartArtifactIgnoreMatcher.Default.HasCandidateDirectory(facts);

		Assert.False(hasCandidate);
	}

	[Fact]
	public void Default_LegacyNuGetPackages_RequiresRepeatedPackageLayoutEvidence()
	{
		using var temp = new TemporaryDirectory();
		var packagesPath = temp.CreateFolder("packages");
		CreateLegacyNuGetPackage(temp, "Newtonsoft.Json.13.0.3", "lib");

		Assert.False(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(packagesPath, "packages"));

		CreateLegacyNuGetPackage(temp, "xunit.2.9.3", "tools");

		Assert.True(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(packagesPath, "packages"));
	}

	[Fact]
	public void Default_LegacyNuGetPackages_RejectsIncompleteAndMismatchedPackageLayouts()
	{
		using var temp = new TemporaryDirectory();
		var packagesPath = temp.CreateFolder("packages");
		temp.CreateFile("packages/OnlyPackage/OnlyPackage.nupkg", "package");
		temp.CreateFolder("packages/OnlyPackage/lib");
		temp.CreateFile("packages/MissingLayout/MissingLayout.nupkg", "package");
		temp.CreateFolder("packages/MissingArtifact/ref");
		temp.CreateFile("packages/WrongName/Other.nupkg", "package");
		temp.CreateFolder("packages/WrongName/tools");

		Assert.False(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(packagesPath, "packages"));
	}

	[Fact]
	public void Default_LegacyNuGetPackages_DoesNotHideSourceMonorepoContainer()
	{
		using var temp = new TemporaryDirectory();
		var packagesPath = temp.CreateFolder("packages");
		temp.CreateFile("packages/api/package.json", "{}");
		temp.CreateFile("packages/domain/Domain.csproj", "<Project />");

		Assert.True(SmartArtifactIgnoreMatcher.Default.IsCandidateName("packages"));
		Assert.False(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(packagesPath, "packages"));
	}

	[Fact]
	public void Default_RepeatedLayoutFingerprint_DoesNotFollowReparsePointChildren()
	{
		using var temp = new TemporaryDirectory();
		var packagesPath = temp.CreateFolder("packages");
		temp.CreateFile("targets/alpha/Alpha.nupkg", "package");
		temp.CreateFolder("targets/alpha/lib");
		temp.CreateFile("targets/beta/Beta.nupkg", "package");
		temp.CreateFolder("targets/beta/ref");
		if (!TryCreateDirectorySymlink(
				Path.Combine(packagesPath, "Alpha"),
				Path.Combine(temp.Path, "targets", "alpha")) ||
		    !TryCreateDirectorySymlink(
				Path.Combine(packagesPath, "Beta"),
				Path.Combine(temp.Path, "targets", "beta")))
		{
			return;
		}

		Assert.False(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(packagesPath, "packages"));
	}

	[Theory]
	[InlineData(".nuget/packages", "packages")]
	[InlineData(".m2/repository", "repository")]
	[InlineData(".cargo/registry", "registry")]
	public void Default_OfficialDependencyStorePath_IsStrongEvidence(
		string relativePath,
		string directoryName)
	{
		using var temp = new TemporaryDirectory();
		var dependencyStorePath = temp.CreateFolder(relativePath);

		Assert.True(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(
			dependencyStorePath,
			directoryName));
	}

	[Theory]
	[InlineData("Solution.sln.DotSettings.user", true)]
	[InlineData("Project.csproj.user", true)]
	[InlineData("Project.FSPROJ.USER", true)]
	[InlineData("Project.vbproj.user", true)]
	[InlineData("Solution.sln.DotSettings", false)]
	[InlineData("notes.user", false)]
	[InlineData("user", false)]
	public void Default_UserSpecificFileSuffixes_AreConservative(
		string fileName,
		bool expectedIgnored)
	{
		Assert.Equal(expectedIgnored, SmartArtifactIgnoreMatcher.Default.IsIgnoredFile(fileName));
	}

	[Fact]
	public void CustomMatcher_ExtensionlessSuffixRemainsSupportedByTerminalFastPath()
	{
		var matcher = new SmartArtifactIgnoreMatcher([], ["generated-state"]);

		Assert.True(matcher.HasRules);
		Assert.True(matcher.IsIgnoredFile("App.generated-state"));
		Assert.False(matcher.IsIgnoredFile("App.generated"));
	}

	[Fact]
	public void HasConfirmedArtifactDirectory_RequiresSignatureNotOnlyCandidateName()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("packages/api/package.json", "{}");
		var factsProvider = new ProjectRootFactsProvider(cacheLimit: 0);

		var sourceFacts = factsProvider.Get(temp.Path);

		Assert.True(SmartArtifactIgnoreMatcher.Default.HasCandidateDirectory(sourceFacts));
		Assert.False(SmartArtifactIgnoreMatcher.Default.HasConfirmedArtifactDirectory(sourceFacts));

		CreateLegacyNuGetPackage(temp, "Alpha.1.0.0", "lib");
		CreateLegacyNuGetPackage(temp, "Beta.2.0.0", "ref");
		var artifactFacts = factsProvider.Get(temp.Path, forceRefresh: true);

		Assert.True(SmartArtifactIgnoreMatcher.Default.HasConfirmedArtifactDirectory(artifactFacts));
	}

	[Fact]
	public void PortableDirectoryEvaluation_DistinguishesDependencyStoresFromScopeBoundBuildArtifacts()
	{
		using var temp = new TemporaryDirectory();
		var objPath = temp.CreateFolder("obj");
		temp.CreateFile("obj/project.assets.json", "{}");
		var packagesPath = temp.CreateFolder("packages");
		CreateLegacyNuGetPackage(temp, "Alpha.1.0.0", "lib");
		CreateLegacyNuGetPackage(temp, "Beta.2.0.0", "ref");

		Assert.True(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(objPath, "obj"));
		Assert.False(SmartArtifactIgnoreMatcher.Default.IsPortableIgnoredDirectory(objPath, "obj"));
		Assert.True(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(packagesPath, "packages"));
		Assert.True(SmartArtifactIgnoreMatcher.Default.IsPortableIgnoredDirectory(packagesPath, "packages"));
	}

	[Fact]
	public void RepeatedChildArtifactSignature_RejectsInvalidBounds()
	{
		Assert.Throws<ArgumentException>(() => new SmartArtifactIgnoreMatcher.RepeatedChildArtifactSignature(
			string.Empty,
			["lib"],
			minimumMatches: 2,
			maxEntries: 8));
		Assert.Throws<ArgumentOutOfRangeException>(() => new SmartArtifactIgnoreMatcher.RepeatedChildArtifactSignature(
			".pkg",
			["lib"],
			minimumMatches: 0,
			maxEntries: 8));
		Assert.Throws<ArgumentOutOfRangeException>(() => new SmartArtifactIgnoreMatcher.RepeatedChildArtifactSignature(
			".pkg",
			["lib"],
			minimumMatches: 3,
			maxEntries: 2));
	}

	[Theory]
	[InlineData("")]
	[InlineData(" ")]
	[InlineData("source")]
	[InlineData("build-cache")]
	public void Default_IsCandidateName_RejectsBlankAndNonCandidateNames(string name)
	{
		Assert.False(SmartArtifactIgnoreMatcher.Default.IsCandidateName(name));
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

	[Fact]
	public void Default_MalformedCandidatePathFailsClosedWithoutThrowing()
	{
		var malformedPath = $"invalid{Path.DirectorySeparatorChar}\0obj";

		var exception = Record.Exception(() =>
			SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(malformedPath, "obj"));

		Assert.Null(exception);
		Assert.False(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(malformedPath, "obj"));
	}

	[Fact]
	public void Default_DeletedCandidateDirectoryFailsClosedWithoutThrowing()
	{
		using var temp = new TemporaryDirectory();
		var candidatePath = temp.CreateFolder("obj");
		Directory.Delete(candidatePath);

		var exception = Record.Exception(() =>
			SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(candidatePath, "obj"));

		Assert.Null(exception);
		Assert.False(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(candidatePath, "obj"));
	}

	[Theory]
	[MemberData(nameof(SmartArtifactFileEvidenceNoFollowCases))]
	public void SmartArtifactEvidenceNoFollow_FileEvidenceMatrix(
		SmartArtifactFileEvidenceKind evidenceKind,
		SmartArtifactLinkState entryState)
	{
		using var temp = new TemporaryDirectory();
		var candidatePath = temp.CreateFolder("output");
		var matcher = CreateFileEvidenceMatcher(evidenceKind);

		ArrangeFileEvidence(temp, evidenceKind, entryState);

		Assert.Equal(
			entryState == SmartArtifactLinkState.Regular,
			matcher.IsIgnoredDirectory(candidatePath, "output"));
	}

	[Theory]
	[MemberData(nameof(SmartArtifactDirectoryEvidenceNoFollowCases))]
	public void SmartArtifactEvidenceNoFollow_DirectoryEvidenceMatrix(
		SmartArtifactDirectoryEvidenceKind evidenceKind,
		SmartArtifactLinkState entryState)
	{
		using var temp = new TemporaryDirectory();
		var matcher = CreateDirectoryEvidenceMatcher(evidenceKind);
		var candidatePath = ArrangeDirectoryEvidence(temp, evidenceKind, entryState);

		Assert.Equal(
			entryState == SmartArtifactLinkState.Regular,
			matcher.IsIgnoredDirectory(candidatePath, "output"));
	}

	public static TheoryData<string, string, ArtifactMarkerKind> StrongArtifactDirectories() => new()
	{
		{ "obj", "project.assets.json", ArtifactMarkerKind.File },
		{ "obj", "App.csproj.nuget.g.props", ArtifactMarkerKind.File },
		{ "bin", "Debug", ArtifactMarkerKind.Directory },
		{ "node_modules", ".bin", ArtifactMarkerKind.Directory },
		{ "bower_components", "jquery/bower.json", ArtifactMarkerKind.File },
		{ "jspm_packages", "github", ArtifactMarkerKind.Directory },
		{ "packages", "repositories.config", ArtifactMarkerKind.File },
		{ "_cacache", "content-v2", ArtifactMarkerKind.Directory },
		{ "modules-2", "files-2.1", ArtifactMarkerKind.Directory },
		{ "__pycache__", "app.cpython-313.pyc", ArtifactMarkerKind.File },
		{ ".venv", "pyvenv.cfg", ArtifactMarkerKind.File },
		{ ".mypy_cache", "3.13", ArtifactMarkerKind.Directory },
		{ ".pytest_cache", "CACHEDIR.TAG", ArtifactMarkerKind.File },
		{ ".ruff_cache", "CACHEDIR.TAG", ArtifactMarkerKind.File },
		{ ".tox", "CACHEDIR.TAG", ArtifactMarkerKind.File },
		{ ".nox", "tests", ArtifactMarkerKind.Directory },
		{ ".hypothesis", "examples", ArtifactMarkerKind.Directory },
		{ ".ipynb_checkpoints", "draft.ipynb", ArtifactMarkerKind.File },
		{ ".pyre", "cache", ArtifactMarkerKind.Directory },
		{ ".gradle", "caches", ArtifactMarkerKind.Directory },
		{ "target", "deps", ArtifactMarkerKind.Directory },
		{ "build", "CMakeCache.txt", ArtifactMarkerKind.File },
		{ "dist", "assets", ArtifactMarkerKind.Directory },
		{ "out", "compile_commands.json", ArtifactMarkerKind.File },
		{ "coverage", "lcov.info", ArtifactMarkerKind.File },
		{ ".next", "BUILD_ID", ArtifactMarkerKind.File },
		{ ".nuxt", "nuxt.json", ArtifactMarkerKind.File },
		{ ".turbo", "runs", ArtifactMarkerKind.Directory },
		{ ".vite", "deps", ArtifactMarkerKind.Directory },
		{ ".parcel-cache", "data", ArtifactMarkerKind.Directory },
		{ ".svelte-kit", "types", ArtifactMarkerKind.Directory },
		{ ".angular", "cache", ArtifactMarkerKind.Directory },
		{ ".astro", "types.d.ts", ArtifactMarkerKind.File },
		{ ".output", "nitro.json", ArtifactMarkerKind.File },
		{ "storybook-static", "assets", ArtifactMarkerKind.Directory },
		{ ".nyc_output", "coverage-final.json", ArtifactMarkerKind.File },
		{ "htmlcov", "index.html", ArtifactMarkerKind.File },
		{ ".dart_tool", "package_config.json", ArtifactMarkerKind.File },
		{ ".build", "repositories", ArtifactMarkerKind.Directory },
		{ ".build", "workspace-state.json", ArtifactMarkerKind.File },
		{ "DerivedData", "Index.noindex", ArtifactMarkerKind.Directory },
		{ "CMakeFiles", "CMakeOutput.log", ArtifactMarkerKind.File },
		{ ".cxx", "CMakeCache.txt", ArtifactMarkerKind.File },
		{ "cmake-build-debug", "CMakeCache.txt", ArtifactMarkerKind.File },
		{ "vendor", "autoload.php", ArtifactMarkerKind.File },
		{ "vendor", "modules.txt", ArtifactMarkerKind.File },
		{ ".bundle", "config", ArtifactMarkerKind.File },
		{ ".terraform", "providers", ArtifactMarkerKind.Directory },
		{ ".serverless", "cloudformation-template-update-stack.json", ArtifactMarkerKind.File },
		{ ".zig-cache", "o", ArtifactMarkerKind.Directory },
		{ "zig-cache", "o", ArtifactMarkerKind.Directory },
		{ ".cache", "CACHEDIR.TAG", ArtifactMarkerKind.File },
		{ "cache", "CACHEDIR.TAG", ArtifactMarkerKind.File },
		{ "tmp", "CACHEDIR.TAG", ArtifactMarkerKind.File },
		{ "temp", "CACHEDIR.TAG", ArtifactMarkerKind.File },
		{ "pkg", "mod", ArtifactMarkerKind.Directory },
		{ "_build", ".mix", ArtifactMarkerKind.File },
		{ ".stack-work", "dist", ArtifactMarkerKind.Directory },
		{ "Library", "ArtifactDB", ArtifactMarkerKind.File },
		{ "Intermediate", "Build", ArtifactMarkerKind.Directory },
		{ "Saved", "Logs", ArtifactMarkerKind.Directory },
		{ "Binaries", "Win64", ArtifactMarkerKind.Directory },
		{ "xcuserdata", "UserInterfaceState.xcuserstate", ArtifactMarkerKind.File }
	};

	public static TheoryData<string> SourceLikeCandidateDirectoriesAcrossSupportedStacks() => new()
	{
		"bower_components",
		"jspm_packages",
		"__pycache__",
		".venv",
		"venv",
		"env",
		".pytest_cache",
		".mypy_cache",
		".ruff_cache",
		".tox",
		".nox",
		".hypothesis",
		".ipynb_checkpoints",
		".pyre",
		".gradle",
		"target",
		"coverage",
		".next",
		".nuxt",
		".turbo",
		".vite",
		".parcel-cache",
		".svelte-kit",
		".angular",
		".astro",
		".output",
		"storybook-static",
		".nyc_output",
		"htmlcov",
		".dart_tool",
		".build",
		"DerivedData",
		"CMakeFiles",
		".cxx",
		"cmake-build-source",
		".bundle",
		"_build",
		".stack-work",
		".terraform",
		".serverless",
		".zig-cache",
		"zig-cache",
		"temp",
		"Intermediate",
		"Saved",
		"Binaries",
		"xcuserdata"
	};

	public static TheoryData<SmartArtifactFileEvidenceKind, SmartArtifactLinkState>
		SmartArtifactFileEvidenceNoFollowCases()
	{
		var cases = new TheoryData<SmartArtifactFileEvidenceKind, SmartArtifactLinkState>();
		foreach (var evidenceKind in Enum.GetValues<SmartArtifactFileEvidenceKind>())
		{
			cases.Add(evidenceKind, SmartArtifactLinkState.Regular);
			cases.Add(evidenceKind, SmartArtifactLinkState.SymbolicLink);
			cases.Add(evidenceKind, SmartArtifactLinkState.DanglingSymbolicLink);
		}

		return cases;
	}

	public static TheoryData<SmartArtifactDirectoryEvidenceKind, SmartArtifactLinkState>
		SmartArtifactDirectoryEvidenceNoFollowCases()
	{
		var cases = new TheoryData<SmartArtifactDirectoryEvidenceKind, SmartArtifactLinkState>();
		foreach (var evidenceKind in Enum.GetValues<SmartArtifactDirectoryEvidenceKind>())
		{
			cases.Add(evidenceKind, SmartArtifactLinkState.Regular);
			cases.Add(evidenceKind, SmartArtifactLinkState.SymbolicLink);
			cases.Add(evidenceKind, SmartArtifactLinkState.DanglingSymbolicLink);
		}

		return cases;
	}

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

	private static void CreateLegacyNuGetPackage(
		TemporaryDirectory temp,
		string packageDirectoryName,
		string layoutDirectoryName)
	{
		temp.CreateFile(
			$"packages/{packageDirectoryName}/{packageDirectoryName}.nupkg",
			"package");
		temp.CreateFolder($"packages/{packageDirectoryName}/{layoutDirectoryName}");
	}

	private static SmartArtifactIgnoreMatcher CreateFileEvidenceMatcher(
		SmartArtifactFileEvidenceKind evidenceKind)
	{
		var rule = evidenceKind switch
		{
			SmartArtifactFileEvidenceKind.DirectFile =>
				SmartArtifactIgnoreMatcher.SmartArtifactDirectoryRule.Exact(
					"output",
					files: ["marker.json"]),
			SmartArtifactFileEvidenceKind.FileSuffix =>
				SmartArtifactIgnoreMatcher.SmartArtifactDirectoryRule.Exact(
					"output",
					fileSuffixes: [".generated.json"]),
			SmartArtifactFileEvidenceKind.FileExtension =>
				SmartArtifactIgnoreMatcher.SmartArtifactDirectoryRule.Exact(
					"output",
					fileExtensions: [".artifact"]),
			SmartArtifactFileEvidenceKind.ChildFile =>
				SmartArtifactIgnoreMatcher.SmartArtifactDirectoryRule.Exact(
					"output",
					childFiles: ["package.json"]),
			SmartArtifactFileEvidenceKind.RepeatedSelfNamedFile =>
				CreateRepeatedEvidenceRule(),
			SmartArtifactFileEvidenceKind.NativeExecutable =>
				SmartArtifactIgnoreMatcher.SmartArtifactDirectoryRule.Exact(
					"output",
					hasNativeExecutable: true),
			_ => throw new ArgumentOutOfRangeException(nameof(evidenceKind), evidenceKind, null)
		};

		return new SmartArtifactIgnoreMatcher([rule]);
	}

	private static SmartArtifactIgnoreMatcher CreateDirectoryEvidenceMatcher(
		SmartArtifactDirectoryEvidenceKind evidenceKind)
	{
		var rule = evidenceKind switch
		{
			SmartArtifactDirectoryEvidenceKind.DirectDirectory =>
				SmartArtifactIgnoreMatcher.SmartArtifactDirectoryRule.Exact(
					"output",
					directories: ["marker-directory"]),
			SmartArtifactDirectoryEvidenceKind.ChildDirectory =>
				SmartArtifactIgnoreMatcher.SmartArtifactDirectoryRule.Exact(
					"output",
					childFiles: ["package.json"]),
			SmartArtifactDirectoryEvidenceKind.RepeatedLayoutDirectory =>
				CreateRepeatedEvidenceRule(),
			SmartArtifactDirectoryEvidenceKind.CandidatePathSuffix =>
				SmartArtifactIgnoreMatcher.SmartArtifactDirectoryRule.Exact(
					"output",
					pathSuffixSegments: ["store", "output"]),
			_ => throw new ArgumentOutOfRangeException(nameof(evidenceKind), evidenceKind, null)
		};

		return new SmartArtifactIgnoreMatcher([rule]);
	}

	private static SmartArtifactIgnoreMatcher.SmartArtifactDirectoryRule CreateRepeatedEvidenceRule() =>
		SmartArtifactIgnoreMatcher.SmartArtifactDirectoryRule.Exact(
			"output",
			repeatedChildSignature: new SmartArtifactIgnoreMatcher.RepeatedChildArtifactSignature(
				".pkg",
				["layout"],
				minimumMatches: 2,
				maxEntries: 8));

	private static void ArrangeFileEvidence(
		TemporaryDirectory temp,
		SmartArtifactFileEvidenceKind evidenceKind,
		SmartArtifactLinkState entryState)
	{
		var evidencePaths = evidenceKind switch
		{
			SmartArtifactFileEvidenceKind.DirectFile => new[] { "output/marker.json" },
			SmartArtifactFileEvidenceKind.FileSuffix => new[] { "output/item.generated.json" },
			SmartArtifactFileEvidenceKind.FileExtension => new[] { "output/item.artifact" },
			SmartArtifactFileEvidenceKind.ChildFile => new[] { "output/package/package.json" },
			SmartArtifactFileEvidenceKind.RepeatedSelfNamedFile =>
				new[] { "output/alpha/alpha.pkg", "output/beta/beta.pkg" },
			SmartArtifactFileEvidenceKind.NativeExecutable => new[] { "output/tool" },
			_ => throw new ArgumentOutOfRangeException(nameof(evidenceKind), evidenceKind, null)
		};

		if (evidenceKind == SmartArtifactFileEvidenceKind.RepeatedSelfNamedFile)
		{
			temp.CreateFolder("output/alpha/layout");
			temp.CreateFolder("output/beta/layout");
		}

		foreach (var relativeEvidencePath in evidencePaths)
		{
			var evidencePath = Path.Combine(temp.Path, relativeEvidencePath);
			var targetPath = Path.Combine(
				temp.Path,
				"external-file-evidence",
				relativeEvidencePath.Replace('/', '_'));
			CreateFileEntryOrSkip(
				evidencePath,
				targetPath,
				entryState,
				evidenceKind == SmartArtifactFileEvidenceKind.NativeExecutable
					? [0x7f, (byte)'E', (byte)'L', (byte)'F']
					: [(byte)'x']);
		}
	}

	private static string ArrangeDirectoryEvidence(
		TemporaryDirectory temp,
		SmartArtifactDirectoryEvidenceKind evidenceKind,
		SmartArtifactLinkState entryState)
	{
		var candidatePath = Path.Combine(
			temp.Path,
			evidenceKind == SmartArtifactDirectoryEvidenceKind.CandidatePathSuffix
				? Path.Combine("store", "output")
				: "output");

		if (evidenceKind == SmartArtifactDirectoryEvidenceKind.CandidatePathSuffix)
		{
			CreateDirectoryEntryOrSkip(
				candidatePath,
				Path.Combine(temp.Path, "external-directory-evidence", "candidate"),
				entryState);
			return candidatePath;
		}

		Directory.CreateDirectory(candidatePath);
		switch (evidenceKind)
		{
			case SmartArtifactDirectoryEvidenceKind.DirectDirectory:
				CreateDirectoryEntryOrSkip(
					Path.Combine(candidatePath, "marker-directory"),
					Path.Combine(temp.Path, "external-directory-evidence", "direct"),
					entryState);
				break;
			case SmartArtifactDirectoryEvidenceKind.ChildDirectory:
				var childTarget = Path.Combine(temp.Path, "external-directory-evidence", "child");
				if (entryState == SmartArtifactLinkState.SymbolicLink)
				{
					Directory.CreateDirectory(childTarget);
					File.WriteAllText(Path.Combine(childTarget, "package.json"), "{}");
				}
				CreateDirectoryEntryOrSkip(
					Path.Combine(candidatePath, "package"),
					childTarget,
					entryState,
					createTarget: false);
				if (entryState == SmartArtifactLinkState.Regular)
					File.WriteAllText(Path.Combine(candidatePath, "package", "package.json"), "{}");
				break;
			case SmartArtifactDirectoryEvidenceKind.RepeatedLayoutDirectory:
				foreach (var childName in new[] { "alpha", "beta" })
				{
					var childPath = Path.Combine(candidatePath, childName);
					Directory.CreateDirectory(childPath);
					File.WriteAllText(Path.Combine(childPath, childName + ".pkg"), "package");
					CreateDirectoryEntryOrSkip(
						Path.Combine(childPath, "layout"),
						Path.Combine(temp.Path, "external-directory-evidence", "layout-" + childName),
						entryState);
				}
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(evidenceKind), evidenceKind, null);
		}

		return candidatePath;
	}

	private static void CreateFileEntryOrSkip(
		string entryPath,
		string targetPath,
		SmartArtifactLinkState entryState,
		byte[] content)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
		if (entryState == SmartArtifactLinkState.Regular)
		{
			File.WriteAllBytes(entryPath, content);
			return;
		}

		Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
		if (entryState == SmartArtifactLinkState.SymbolicLink)
			File.WriteAllBytes(targetPath, content);

		try
		{
			File.CreateSymbolicLink(entryPath, targetPath);
			if (string.IsNullOrEmpty(new FileInfo(entryPath).LinkTarget))
				Assert.Skip("The filesystem did not preserve the file symbolic link.");
		}
		catch (Exception exception) when (exception is
		       IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
		{
			Assert.Skip($"File symbolic links are unavailable: {exception.GetType().Name}.");
		}
	}

	private static void CreateDirectoryEntryOrSkip(
		string entryPath,
		string targetPath,
		SmartArtifactLinkState entryState,
		bool createTarget = true)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
		if (entryState == SmartArtifactLinkState.Regular)
		{
			Directory.CreateDirectory(entryPath);
			return;
		}

		if (createTarget && entryState != SmartArtifactLinkState.DanglingSymbolicLink)
			Directory.CreateDirectory(targetPath);

		try
		{
			Directory.CreateSymbolicLink(entryPath, targetPath);
			if (string.IsNullOrEmpty(new DirectoryInfo(entryPath).LinkTarget))
				Assert.Skip("The filesystem did not preserve the directory symbolic link.");
		}
		catch (Exception exception) when (exception is
		       IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
		{
			Assert.Skip($"Directory symbolic links are unavailable: {exception.GetType().Name}.");
		}
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

	public enum ArtifactMarkerKind
	{
		File,
		Directory
	}

	public enum SmartArtifactFileEvidenceKind
	{
		DirectFile,
		FileSuffix,
		FileExtension,
		ChildFile,
		RepeatedSelfNamedFile,
		NativeExecutable
	}

	public enum SmartArtifactDirectoryEvidenceKind
	{
		DirectDirectory,
		ChildDirectory,
		RepeatedLayoutDirectory,
		CandidatePathSuffix
	}

	public enum SmartArtifactLinkState
	{
		Regular,
		SymbolicLink,
		DanglingSymbolicLink
	}
}
