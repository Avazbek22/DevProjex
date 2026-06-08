using DevProjex.Tests.Shared.ProjectLoadWorkflow;
using static DevProjex.Tests.Shared.ProjectLoadWorkflow.ProjectLoadWorkflowRefreshHarness;

namespace DevProjex.Tests.Integration;

public sealed class IgnoreLogicDesktopScaleContractIntegrationTests
{
	private const int DesktopScaleDotFolderCount = 250;
	private const int DesktopRegressionVisibleDotFolderCount = 100;
	private const int DesktopRegressionControllerOwnedDotFolderCount = 150;
	private const int GitMaskedDotFolderCount = 17;
	private const int MaximumConvergencePasses = 6;

	[Fact]
	public void DesktopScaleDotFolderCounts_StayStableAcrossSelectionScanAndWorkspaceInventory()
	{
		using var workspace = CreateDesktopScaleWorkspace();
		var services = CreateServices();

		var regularSnapshot = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateDefaultContext(workspace.Path));
		var inventorySnapshot = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateDefaultContext(workspace.Path) with { CaptureTreeInventory = true });

		// This is the Desktop-scale regression guard: the same project state must not
		// produce 100 here and 250 elsewhere just because another scan/projection path ran.
		AssertDesktopScaleDotFolderCount(regularSnapshot, DesktopScaleDotFolderCount);
		AssertDesktopScaleDotFolderCount(inventorySnapshot, DesktopScaleDotFolderCount);
		AssertEquivalentVisibleSnapshots(regularSnapshot, inventorySnapshot);

		var inventory = Assert.IsType<ProjectTreeInventorySnapshot>(inventorySnapshot.TreeInventory);
		Assert.Equal(DesktopScaleDotFolderCount, CountRootDotDirectories(inventory));

		AssertScanPipelinesAgree(
			services,
			workspace.Path,
			regularSnapshot,
			expectedDotFolders: DesktopScaleDotFolderCount);
		AssertTreeProjectionMatchesDirectBuild(
			services,
			workspace.Path,
			regularSnapshot,
			inventory,
			expectedRootDotFolders: 0);
	}

	[Fact]
	public void DesktopScaleDotFolderCounts_RemainStableAcrossLiveRootAndExtensionChanges()
	{
		using var workspace = CreateDesktopScaleWorkspace();
		var services = CreateServices();
		var baseline = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateDefaultContext(workspace.Path));

		var srcOnlyContext = CreateSingleRootContext(workspace.Path, baseline, "src");
		var srcOnlyLiveSnapshot = services.Engine.ComputeLiveRefreshSnapshot(
			srcOnlyContext,
			new HashSet<string>(PathComparer.Default) { "src" },
			TestContext.Current.CancellationToken);

		// Live root filtering may narrow content roots, but root-level directory-toggle
		// evidence still belongs to the current project state when root state is complete.
		AssertDesktopScaleDotFolderCount(srcOnlyLiveSnapshot, DesktopScaleDotFolderCount);
		Assert.DoesNotContain(srcOnlyLiveSnapshot.ExtensionOptions, option =>
			string.Equals(option.Name, ".md", StringComparison.OrdinalIgnoreCase) && !option.IsChecked);

		var csOnlySnapshot = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateExtensionSubsetContext(workspace.Path, baseline, ".cs"));

		// Extension filtering must not rewrite directory-toggle counts. A dot folder is
		// structural evidence even when its child files have currently unchecked extensions.
		AssertDesktopScaleDotFolderCount(csOnlySnapshot, DesktopScaleDotFolderCount);
		Assert.Contains(csOnlySnapshot.ExtensionOptions, option =>
			string.Equals(option.Name, ".cs", StringComparison.OrdinalIgnoreCase) && option.IsChecked);
		Assert.Contains(csOnlySnapshot.ExtensionOptions, option =>
			string.Equals(option.Name, ".md", StringComparison.OrdinalIgnoreCase) && !option.IsChecked);
		Assert.Contains(csOnlySnapshot.ExtensionOptions, option =>
			string.Equals(option.Name, ".txt", StringComparison.OrdinalIgnoreCase) && !option.IsChecked);
	}

	[Fact]
	public void DesktopScaleDotFolderCounts_DotFoldersOffProjectsCapturedBroadInventory()
	{
		using var workspace = CreateDesktopScaleWorkspace();
		var services = CreateServices();
		var baseline = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateDefaultContext(workspace.Path) with { CaptureTreeInventory = true });
		var baselineInventory = Assert.IsType<ProjectTreeInventorySnapshot>(baseline.TreeInventory);

		var dotFoldersOff = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateForcedIgnoreContext(workspace.Path, baseline, new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.DotFolders] = false
			}));

		// The option remains visible and counted when unchecked: it still has an observable
		// inverse effect, and broad inventory must be able to project that expanded state.
		AssertDesktopScaleDotFolderCount(dotFoldersOff, DesktopScaleDotFolderCount, expectedChecked: false);
		Assert.Equal(DesktopScaleDotFolderCount, CountRootDotOptions(dotFoldersOff));
		AssertTreeProjectionMatchesDirectBuild(
			services,
			workspace.Path,
			dotFoldersOff,
			baselineInventory,
			expectedRootDotFolders: DesktopScaleDotFolderCount);
	}

	[Fact]
	public void DesktopScaleDotFolderCounts_ControllerMasksOnlyTheFoldersItOwns()
	{
		using var workspace = CreateDesktopScaleWorkspace(
			gitMaskedDotFolders: GitMaskedDotFolderCount,
			includeGitIgnore: true);
		var services = CreateServices();
		var defaults = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateDefaultContext(workspace.Path));

		Assert.Contains(defaults.IgnoreOptions, option =>
			option.Id == IgnoreOptionId.UseGitIgnore && option.IsChecked);
		AssertDesktopScaleDotFolderCount(defaults, DesktopScaleDotFolderCount);

		var gitIgnoreOff = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateForcedIgnoreContext(workspace.Path, defaults, new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.UseGitIgnore] = false
			}));

		// Help 11.9 says basic counters are rule-specific impact. Git-owned dot roots
		// become DotFolders impact only after the Git controller is disabled.
		Assert.Contains(gitIgnoreOff.IgnoreOptions, option =>
			option.Id == IgnoreOptionId.UseGitIgnore && !option.IsChecked);
		AssertDesktopScaleDotFolderCount(
			gitIgnoreOff,
			DesktopScaleDotFolderCount + GitMaskedDotFolderCount);
	}

	[Fact]
	public void DesktopScaleDotFolderCounts_OneHundredRuleOwnedPlusControllerOwnedStaysStable()
	{
		using var workspace = CreateDesktopScaleWorkspace(
			dotFolderCount: DesktopRegressionVisibleDotFolderCount,
			gitMaskedDotFolders: DesktopRegressionControllerOwnedDotFolderCount,
			includeGitIgnore: true);
		var services = CreateServices();
		var allDotFolders =
			DesktopRegressionVisibleDotFolderCount + DesktopRegressionControllerOwnedDotFolderCount;

		var defaults = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateDefaultContext(workspace.Path));

		// This is the exact "Desktop has 250 dot folders but DotFolders shows 100" guard:
		// 100 is correct while the Git controller owns the other 150, and it must stay
		// stable across refresh paths instead of oscillating between rule-owned and total.
		Assert.Contains(defaults.IgnoreOptions, option =>
			option.Id == IgnoreOptionId.UseGitIgnore && option.IsChecked);
		AssertDesktopScaleDotFolderCount(defaults, DesktopRegressionVisibleDotFolderCount);

		var gitIgnoreOff = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateForcedIgnoreContext(workspace.Path, defaults, new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.UseGitIgnore] = false
			}));

		Assert.Contains(gitIgnoreOff.IgnoreOptions, option =>
			option.Id == IgnoreOptionId.UseGitIgnore && !option.IsChecked);
		AssertDesktopScaleDotFolderCount(gitIgnoreOff, allDotFolders);
	}

	[Fact]
	public void DesktopScaleDotFolderCounts_SmartIgnoreTogglesDoNotChangeUnrelatedDotFolders()
	{
		using var workspace = CreateDesktopScaleWorkspace(includeSmartIgnoreCandidate: true);
		var services = CreateServices();
		var defaults = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateDefaultContext(workspace.Path));

		Assert.Contains(defaults.IgnoreOptions, option =>
			option.Id == IgnoreOptionId.SmartIgnore && option.IsChecked);
		AssertDesktopScaleDotFolderCount(defaults, DesktopScaleDotFolderCount);

		var smartIgnoreOff = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateForcedIgnoreContext(workspace.Path, defaults, new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.SmartIgnore] = false
			}));

		// Smart ignore owns artifact folders such as node_modules/bin/obj. It must not
		// silently steal responsibility for dot-prefixed roots from the DotFolders toggle.
		Assert.Contains(smartIgnoreOff.IgnoreOptions, option =>
			option.Id == IgnoreOptionId.SmartIgnore && !option.IsChecked);
		AssertDesktopScaleDotFolderCount(smartIgnoreOff, DesktopScaleDotFolderCount);
	}

	private static TemporaryDirectory CreateDesktopScaleWorkspace(
		int dotFolderCount = DesktopScaleDotFolderCount,
		int gitMaskedDotFolders = 0,
		bool includeGitIgnore = false,
		bool includeSmartIgnoreCandidate = false)
	{
		var workspace = new TemporaryDirectory();
		workspace.CreateFile("src/App.cs", "public sealed class App {}\n");
		workspace.CreateFile("docs/readme.md", "# docs\n");
		workspace.CreateFile("assets/notes.txt", "asset notes\n");
		workspace.CreateFile("README.md", "# root\n");

		if (includeGitIgnore)
		{
			workspace.CreateFile("DesktopScale.csproj", "<Project />\n");
			workspace.CreateFile(".gitignore", "git-logs/\n.git-owned-*/\n");
			workspace.CreateFile("git-logs/runtime.log", "ignored runtime log\n");
		}

		if (includeSmartIgnoreCandidate)
		{
			workspace.CreateFile("package.json", "{}\n");
			workspace.CreateFile("node_modules/pkg/index.js", "module.exports = {};\n");
		}

		for (var index = 0; index < dotFolderCount; index++)
		{
			workspace.CreateFile(
				Path.Combine($".desktop-dot-{index:D3}", "payload.txt"),
				$"dot payload {index}\n");
		}

		for (var index = 0; index < gitMaskedDotFolders; index++)
		{
			workspace.CreateFile(
				Path.Combine($".git-owned-{index:D2}", "payload.txt"),
				$"git owned dot payload {index}\n");
		}

		return workspace;
	}

	private static SelectionRefreshSnapshot ComputeConvergedSnapshot(
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		string rootPath,
		SelectionRefreshContext context)
	{
		var previous = services.Engine.ComputeFullRefreshSnapshot(
			context,
			TestContext.Current.CancellationToken);

		for (var pass = 0; pass < MaximumConvergencePasses; pass++)
		{
			var next = services.Engine.ComputeFullRefreshSnapshot(
				CreateContextFromSnapshot(rootPath, previous) with
				{
					CaptureTreeInventory = context.CaptureTreeInventory
				},
				TestContext.Current.CancellationToken);

			if (AreEquivalentVisibleSnapshots(previous, next))
				return next;

			previous = next;
		}

		var final = services.Engine.ComputeFullRefreshSnapshot(
			CreateContextFromSnapshot(rootPath, previous) with
			{
				CaptureTreeInventory = context.CaptureTreeInventory
			},
			TestContext.Current.CancellationToken);
		AssertEquivalentVisibleSnapshots(previous, final);
		return final;
	}

	private static bool AreEquivalentVisibleSnapshots(
		SelectionRefreshSnapshot expected,
		SelectionRefreshSnapshot actual)
	{
		try
		{
			AssertEquivalentVisibleSnapshots(expected, actual);
			return true;
		}
		catch (Xunit.Sdk.XunitException)
		{
			return false;
		}
	}

	private static SelectionRefreshContext CreateSingleRootContext(
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		string rootName)
	{
		var rootStates = snapshot.RootOptions?.ToDictionary(
			option => option.Name,
			option => string.Equals(option.Name, rootName, StringComparison.Ordinal),
			PathComparer.Default) ?? new Dictionary<string, bool>(PathComparer.Default);

		return CreateContextFromSnapshot(rootPath, snapshot) with
		{
			AllRootFoldersChecked = false,
			RootSelectionInitialized = true,
			RootSelectionCache = new HashSet<string>(PathComparer.Default) { rootName },
			RootOptionStateCache = rootStates
		};
	}

	private static SelectionRefreshContext CreateExtensionSubsetContext(
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		string extensionName)
	{
		var extensionStates = snapshot.ExtensionOptions.ToDictionary(
			option => option.Name,
			option => string.Equals(option.Name, extensionName, StringComparison.OrdinalIgnoreCase),
			StringComparer.OrdinalIgnoreCase);

		return CreateContextFromSnapshot(rootPath, snapshot) with
		{
			AllExtensionsChecked = false,
			ExtensionsSelectionInitialized = true,
			ExtensionsSelectionCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { extensionName },
			ExtensionOptionStateCache = extensionStates
		};
	}

	private static SelectionRefreshContext CreateForcedIgnoreContext(
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		IReadOnlyDictionary<IgnoreOptionId, bool> forcedStates)
	{
		var selected = CollectCheckedIgnoreOptionIds(snapshot);
		var stateCache = new Dictionary<IgnoreOptionId, bool>(snapshot.IgnoreOptionStateCache);

		foreach (var (optionId, isChecked) in forcedStates)
		{
			stateCache[optionId] = isChecked;
			if (isChecked)
				selected.Add(optionId);
			else
				selected.Remove(optionId);
		}

		return CreateContextFromSnapshot(rootPath, snapshot) with
		{
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = selected,
			IgnoreOptionStateCache = stateCache,
			IgnoreAllPreference = null,
			IgnoreOptionStateCacheIsComplete = true
		};
	}

	private static void AssertDesktopScaleDotFolderCount(
		SelectionRefreshSnapshot snapshot,
		int expectedDotFolders,
		bool expectedChecked = true)
	{
		Assert.True(
			snapshot.HasIgnoreOptionCounts,
			$"Snapshot did not publish ignore counts. {DescribeSnapshot(snapshot)}");
		Assert.Equal(expectedDotFolders, snapshot.IgnoreOptionCounts.DotFolders);
		var dotFolders = AssertIgnoreOption(snapshot, IgnoreOptionId.DotFolders, expectedVisible: true, expectedChecked);
		Assert.Contains($"({expectedDotFolders})", dotFolders.Label);
	}

	private static void AssertScanPipelinesAgree(
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		int expectedDotFolders)
	{
		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var selectedRoots = CollectCheckedRootNames(snapshot);
		var selectedExtensions = CollectCheckedExtensionNames(snapshot);
		var selectedIgnoreOptions = CollectCheckedIgnoreOptionIds(snapshot);
		var rules = services.IgnoreRulesService.Build(rootPath, selectedIgnoreOptions, selectedRoots);

		var ignoreSection = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			rootPath,
			selectedRoots,
			rules,
			rules,
			selectedExtensions,
			includeDirectoryToggleProbeRoots: true,
			cancellationToken: TestContext.Current.CancellationToken);
		var workspaceSnapshot = scanOptions.GetProjectWorkspaceSnapshotForRootFolders(
			rootPath,
			selectedRoots,
			rules,
			rules,
			new ExtensionSetInclusionPolicy(selectedExtensions),
			includeDirectoryToggleProbeRoots: true,
			cancellationToken: TestContext.Current.CancellationToken);
		var explicitCounts = scanOptions.GetEffectiveIgnoreOptionCountsForRootFolders(
			rootPath,
			selectedRoots,
			selectedExtensions,
			rules,
			ignoreSection.Value.RawIgnoreOptionCounts,
			includeDirectoryToggleProbeRoots: true,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(expectedDotFolders, ignoreSection.Value.EffectiveIgnoreOptionCounts.DotFolders);
		Assert.Equal(expectedDotFolders, workspaceSnapshot.Value.IgnoreSection.EffectiveIgnoreOptionCounts.DotFolders);
		Assert.Equal(expectedDotFolders, explicitCounts.Value.DotFolders);
		Assert.Equal(ignoreSection.Value.EffectiveIgnoreOptionCounts, workspaceSnapshot.Value.IgnoreSection.EffectiveIgnoreOptionCounts);
		Assert.NotNull(workspaceSnapshot.Value.TreeInventory);
	}

	private static void AssertTreeProjectionMatchesDirectBuild(
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		ProjectTreeInventorySnapshot inventory,
		int expectedRootDotFolders)
	{
		var selectedRoots = CollectCheckedRootNames(snapshot);
		var selectedExtensions = CollectCheckedExtensionNames(snapshot);
		var selectedIgnoreOptions = CollectCheckedIgnoreOptionIds(snapshot);
		var rules = services.IgnoreRulesService.Build(rootPath, selectedIgnoreOptions, selectedRoots);
		var options = new TreeFilterOptions(
			AllowedExtensions: selectedExtensions,
			AllowedRootFolders: selectedRoots,
			IgnoreRules: rules);
		var builder = new TreeBuilder();

		var direct = builder.Build(rootPath, options, TestContext.Current.CancellationToken);
		var projected = builder.Build(inventory, options, TestContext.Current.CancellationToken);

		Assert.Equal(FlattenTree(direct.Root), FlattenTree(projected.Root));
		Assert.Equal(expectedRootDotFolders, CountRootDotDirectories(projected.Root));
	}

	private static int CountRootDotOptions(SelectionRefreshSnapshot snapshot)
	{
		Assert.NotNull(snapshot.RootOptions);
		var count = 0;
		foreach (var option in snapshot.RootOptions)
		{
			if (IgnoreRuleSemantics.IsDotName(option.Name))
				count++;
		}

		return count;
	}

	private static int CountRootDotDirectories(ProjectTreeInventorySnapshot inventory)
	{
		var count = 0;
		var children = inventory.GetChildren(0);
		for (var index = 0; index < children.Length; index++)
		{
			var entry = children[index];
			if (entry.IsDirectory && IgnoreRuleSemantics.IsDotName(entry.Name))
				count++;
		}

		return count;
	}

	private static int CountRootDotDirectories(FileSystemNode root)
	{
		var count = 0;
		foreach (var child in root.Children)
		{
			if (child.IsDirectory && IgnoreRuleSemantics.IsDotName(child.Name))
				count++;
		}

		return count;
	}

	private static List<string> FlattenTree(FileSystemNode root)
	{
		var paths = new List<string>();
		var pending = new Stack<FileSystemNode>();
		pending.Push(root);
		while (pending.Count > 0)
		{
			var node = pending.Pop();
			paths.Add($"{node.FullPath}|{node.IsDirectory}|{node.IsAccessDenied}");
			for (var index = node.Children.Count - 1; index >= 0; index--)
				pending.Push(node.Children[index]);
		}

		return paths;
	}

	private static ResolvedIgnoreOptionState AssertIgnoreOption(
		SelectionRefreshSnapshot snapshot,
		IgnoreOptionId optionId,
		bool expectedVisible,
		bool expectedChecked)
	{
		var options = snapshot.IgnoreOptions.Where(option => option.Id == optionId).ToArray();
		if (!expectedVisible)
		{
			Assert.True(
				options.Length == 0,
				$"Expected ignore option '{optionId}' to be hidden, but it was visible. {DescribeSnapshot(snapshot)}");
			return default;
		}

		Assert.True(
			options.Length == 1,
			$"Expected ignore option '{optionId}' to be visible once, but found {options.Length}. {DescribeSnapshot(snapshot)}");
		Assert.Equal(expectedChecked, options[0].IsChecked);
		return options[0];
	}

	private static string DescribeSnapshot(SelectionRefreshSnapshot snapshot)
	{
		var roots = snapshot.RootOptions is null
			? "<null>"
			: string.Join(", ", snapshot.RootOptions.Select(option => $"{option.Name}:{option.IsChecked}"));
		var extensions = string.Join(", ", snapshot.ExtensionOptions.Select(option => $"{option.Name}:{option.IsChecked}"));
		var ignore = string.Join(", ", snapshot.IgnoreOptions.Select(option => $"{option.Id}:{option.IsChecked}:{option.Label}"));
		return $"Roots=[{roots}] Extensions=[{extensions}] Ignore=[{ignore}] Counts={snapshot.IgnoreOptionCounts} Controller={snapshot.ControllerImpactCounts}";
	}
}
