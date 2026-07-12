namespace DevProjex.Tests.Unit;

public sealed class IgnoreRulesSmartArtifactScopeTests
{
	[Fact]
	public void IsSmartIgnoredDirectory_ArtifactMatcherRespectsActiveSmartScopeRoots()
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
