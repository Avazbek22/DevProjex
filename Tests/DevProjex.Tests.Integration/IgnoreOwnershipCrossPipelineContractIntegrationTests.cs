using DevProjex.Tests.Shared.ProjectLoadWorkflow;
using static DevProjex.Tests.Shared.ProjectLoadWorkflow.ProjectLoadWorkflowRefreshHarness;

namespace DevProjex.Tests.Integration;

public sealed class IgnoreOwnershipCrossPipelineContractIntegrationTests
{
	private const int RuleOwnedDotFolders = 100;
	private const int GitOwnedDotFolders = 150;

	[Fact]
	public void RootDotDirectoryOwnership_GitAndDotFolders_AgreesAcrossAuditSnapshotAndTreePipelines()
	{
		using var workspace = CreateDotOwnershipWorkspace(RuleOwnedDotFolders, GitOwnedDotFolders);
		var services = CreateServices();

		var baseline = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateDefaultContext(workspace.Path));
		var rules = services.IgnoreRulesService.Build(
			workspace.Path,
			CollectCheckedIgnoreOptionIds(baseline),
			CollectCheckedRootNames(baseline));
		var audit = new IgnoreOwnershipAuditService().AuditRootDirectories(
			workspace.Path,
			rules,
			TestContext.Current.CancellationToken);

		Assert.False(audit.RootAccessDenied);
		Assert.Equal(RuleOwnedDotFolders + GitOwnedDotFolders, audit.PhysicalDotDirectories);
		Assert.Equal(RuleOwnedDotFolders, audit.Count(IgnoreDecisionOwner.DotFolders));
		Assert.Equal(ExpectedGitOwnedRootDirectories(GitOwnedDotFolders), audit.Count(IgnoreDecisionOwner.GitIgnore));
		Assert.Equal(audit.Count(IgnoreDecisionOwner.DotFolders), baseline.IgnoreOptionCounts.DotFolders);

		var options = CreateTreeOptions(workspace.Path, baseline, services.IgnoreRulesService);
		var treeBuilder = new TreeBuilder();
		var directTree = treeBuilder.Build(workspace.Path, options, TestContext.Current.CancellationToken);
		var inventory = treeBuilder.ReadInventory(workspace.Path, options, TestContext.Current.CancellationToken);
		var projectedTree = treeBuilder.Build(inventory, options, TestContext.Current.CancellationToken);

		AssertRootChildrenEqual(directTree.Root, projectedTree.Root);
		Assert.DoesNotContain(directTree.Root.Children, child => child.Name.StartsWith(".", StringComparison.Ordinal));

		var gitOff = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateForcedIgnoreContext(workspace.Path, baseline, new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.UseGitIgnore] = false
			}));
		Assert.Equal(RuleOwnedDotFolders + GitOwnedDotFolders, gitOff.IgnoreOptionCounts.DotFolders);
	}

	[Theory]
	[InlineData(0, 0)]
	[InlineData(1, 0)]
	[InlineData(0, 1)]
	[InlineData(3, 7)]
	[InlineData(17, 11)]
	public void RootDotDirectoryOwnership_SmallMatrix_AuditExplainsSelectionSnapshot(
		int ruleOwnedDotFolders,
		int gitOwnedDotFolders)
	{
		using var workspace = CreateDotOwnershipWorkspace(ruleOwnedDotFolders, gitOwnedDotFolders);
		var services = CreateServices();

		var snapshot = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateDefaultContext(workspace.Path));
		var rules = services.IgnoreRulesService.Build(
			workspace.Path,
			CollectCheckedIgnoreOptionIds(snapshot),
			CollectCheckedRootNames(snapshot));
		var audit = new IgnoreOwnershipAuditService().AuditRootDirectories(
			workspace.Path,
			rules,
			TestContext.Current.CancellationToken);

		Assert.Equal(ruleOwnedDotFolders + gitOwnedDotFolders, audit.PhysicalDotDirectories);
		Assert.Equal(ruleOwnedDotFolders, audit.Count(IgnoreDecisionOwner.DotFolders));
		Assert.Equal(ExpectedGitOwnedRootDirectories(gitOwnedDotFolders), audit.Count(IgnoreDecisionOwner.GitIgnore));
		Assert.Equal(ruleOwnedDotFolders, snapshot.IgnoreOptionCounts.DotFolders);
	}

	[Theory]
	[MemberData(nameof(RandomOwnershipCases))]
	public void RootDotDirectoryOwnership_RandomizedMatrix_AuditAndSnapshotNeverDisagree(int seed)
	{
		var random = new Random(seed);
		var ruleOwnedDotFolders = random.Next(0, 24);
		var gitOwnedDotFolders = random.Next(0, 24);
		using var workspace = CreateDotOwnershipWorkspace(ruleOwnedDotFolders, gitOwnedDotFolders);
		var services = CreateServices();

		var defaults = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateDefaultContext(workspace.Path));
		var defaultRules = services.IgnoreRulesService.Build(
			workspace.Path,
			CollectCheckedIgnoreOptionIds(defaults),
			CollectCheckedRootNames(defaults));
		var defaultAudit = new IgnoreOwnershipAuditService().AuditRootDirectories(
			workspace.Path,
			defaultRules,
			TestContext.Current.CancellationToken);

		Assert.Equal(ruleOwnedDotFolders + gitOwnedDotFolders, defaultAudit.PhysicalDotDirectories);
		Assert.Equal(ruleOwnedDotFolders, defaultAudit.Count(IgnoreDecisionOwner.DotFolders));
		Assert.Equal(ExpectedGitOwnedRootDirectories(gitOwnedDotFolders), defaultAudit.Count(IgnoreDecisionOwner.GitIgnore));
		Assert.Equal(ruleOwnedDotFolders, defaults.IgnoreOptionCounts.DotFolders);

		var gitOff = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateForcedIgnoreContext(workspace.Path, defaults, new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.UseGitIgnore] = false
			}));
		var gitOffRules = services.IgnoreRulesService.Build(
			workspace.Path,
			CollectCheckedIgnoreOptionIds(gitOff),
			CollectCheckedRootNames(gitOff));
		var gitOffAudit = new IgnoreOwnershipAuditService().AuditRootDirectories(
			workspace.Path,
			gitOffRules,
			TestContext.Current.CancellationToken);

		Assert.Equal(0, gitOffAudit.Count(IgnoreDecisionOwner.GitIgnore));
		Assert.Equal(ruleOwnedDotFolders + gitOwnedDotFolders, gitOffAudit.Count(IgnoreDecisionOwner.DotFolders));
		Assert.Equal(ruleOwnedDotFolders + gitOwnedDotFolders, gitOff.IgnoreOptionCounts.DotFolders);
	}

	[Fact]
	public void IgnoreDecisionEngine_FileOwnershipPriority_IsStableForOverlappingFileRules()
	{
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: true,
			IgnoreDotFolders: false,
			IgnoreDotFiles: true,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			IgnoreEmptyFiles = true,
			IgnoreExtensionlessFiles = true
		};

		var dotHiddenEmpty = IgnoreDecisionEngine.EvaluateFile(
			fullPath: @"C:\repo\.env",
			name: ".env",
			isHidden: true,
			length: 0,
			rules,
			shouldApplySmartIgnore: false,
			IgnoreRules.GitIgnoreEvaluation.NotIgnored);
		var extensionlessEmpty = IgnoreDecisionEngine.EvaluateFile(
			fullPath: @"C:\repo\Dockerfile",
			name: "Dockerfile",
			isHidden: false,
			length: 0,
			rules,
			shouldApplySmartIgnore: false,
			IgnoreRules.GitIgnoreEvaluation.NotIgnored);

		Assert.Equal(IgnoreDecisionOwner.DotFiles, dotHiddenEmpty.Owner);
		Assert.Equal(IgnoreDecisionOwner.ExtensionlessFiles, extensionlessEmpty.Owner);
	}

	[Theory]
	[MemberData(nameof(RootRuntimeVisibilityCases))]
	public void RootDirectoryRuntimeVisibility_ScannerAndTreeBuilderAgree(
		string workspaceKind,
		IgnoreOptionId[] selectedIgnoreOptions)
	{
		using var workspace = workspaceKind == "git"
			? CreateRootGitVisibilityWorkspace()
			: CreateRootSmartVisibilityWorkspace();
		var services = CreateServices();
		var rules = services.IgnoreRulesService.Build(
			workspace.Path,
			selectedIgnoreOptions.ToHashSet(),
			selectedRootFolders: null);
		var allRootDirectories = Directory
			.EnumerateDirectories(workspace.Path)
			.Select(Path.GetFileName)
			.Where(name => !string.IsNullOrWhiteSpace(name))
			.Select(name => name!)
			.ToHashSet(PathComparer.Default);
		var scannerRoots = new FileSystemScanner()
			.GetRootFolderNames(workspace.Path, rules, TestContext.Current.CancellationToken)
			.Value
			.OrderBy(name => name, PathComparer.Default)
			.ToArray();
		var tree = new TreeBuilder().Build(
			workspace.Path,
			new TreeFilterOptions(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase)
				{
					".cs", ".json", ".js", ".log", ".md", ".txt", ".xml"
				},
				allRootDirectories,
				rules),
			TestContext.Current.CancellationToken);
		var treeRoots = tree.Root.Children
			.Where(child => child.IsDirectory)
			.Select(child => child.Name)
			.OrderBy(name => name, PathComparer.Default)
			.ToArray();

		Assert.Equal(scannerRoots, treeRoots);
	}

	public static IEnumerable<object[]> RandomOwnershipCases()
	{
		for (var seed = 1000; seed < 1032; seed++)
			yield return [seed];
	}

	public static IEnumerable<object[]> RootRuntimeVisibilityCases()
	{
		yield return ["git", Array.Empty<IgnoreOptionId>()];
		yield return ["git", new[] { IgnoreOptionId.UseGitIgnore }];
		yield return ["git", new[] { IgnoreOptionId.DotFolders }];
		yield return ["git", new[] { IgnoreOptionId.UseGitIgnore, IgnoreOptionId.DotFolders }];
		yield return ["smart", Array.Empty<IgnoreOptionId>()];
		yield return ["smart", new[] { IgnoreOptionId.SmartIgnore }];
		yield return ["smart", new[] { IgnoreOptionId.DotFolders }];
		yield return ["smart", new[] { IgnoreOptionId.SmartIgnore, IgnoreOptionId.DotFolders }];
	}

	private static TemporaryDirectory CreateDotOwnershipWorkspace(
		int ruleOwnedDotFolders,
		int gitOwnedDotFolders)
	{
		var workspace = new TemporaryDirectory();
		workspace.CreateFile(".gitignore", "git-logs/\n.git-owned-*/\n");
		workspace.CreateFile("Project.csproj", "<Project />\n");
		workspace.CreateFile("src/App.cs", "public sealed class App {}\n");
		if (gitOwnedDotFolders > 0)
			workspace.CreateFile("git-logs/runtime.log", "ignored\n");

		for (var index = 0; index < ruleOwnedDotFolders; index++)
		{
			workspace.CreateFile(
				Path.Combine($".rule-owned-{index:D3}", "payload.txt"),
				$"rule-owned {index}\n");
		}

		for (var index = 0; index < gitOwnedDotFolders; index++)
		{
			workspace.CreateFile(
				Path.Combine($".git-owned-{index:D3}", "payload.txt"),
				$"git-owned {index}\n");
		}

		return workspace;
	}

	private static int ExpectedGitOwnedRootDirectories(int gitOwnedDotFolders) =>
		gitOwnedDotFolders == 0 ? 0 : gitOwnedDotFolders + 1;

	private static TemporaryDirectory CreateRootGitVisibilityWorkspace()
	{
		var workspace = new TemporaryDirectory();
		workspace.CreateFile(".gitignore", "git-logs/\n.git-owned/\n");
		workspace.CreateFile("Project.csproj", "<Project />\n");
		workspace.CreateFile("src/App.cs", "public sealed class App {}\n");
		workspace.CreateFile("docs/readme.md", "# docs\n");
		workspace.CreateFile("git-logs/runtime.log", "ignored\n");
		workspace.CreateFile(".git-owned/payload.txt", "ignored dot root\n");
		workspace.CreateFile(".idea/workspace.xml", "<project />\n");
		return workspace;
	}

	private static TemporaryDirectory CreateRootSmartVisibilityWorkspace()
	{
		var workspace = new TemporaryDirectory();
		workspace.CreateFile("package.json", "{ \"name\": \"web\" }\n");
		workspace.CreateFile("src/app.js", "export const ok = true;\n");
		workspace.CreateFile("docs/readme.md", "# docs\n");
		workspace.CreateFile("node_modules/pkg/index.js", "module.exports = {};\n");
		workspace.CreateFile(".idea/workspace.xml", "<project />\n");
		return workspace;
	}

	private static SelectionRefreshSnapshot ComputeConvergedSnapshot(
		WorkflowServices services,
		string rootPath,
		SelectionRefreshContext context)
	{
		var previous = services.Engine.ComputeFullRefreshSnapshot(
			context,
			TestContext.Current.CancellationToken);
		for (var pass = 0; pass < 5; pass++)
		{
			var next = services.Engine.ComputeFullRefreshSnapshot(
				CreateContextFromSnapshot(rootPath, previous),
				TestContext.Current.CancellationToken);
			if (SnapshotsMatch(previous, next))
				return next;

			previous = next;
		}

		return previous;
	}

	private static SelectionRefreshContext CreateForcedIgnoreContext(
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		IReadOnlyDictionary<IgnoreOptionId, bool> overrides)
	{
		var stateCache = new Dictionary<IgnoreOptionId, bool>(snapshot.IgnoreOptionStateCache);
		foreach (var (id, isChecked) in overrides)
			stateCache[id] = isChecked;

		return CreateContextFromSnapshot(rootPath, snapshot) with
		{
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = stateCache
				.Where(pair => pair.Value)
				.Select(pair => pair.Key)
				.ToHashSet(),
			IgnoreOptionStateCache = stateCache,
			IgnoreOptionStateCacheIsComplete = true,
			IgnoreAllPreference = null
		};
	}

	private static TreeFilterOptions CreateTreeOptions(
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		IgnoreRulesService ignoreRulesService)
	{
		var roots = CollectCheckedRootNames(snapshot);
		var extensions = CollectCheckedExtensionNames(snapshot);
		var ignore = CollectCheckedIgnoreOptionIds(snapshot);
		return new TreeFilterOptions(
			extensions,
			roots,
			ignoreRulesService.Build(rootPath, ignore, roots));
	}

	private static bool SnapshotsMatch(SelectionRefreshSnapshot left, SelectionRefreshSnapshot right) =>
		SequenceEqual(left.RootOptions, right.RootOptions) &&
		left.ExtensionOptions.SequenceEqual(right.ExtensionOptions) &&
		left.IgnoreOptions.SequenceEqual(right.IgnoreOptions) &&
		left.IgnoreOptionCounts == right.IgnoreOptionCounts &&
		left.ControllerImpactCounts == right.ControllerImpactCounts;

	private static bool SequenceEqual<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right)
	{
		if (left is null || right is null)
			return left is null && right is null;

		return left.SequenceEqual(right);
	}

	private static void AssertRootChildrenEqual(FileSystemNode expected, FileSystemNode actual)
	{
		var expectedNames = expected.Children.Select(child => child.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
		var actualNames = actual.Children.Select(child => child.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
		Assert.Equal(expectedNames, actualNames);
	}
}
