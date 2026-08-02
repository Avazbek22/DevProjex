namespace DevProjex.Tests.Unit;

public sealed class SmartArtifactIgnoreBoundaryTests
{
	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	public void RootFactsUnavailableMatrix_NeverReportsCandidateOrConfirmedArtifact(
		bool exists,
		bool isAccessible)
	{
		using var temp = new TemporaryDirectory();
		var candidatePath = temp.CreateFolder("obj");
		temp.CreateFile("obj/project.assets.json", "{}");
		var facts = new ProjectRootFacts(
			temp.Path,
			exists,
			isAccessible,
			files: [],
			directories:
			[
				new ProjectRootDirectoryFact("obj", candidatePath, IsReparsePoint: false)
			],
			gitIgnoreSignature: null);

		Assert.False(SmartArtifactIgnoreMatcher.Default.HasCandidateDirectory(facts));
		Assert.False(SmartArtifactIgnoreMatcher.Default.HasConfirmedArtifactDirectory(facts));
	}

	[Fact]
	public void HasConfirmedArtifactDirectory_ReparsePointFactDoesNotOwnRealArtifactTarget()
	{
		using var temp = new TemporaryDirectory();
		var candidatePath = temp.CreateFolder("obj");
		temp.CreateFile("obj/project.assets.json", "{}");
		var facts = new ProjectRootFacts(
			temp.Path,
			exists: true,
			isAccessible: true,
			files: [],
			directories:
			[
				new ProjectRootDirectoryFact("obj", candidatePath, IsReparsePoint: true)
			],
			gitIgnoreSignature: null);

		Assert.False(SmartArtifactIgnoreMatcher.Default.HasCandidateDirectory(facts));
		Assert.False(SmartArtifactIgnoreMatcher.Default.HasConfirmedArtifactDirectory(facts));
	}

	[Fact]
	public void CustomMatcher_PortableEvaluationFiltersBothExactAndPrefixRules()
	{
		using var temp = new TemporaryDirectory();
		var scopedPath = temp.CreateFolder("scoped");
		var portablePath = temp.CreateFolder("portable");
		var portablePrefixPath = temp.CreateFolder("portable-cache");
		temp.CreateFile("scoped/signature", "marker");
		temp.CreateFile("portable/signature", "marker");
		temp.CreateFile("portable-cache/signature", "marker");
		var matcher = new SmartArtifactIgnoreMatcher(
		[
			SmartArtifactIgnoreMatcher.SmartArtifactDirectoryRule.Exact(
				"scoped",
				files: ["signature"]),
			SmartArtifactIgnoreMatcher.SmartArtifactDirectoryRule.Exact(
				"portable",
				files: ["signature"],
				applyOutsideProjectScopes: true),
			SmartArtifactIgnoreMatcher.SmartArtifactDirectoryRule.Prefix(
				"portable-",
				files: ["signature"],
				applyOutsideProjectScopes: true)
		]);

		Assert.True(matcher.IsIgnoredDirectory(scopedPath, "scoped"));
		Assert.False(matcher.IsPortableIgnoredDirectory(scopedPath, "scoped"));
		Assert.True(matcher.IsPortableIgnoredDirectory(portablePath, "portable"));
		Assert.True(matcher.IsPortableIgnoredDirectory(portablePrefixPath, "portable-cache"));
	}

	[Fact]
	public void RepeatedChildSignature_EmptyLayoutSetStillRequiresConfiguredMatchThreshold()
	{
		using var temp = new TemporaryDirectory();
		var packagesPath = temp.CreateFolder("packages");
		temp.CreateFile("packages/Alpha/Alpha.pkg", "package");
		var matcher = CreateRepeatedPackageMatcher(layoutDirectories: []);

		Assert.False(matcher.IsIgnoredDirectory(packagesPath, "packages"));

		temp.CreateFile("packages/Beta/Beta.pkg", "package");

		Assert.True(matcher.IsIgnoredDirectory(packagesPath, "packages"));
	}

	[Fact]
	public void RepeatedChildSignature_StopsAfterConfiguredEntryBudgetWithoutMatching()
	{
		using var temp = new TemporaryDirectory();
		var packagesPath = temp.CreateFolder("packages");
		for (var index = 0; index < 12; index++)
			temp.CreateFolder($"packages/source-{index:D2}");
		var matcher = new SmartArtifactIgnoreMatcher(
		[
			SmartArtifactIgnoreMatcher.SmartArtifactDirectoryRule.Exact(
				"packages",
				repeatedChildSignature: new SmartArtifactIgnoreMatcher.RepeatedChildArtifactSignature(
					".pkg",
					["lib"],
					minimumMatches: 2,
					maxEntries: 8))
		]);

		Assert.False(matcher.IsIgnoredDirectory(packagesPath, "packages"));
	}

	[Fact]
	public void EnumeratedSignature_LargeNearMissDirectoryRemainsVisibleAndBounded()
	{
		using var temp = new TemporaryDirectory();
		var cachePath = temp.CreateFolder("cache");
		for (var index = 0; index < 1_100; index++)
			temp.CreateFile($"cache/source-{index:D4}.txt", "source");
		var matcher = new SmartArtifactIgnoreMatcher(
		[
			SmartArtifactIgnoreMatcher.SmartArtifactDirectoryRule.Exact(
				"cache",
				fileSuffixes: [".generated-marker"])
		]);

		Assert.False(matcher.IsIgnoredDirectory(cachePath, "cache"));
	}

	[Theory]
	[InlineData("App.deps.json")]
	[InlineData("App.runtimeconfig.json")]
	public void MultiSegmentFileExtensionSignatures_AreMatchedByFullTerminalSuffix(string fileName)
	{
		using var temp = new TemporaryDirectory();
		var binPath = temp.CreateFolder("bin");
		temp.CreateFile($"bin/{fileName}", "{}");

		Assert.True(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(binPath, "bin"));
	}

	[Fact]
	public void ChildFileSignature_RequiresDirectChildAndDoesNotMatchDeeperSourceFile()
	{
		using var temp = new TemporaryDirectory();
		var componentsPath = temp.CreateFolder("bower_components");
		temp.CreateFile("bower_components/jquery/source/bower.json", "{}");

		Assert.False(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(
			componentsPath,
			"bower_components"));
	}

	[Fact]
	public void SignatureEvidence_DoesNotLeakBetweenSiblingCandidates()
	{
		using var temp = new TemporaryDirectory();
		var firstPath = temp.CreateFolder("first/obj");
		var secondPath = temp.CreateFolder("second/obj");
		temp.CreateFile("first/obj/project.assets.json", "{}");
		temp.CreateFile("second/obj/README.md", "source");

		Assert.True(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(firstPath, "obj"));
		Assert.False(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(secondPath, "obj"));
	}

	[Fact]
	public void CandidateNameCannotBorrowUnrelatedDirectorySignature()
	{
		using var temp = new TemporaryDirectory();
		var objPath = temp.CreateFolder("obj");
		temp.CreateFile("obj/project.assets.json", "{}");

		Assert.False(SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(objPath, "build"));
	}

	[Fact]
	public void CustomIgnoredFileSuffixes_DeduplicateCaseAndDiscardBlankValues()
	{
		var matcher = new SmartArtifactIgnoreMatcher(
			[],
			["", " ", ".local-state", ".LOCAL-STATE", "generated-state"]);

		Assert.True(matcher.HasRules);
		Assert.True(matcher.IsIgnoredFile("App.local-state"));
		Assert.True(matcher.IsIgnoredFile("APP.LOCAL-STATE"));
		Assert.True(matcher.IsIgnoredFile("App.generated-state"));
		Assert.False(matcher.IsIgnoredFile("App.state"));
		Assert.False(matcher.IsIgnoredFile(" "));
	}

	private static SmartArtifactIgnoreMatcher CreateRepeatedPackageMatcher(
		IReadOnlyCollection<string> layoutDirectories) =>
		new(
		[
			SmartArtifactIgnoreMatcher.SmartArtifactDirectoryRule.Exact(
				"packages",
				repeatedChildSignature: new SmartArtifactIgnoreMatcher.RepeatedChildArtifactSignature(
					".pkg",
					layoutDirectories,
					minimumMatches: 2,
					maxEntries: 8))
		]);
}
