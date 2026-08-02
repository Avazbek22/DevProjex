namespace DevProjex.Tests.Unit;

public sealed class IgnoreRulesSmartArtifactScopeTests
{
	[Fact]
	public void IsSmartIgnoredDirectory_ScopeBoundArtifactMatcherRespectsActiveSmartScopeRoots()
	{
		using var temp = new TemporaryDirectory();
		var scopedObj = temp.CreateFolder("scoped/obj");
		var outsideObj = temp.CreateFolder("outside/obj");
		temp.CreateFile("scoped/obj/project.assets.json", "{}");
		temp.CreateFile("outside/obj/project.assets.json", "{}");
		var rules = CreateArtifactRules(useSmartIgnore: true) with
		{
			SmartIgnoreScopeRoots = [Path.Combine(temp.Path, "scoped")]
		};

		Assert.True(rules.IsSmartIgnoredDirectory(scopedObj, "obj"));
		Assert.False(rules.IsSmartIgnoredDirectory(outsideObj, "obj"));
	}

	[Fact]
	public void IsSmartIgnoredDirectory_PortableDependencyStoreAppliesOutsideStackScopes()
	{
		using var temp = new TemporaryDirectory();
		var packagesPath = temp.CreateFolder("outside/packages");
		temp.CreateFile("outside/packages/Alpha/Alpha.nupkg", "package");
		temp.CreateFolder("outside/packages/Alpha/lib");
		temp.CreateFile("outside/packages/Beta/Beta.nupkg", "package");
		temp.CreateFolder("outside/packages/Beta/ref");
		var rules = CreateArtifactRules(useSmartIgnore: true) with
		{
			SmartIgnoreScopeRoots = [Path.Combine(temp.Path, "scoped")]
		};

		Assert.True(rules.IsSmartIgnoredDirectory(packagesPath, "packages"));
	}

	[Fact]
	public void IsSmartIgnoredDirectory_StackDescriptorNamesRemainLimitedToActiveSmartScopeRoots()
	{
		using var temp = new TemporaryDirectory();
		var scopedGenerated = temp.CreateFolder("scoped/generated");
		var outsideGenerated = temp.CreateFolder("outside/generated");
		var folderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "generated" };
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: folderNames,
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			UseSmartIgnore = true,
			SmartIgnoreScopeRoots = [Path.Combine(temp.Path, "scoped")],
			ScopedSmartIgnoreMatchers =
			[
				new ScopedSmartIgnoreMatcher(
					Path.Combine(temp.Path, "scoped"),
					folderNames,
					new HashSet<string>(StringComparer.OrdinalIgnoreCase),
					new HashSet<string>(StringComparer.OrdinalIgnoreCase))
			]
		};

		Assert.True(rules.IsSmartIgnoredDirectory(scopedGenerated, "generated"));
		Assert.False(rules.IsSmartIgnoredDirectory(outsideGenerated, "generated"));
	}

	[Fact]
	public void IsSmartIgnoredDirectory_NearestNestedProjectOwnsTheFolderDecision()
	{
		using var temp = new TemporaryDirectory();
		var parentGenerated = temp.CreateFolder("workspace/generated");
		var nestedGenerated = temp.CreateFolder("workspace/service/generated");
		var generated = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "generated" };
		var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var matchers = new[]
		{
			new ScopedSmartIgnoreMatcher(
				Path.Combine(temp.Path, "workspace"),
				generated,
				empty,
				empty),
			new ScopedSmartIgnoreMatcher(
				Path.Combine(temp.Path, "workspace", "service"),
				empty,
				empty,
				empty)
		};
		var rules = new IgnoreRules(false, false, false, false, generated, empty)
		{
			UseSmartIgnore = true,
			ScopedSmartIgnoreMatchers = matchers,
			ScopedSmartIgnoreCandidateMatchers = matchers,
			SmartIgnoreCandidateFolders = generated
		};

		Assert.True(rules.IsSmartIgnoredDirectory(parentGenerated, "generated"));
		Assert.False(rules.IsSmartIgnoredDirectory(nestedGenerated, "generated"));
		Assert.False(rules.IsSmartIgnoredDirectoryCandidate(nestedGenerated, "generated"));
	}

	[Fact]
	public void IsSmartIgnoredDirectoryCandidate_ReportsArtifactImpactWithoutActivatingSmartIgnore()
	{
		using var temp = new TemporaryDirectory();
		var obj = temp.CreateFolder("obj");
		temp.CreateFile("obj/project.assets.json", "{}");
		var rules = CreateArtifactRules(useSmartIgnore: false) with
		{
			SmartIgnoreCandidateScopeRoots = [temp.Path]
		};

		Assert.False(rules.IsSmartIgnoredDirectory(obj, "obj"));
		Assert.True(rules.IsSmartIgnoredDirectoryCandidate(obj, "obj"));
	}

	[Fact]
	public void EvaluateDirectory_GitIgnoreTraversalFallsThroughToSmartArtifactButHardGitIgnoreOwnsOverlap()
	{
		using var temp = new TemporaryDirectory();
		var obj = temp.CreateFolder("obj");
		temp.CreateFile("obj/project.assets.json", "{}");
		var rules = CreateArtifactRules(useSmartIgnore: true);

		var hardGitDecision = IgnoreDecisionEngine.EvaluateDirectory(
			obj,
			"obj",
			isHidden: false,
			rules,
			new IgnoreRules.GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: false));
		var traversedGitDecision = IgnoreDecisionEngine.EvaluateDirectory(
			obj,
			"obj",
			isHidden: false,
			rules,
			new IgnoreRules.GitIgnoreEvaluation(IsIgnored: true, ShouldTraverseIgnoredDirectory: true));

		Assert.Equal(IgnoreDecisionOwner.GitIgnore, hardGitDecision.Owner);
		Assert.Equal(IgnoreDecisionOwner.SmartIgnore, traversedGitDecision.Owner);
	}

	[Theory]
	[InlineData("App.sln.DotSettings.user")]
	[InlineData("App.csproj.user")]
	[InlineData("App.fsproj.user")]
	[InlineData("App.vbproj.user")]
	public void IsSmartIgnoredFile_UserSpecificProjectState_IsPortableAcrossStackScopes(string fileName)
	{
		using var temp = new TemporaryDirectory();
		var fullPath = temp.CreateFile(fileName, "local state");
		var rules = CreateArtifactRules(useSmartIgnore: true);

		Assert.True(rules.IsSmartIgnoredFile(fullPath, fileName, shouldApplySmartIgnore: true));
		Assert.True(rules.IsSmartIgnoredFile(fullPath, fileName, shouldApplySmartIgnore: false));
	}

	[Fact]
	public void IsSmartIgnoredFile_StackDescriptorNameStillRequiresApplicableScope()
	{
		using var temp = new TemporaryDirectory();
		var fileName = "generated.lock";
		var fullPath = temp.CreateFile(fileName, "generated");
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fileName })
		{
			UseSmartIgnore = true
		};

		Assert.True(rules.IsSmartIgnoredFile(fullPath, fileName, shouldApplySmartIgnore: true));
		Assert.False(rules.IsSmartIgnoredFile(fullPath, fileName, shouldApplySmartIgnore: false));
	}

	[Fact]
	public void IsSmartIgnoredFile_SharedProjectStateAndUnrelatedUserSuffixRemainVisible()
	{
		using var temp = new TemporaryDirectory();
		var sharedSettings = temp.CreateFile("App.sln.DotSettings", "shared state");
		var unrelatedUserFile = temp.CreateFile("notes.user", "notes");
		var rules = CreateArtifactRules(useSmartIgnore: true);

		Assert.False(rules.IsSmartIgnoredFile(
			sharedSettings,
			Path.GetFileName(sharedSettings),
			shouldApplySmartIgnore: true));
		Assert.False(rules.IsSmartIgnoredFile(
			unrelatedUserFile,
			Path.GetFileName(unrelatedUserFile),
			shouldApplySmartIgnore: true));
	}

	[Fact]
	public void IsSmartIgnoredFileCandidate_ReportsUserStateImpactWithoutActivatingSmartIgnore()
	{
		using var temp = new TemporaryDirectory();
		var fileName = "App.csproj.user";
		var fullPath = temp.CreateFile(fileName, "local state");
		var rules = CreateArtifactRules(useSmartIgnore: false);

		Assert.False(rules.IsSmartIgnoredFile(fullPath, fileName, shouldApplySmartIgnore: true));
		Assert.True(rules.IsSmartIgnoredFileCandidate(fullPath, fileName, shouldApplySmartIgnore: true));
		Assert.True(rules.IsSmartIgnoredFileCandidate(fullPath, fileName, shouldApplySmartIgnore: false));
	}

	[Fact]
	public void IsSmartIgnoredDirectory_ConcurrentRepeatedFingerprintQueriesRemainStable()
	{
		using var temp = new TemporaryDirectory();
		var packagesPath = temp.CreateFolder("packages");
		temp.CreateFile("packages/Alpha/Alpha.nupkg", "package");
		temp.CreateFolder("packages/Alpha/lib");
		temp.CreateFile("packages/Beta/Beta.nupkg", "package");
		temp.CreateFolder("packages/Beta/ref");
		var rules = CreateArtifactRules(useSmartIgnore: true);
		var results = new bool[128];

		Parallel.For(0, results.Length, index =>
		{
			results[index] = index % 2 == 0
				? rules.IsSmartIgnoredDirectory(packagesPath, "packages")
				: rules.IsSmartIgnoredDirectoryCandidate(packagesPath, "packages");
		});

		Assert.All(results, result => Assert.True(result));
	}

	private static IgnoreRules CreateArtifactRules(bool useSmartIgnore) => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
		SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
	{
		UseSmartIgnore = useSmartIgnore,
		SmartArtifactIgnoreMatcher = useSmartIgnore
			? SmartArtifactIgnoreMatcher.Default
			: SmartArtifactIgnoreMatcher.Empty,
		SmartArtifactIgnoreCandidateMatcher = SmartArtifactIgnoreMatcher.Default,
		SmartIgnoreCandidateScopeRoots = [string.Empty]
	};
}
