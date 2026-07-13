namespace DevProjex.Tests.Unit;

public sealed class SmartArtifactIgnoreMutationTests
{
	[Fact]
	public void ActiveRules_ReevaluateSourceDirectoryWhenArtifactSignatureAppears()
	{
		using var temp = new TemporaryDirectory();
		var packagesPath = temp.CreateFolder("outside/packages");
		temp.CreateFile("outside/packages/README.md", "source packages\n");
		var rules = CreateRules(temp.Path, useSmartIgnore: true);

		Assert.False(rules.IsSmartIgnoredDirectory(packagesPath, "packages"));

		temp.CreateFile("outside/packages/repositories.config", "<repositories />\n");

		Assert.True(rules.IsSmartIgnoredDirectory(packagesPath, "packages"));
	}

	[Fact]
	public void ActiveRules_ReevaluateArtifactDirectoryWhenSignatureDisappears()
	{
		using var temp = new TemporaryDirectory();
		var packagesPath = temp.CreateFolder("outside/packages");
		var markerPath = temp.CreateFile(
			"outside/packages/repositories.config",
			"<repositories />\n");
		temp.CreateFile("outside/packages/README.md", "source packages\n");
		var rules = CreateRules(temp.Path, useSmartIgnore: true);

		Assert.True(rules.IsSmartIgnoredDirectory(packagesPath, "packages"));

		File.Delete(markerPath);

		Assert.False(rules.IsSmartIgnoredDirectory(packagesPath, "packages"));
	}

	[Fact]
	public void CandidateRules_ReevaluateSourceDirectoryWhenArtifactSignatureAppears()
	{
		using var temp = new TemporaryDirectory();
		var objPath = temp.CreateFolder("project/obj");
		temp.CreateFile("project/obj/Source.cs", "class Source {}\n");
		var rules = CreateRules(temp.Path, useSmartIgnore: false);

		Assert.False(rules.IsSmartIgnoredDirectoryCandidate(objPath, "obj"));

		temp.CreateFile("project/obj/project.assets.json", "{}\n");

		Assert.True(rules.IsSmartIgnoredDirectoryCandidate(objPath, "obj"));
	}

	[Fact]
	public void CandidateRules_ReevaluateArtifactDirectoryWhenSignatureDisappears()
	{
		using var temp = new TemporaryDirectory();
		var objPath = temp.CreateFolder("project/obj");
		var markerPath = temp.CreateFile("project/obj/project.assets.json", "{}\n");
		temp.CreateFile("project/obj/Source.cs", "class Source {}\n");
		var rules = CreateRules(temp.Path, useSmartIgnore: false);

		Assert.True(rules.IsSmartIgnoredDirectoryCandidate(objPath, "obj"));

		File.Delete(markerPath);

		Assert.False(rules.IsSmartIgnoredDirectoryCandidate(objPath, "obj"));
	}

	[Fact]
	public void ActiveAndCandidateEvaluation_RemainIndependentAcrossToggleState()
	{
		using var temp = new TemporaryDirectory();
		var objPath = temp.CreateFolder("project/obj");
		temp.CreateFile("project/obj/project.assets.json", "{}\n");
		var disabledRules = CreateRules(temp.Path, useSmartIgnore: false);
		var enabledRules = disabledRules with { UseSmartIgnore = true };

		Assert.False(disabledRules.IsSmartIgnoredDirectory(objPath, "obj"));
		Assert.True(disabledRules.IsSmartIgnoredDirectoryCandidate(objPath, "obj"));
		Assert.True(enabledRules.IsSmartIgnoredDirectory(objPath, "obj"));
		Assert.True(enabledRules.IsSmartIgnoredDirectoryCandidate(objPath, "obj"));
	}

	[Theory]
	[InlineData("obj", "project.assets.json")]
	[InlineData("node_modules", "package-lock.json")]
	[InlineData("__pycache__", "app.pyc")]
	[InlineData("target", ".rustc_info.json")]
	[InlineData("build", "CMakeCache.txt")]
	[InlineData("vendor", "autoload.php")]
	[InlineData(".dart_tool", "package_config.json")]
	[InlineData("Library", "ArtifactDB")]
	public void ScopeBoundArtifactProfiles_DoNotHideMatchingDirectoriesOutsideProjectScopes(
		string directoryName,
		string markerFileName)
	{
		using var temp = new TemporaryDirectory();
		var relativeDirectory = Path.Combine("outside", directoryName);
		var artifactPath = temp.CreateFolder(relativeDirectory);
		temp.CreateFile(Path.Combine(relativeDirectory, markerFileName), "artifact\n");
		var rules = CreateRules(temp.Path, useSmartIgnore: true);

		Assert.True(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(artifactPath, directoryName));
		Assert.False(rules.IsSmartIgnoredDirectory(artifactPath, directoryName));
	}

	[Fact]
	public void PortableDependencyStoreProfiles_HideConfirmedStoresOutsideProjectScopes()
	{
		using var temp = new TemporaryDirectory();
		var packagesPath = temp.CreateFolder("outside/packages");
		temp.CreateFile("outside/packages/repositories.config", "<repositories />\n");
		var npmCachePath = temp.CreateFolder("outside/_cacache");
		temp.CreateFolder("outside/_cacache/content-v2");
		var gradleModulesPath = temp.CreateFolder("outside/modules-2");
		temp.CreateFolder("outside/modules-2/files-2.1");
		var mavenRepositoryPath = temp.CreateFolder("outside/.m2/repository");
		var cargoRegistryPath = temp.CreateFolder("outside/.cargo/registry");
		var rules = CreateRules(temp.Path, useSmartIgnore: true);

		Assert.True(rules.IsSmartIgnoredDirectory(packagesPath, "packages"));
		Assert.True(rules.IsSmartIgnoredDirectory(npmCachePath, "_cacache"));
		Assert.True(rules.IsSmartIgnoredDirectory(gradleModulesPath, "modules-2"));
		Assert.True(rules.IsSmartIgnoredDirectory(mavenRepositoryPath, "repository"));
		Assert.True(rules.IsSmartIgnoredDirectory(cargoRegistryPath, "registry"));
	}

	[Fact]
	public void ConcurrentActiveAndCandidateProbes_ReturnStableResults()
	{
		using var temp = new TemporaryDirectory();
		var artifactPath = temp.CreateFolder("project/obj");
		temp.CreateFile("project/obj/project.assets.json", "{}\n");
		var sourcePath = temp.CreateFolder("project/build");
		temp.CreateFile("project/build/Source.cs", "class Source {}\n");
		var rules = CreateRules(temp.Path, useSmartIgnore: true);

		Parallel.For(0, 256, iteration =>
		{
			Assert.True(rules.IsSmartIgnoredDirectory(artifactPath, "obj"));
			Assert.True(rules.IsSmartIgnoredDirectoryCandidate(artifactPath, "obj"));
			Assert.False(rules.IsSmartIgnoredDirectory(sourcePath, "build"));
			Assert.False(rules.IsSmartIgnoredDirectoryCandidate(sourcePath, "build"));
		});
	}

	private static IgnoreRules CreateRules(string rootPath, bool useSmartIgnore) => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
		SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
	{
		UseSmartIgnore = useSmartIgnore,
		SmartIgnoreScopeRoots = [Path.Combine(rootPath, "project")],
		SmartIgnoreCandidateScopeRoots = [Path.Combine(rootPath, "project")],
		SmartArtifactIgnoreMatcher = SmartArtifactIgnoreMatcher.Default,
		SmartArtifactIgnoreCandidateMatcher = SmartArtifactIgnoreMatcher.Default
	};
}
