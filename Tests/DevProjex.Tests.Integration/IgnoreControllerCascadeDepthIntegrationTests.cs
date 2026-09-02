namespace DevProjex.Tests.Integration;

public sealed class IgnoreControllerCascadeDepthIntegrationTests
{
	[Fact]
	public void SmartIgnore_DeepProjectBeyondEagerDiscoveryDepth_HidesOnlyOwnedArtifactScope()
	{
		using var temp = new TemporaryDirectory();
		var deepProjectRelativePath = string.Join(
			'/',
			Enumerable.Range(0, 12).Select(static index => $"level-{index}").Append("web"));
		temp.CreateFile($"{deepProjectRelativePath}/package.json", "{}");
		temp.CreateFile($"{deepProjectRelativePath}/src/main.js", "export const ok = true;");
		temp.CreateFile($"{deepProjectRelativePath}/node_modules/pkg/index.js", "generated");
		temp.CreateFile("archive/node_modules/keep.js", "ordinary source");

		var service = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();
		var rules = service.Build(
			temp.Path,
			[IgnoreOptionId.SmartIgnore],
			selectedRootFolders: ["level-0", "archive"]);
		var deepArtifactPath = Path.Combine(
			temp.Path,
			deepProjectRelativePath.Replace('/', Path.DirectorySeparatorChar),
			"node_modules");
		var siblingLookalikePath = Path.Combine(temp.Path, "archive", "node_modules");

		Assert.DoesNotContain(
			rules.SmartIgnoreCandidateScopeRoots,
			scope => PathComparer.Default.Equals(
				scope,
				Path.GetDirectoryName(deepArtifactPath)));
		Assert.True(rules.IsSmartIgnoredDirectoryCandidate(deepArtifactPath, "node_modules"));
		Assert.True(rules.IsSmartIgnoredDirectory(deepArtifactPath, "node_modules"));
		Assert.False(rules.IsSmartIgnoredDirectoryCandidate(siblingLookalikePath, "node_modules"));
		Assert.False(rules.IsSmartIgnoredDirectory(siblingLookalikePath, "node_modules"));

		var tree = new TreeBuilder().Build(
			temp.Path,
			new TreeFilterOptions(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".js", ".json" },
				new HashSet<string>(PathComparer.Default) { "level-0", "archive" },
				rules),
			TestContext.Current.CancellationToken);

		Assert.False(ContainsPath(tree, $"{deepProjectRelativePath}/node_modules/pkg/index.js"));
		Assert.True(ContainsPath(tree, $"{deepProjectRelativePath}/src/main.js"));
		Assert.True(ContainsPath(tree, "archive/node_modules/keep.js"));
	}

	[Theory]
	[InlineData(false, false, true, 1)]
	[InlineData(true, false, false, 0)]
	[InlineData(false, true, false, 1)]
	[InlineData(true, true, false, 0)]
	public void GitThenSmartResidualMatrix_AssignsOverlappingArtifactToOneActiveController(
		bool useGitIgnore,
		bool useSmartIgnore,
		bool expectedArtifactVisible,
		int expectedSmartImpactMinimum)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "__pycache__/\n");
		temp.CreateFile("requirements.txt", "pytest\n");
		temp.CreateFile("src/app.py", "print('ok')\n");
		temp.CreateFile("__pycache__/app.pyc", "binary");

		var selectedOptions = new List<IgnoreOptionId>();
		if (useGitIgnore)
			selectedOptions.Add(IgnoreOptionId.UseGitIgnore);
		if (useSmartIgnore)
			selectedOptions.Add(IgnoreOptionId.SmartIgnore);

		var service = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();
		var rules = service.Build(temp.Path, selectedOptions, selectedRootFolders: []);
		var scan = new ScanOptionsUseCase(new FileSystemScanner())
			.GetProjectWorkspaceSnapshotForRootFolders(
				temp.Path,
				rootFolders: [],
				extensionDiscoveryRules: rules,
				effectiveRules: rules,
				effectiveExtensionPolicy: new ExtensionSetInclusionPolicy(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".py", ".pyc" }),
				includeDirectoryToggleProbeRoots: true,
				cancellationToken: TestContext.Current.CancellationToken,
				includeControllerImpactProbeRoots: true);
		var tree = new TreeBuilder().Build(
			temp.Path,
				new TreeFilterOptions(
					new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".py", ".pyc" },
					new HashSet<string>(PathComparer.Default) { "__pycache__", "src" },
					rules),
			TestContext.Current.CancellationToken);

		Assert.Equal(expectedArtifactVisible, ContainsPath(tree, "__pycache__/app.pyc"));
		Assert.True(scan.Value.IgnoreSection.ControllerImpactCounts.GitIgnore > 0);
		var smartImpact = scan.Value.IgnoreSection.ControllerImpactCounts.SmartIgnore;
		Assert.True(
			expectedSmartImpactMinimum > 0
				? smartImpact >= expectedSmartImpactMinimum
				: smartImpact == 0,
			$"Unexpected Smart Ignore impact count: {smartImpact}.");
	}

	private static bool ContainsPath(TreeBuildResult tree, string relativePath)
	{
		var segments = relativePath.Split(
			['/', '\\'],
			StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		IReadOnlyList<FileSystemNode> children = tree.Root.Children;
		foreach (var segment in segments)
		{
			var match = children.FirstOrDefault(node =>
				string.Equals(node.Name, segment, StringComparison.OrdinalIgnoreCase));
			if (match is null)
				return false;
			children = match.Children;
		}

		return true;
	}
}
