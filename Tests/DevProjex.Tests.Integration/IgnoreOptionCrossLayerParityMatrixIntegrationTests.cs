using DevProjex.Application.Presentation;
using static DevProjex.Tests.Shared.ProjectLoadWorkflow.ProjectLoadWorkflowRefreshHarness;

namespace DevProjex.Tests.Integration;

public sealed class IgnoreOptionCrossLayerParityMatrixIntegrationTests
{
	private const int MaximumConvergencePasses = 6;

	// This test walks the same contract through Application refresh, Infrastructure
	// scanning, labels, and optional inventory projection. A mismatch here means the UI
	// can publish a count that no longer matches the scanner's active ignore rules.
	[Theory]
	[MemberData(nameof(CrossLayerScenarios))]
	public void FullRefresh_IgnoreCountsLabelsAndScannerSnapshotStayAligned(CrossLayerScenario scenario)
	{
		using var workspace = CreateCrossLayerWorkspace();
		var services = CreateServices();
		var baseline = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateDefaultContext(workspace.Path) with { CaptureTreeInventory = scenario.CaptureTreeInventory });
		var scenarioContext = BuildScenarioContext(workspace.Path, baseline, scenario);
		var snapshot = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			scenarioContext);

		AssertDirectScannerParity(services, workspace.Path, snapshot, scenario);
		AssertIgnoreLabelsMatchPublishedCounts(snapshot, scenario);
		AssertRepeatedRefreshIsStable(services, workspace.Path, snapshot, scenarioContext, scenario);
		AssertTreeInventoryProjectionMatchesDirectTree(workspace.Path, services, snapshot, scenario);
	}

	// Live refresh previously used only visible checked options, while full refresh also
	// used checked state-cache entries. This pins both paths to the same active-rule model.
	[Theory]
	[MemberData(nameof(CrossLayerScenarios))]
	public void LiveRefresh_AfterConvergedFullRefresh_KeepsDynamicSectionsStable(CrossLayerScenario scenario)
	{
		using var workspace = CreateCrossLayerWorkspace();
		var services = CreateServices();
		var baseline = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateDefaultContext(workspace.Path) with { CaptureTreeInventory = scenario.CaptureTreeInventory });
		var scenarioContext = BuildScenarioContext(workspace.Path, baseline, scenario);
		var fullSnapshot = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			scenarioContext);
		var liveSnapshot = services.Engine.ComputeLiveRefreshSnapshot(
			BuildConvergedContext(workspace.Path, fullSnapshot, scenarioContext) with
			{
				CaptureTreeInventory = scenario.CaptureTreeInventory
			},
			TestContext.Current.CancellationToken);

		AssertEquivalentDynamicSections(fullSnapshot, liveSnapshot, scenario);
		AssertIgnoreLabelsMatchPublishedCounts(liveSnapshot, scenario);
	}

	public static IEnumerable<object[]> CrossLayerScenarios()
	{
		// The matrix mixes root filters, extension filters, controller toggles, basic
		// toggles, and inventory capture because most regressions appear only when two
		// selection axes interact.
		yield return [CrossLayerScenario.Defaults("defaults")];
		yield return [CrossLayerScenario.Defaults("defaults with inventory", captureTreeInventory: true)];
		yield return [CrossLayerScenario.CreateAllIgnoreOff("all ignore off")];
		yield return [CrossLayerScenario.CreateAllIgnoreOff("all ignore off with inventory", captureTreeInventory: true)];

		foreach (var optionId in SupportedSingleOptionCases())
		{
			yield return [CrossLayerScenario.CreateSingleIgnoreOn($"single on {optionId}", optionId)];
			yield return [CrossLayerScenario.ForcedIgnoreState($"forced off {optionId}", optionId, isChecked: false)];
		}

		yield return [CrossLayerScenario.CreateRoots("root api only", ["api"])];
		yield return [CrossLayerScenario.CreateRoots("root web only", ["web"])];
		yield return [CrossLayerScenario.CreateRoots("root general only", ["general"])];
		yield return [CrossLayerScenario.CreateRoots("root archive only", ["archive"])];
		yield return [CrossLayerScenario.CreateRoots("roots api+general", ["api", "general"], captureTreeInventory: true)];
		yield return [CrossLayerScenario.CreateRoots("roots web+general", ["web", "general"], captureTreeInventory: true)];

		yield return [CrossLayerScenario.CreateExtensions("extension .cs only", [".cs"])];
		yield return [CrossLayerScenario.CreateExtensions("extension .txt only", [".txt"])];
		yield return [CrossLayerScenario.CreateExtensions("extension .json only", [".json"])];
		yield return [CrossLayerScenario.CreateExtensions("extension .ts only", [".ts"])];
		yield return [CrossLayerScenario.CreateExtensions("extension .log only", [".log"])];
		yield return [CrossLayerScenario.CreateExtensions("extensions .md+.txt", [".md", ".txt"], captureTreeInventory: true)];

		yield return [CrossLayerScenario.Mixed("general txt all off", ["general"], [".txt"], allIgnoreOff: true)];
		yield return [CrossLayerScenario.Mixed("api cs git off", ["api"], [".cs"], forcedIgnoreStates: new Dictionary<IgnoreOptionId, bool>
		{
			[IgnoreOptionId.UseGitIgnore] = false
		})];
		yield return [CrossLayerScenario.Mixed("web ts smart off", ["web"], [".ts"], forcedIgnoreStates: new Dictionary<IgnoreOptionId, bool>
		{
			[IgnoreOptionId.SmartIgnore] = false
		})];
		yield return [CrossLayerScenario.Mixed("general json dot folders off", ["general"], [".json"], forcedIgnoreStates: new Dictionary<IgnoreOptionId, bool>
		{
			[IgnoreOptionId.DotFolders] = false
		})];
		yield return [CrossLayerScenario.Mixed("general txt empty folders off", ["general"], [".txt"], forcedIgnoreStates: new Dictionary<IgnoreOptionId, bool>
		{
			[IgnoreOptionId.EmptyFolders] = false
		}, captureTreeInventory: true)];
		yield return [CrossLayerScenario.Mixed("archive md dot folders off", ["archive"], [".md"], forcedIgnoreStates: new Dictionary<IgnoreOptionId, bool>
		{
			[IgnoreOptionId.DotFolders] = false
		}, captureTreeInventory: true)];
	}

	private static IEnumerable<IgnoreOptionId> SupportedSingleOptionCases()
	{
		yield return IgnoreOptionId.UseGitIgnore;
		yield return IgnoreOptionId.SmartIgnore;
		yield return IgnoreOptionId.DotFolders;
		yield return IgnoreOptionId.DotFiles;
		yield return IgnoreOptionId.EmptyFolders;
		yield return IgnoreOptionId.EmptyFiles;
		yield return IgnoreOptionId.ExtensionlessFiles;

		if (!OperatingSystem.IsWindows())
			yield break;

		yield return IgnoreOptionId.HiddenFolders;
		yield return IgnoreOptionId.HiddenFiles;
	}

	private static TemporaryDirectory CreateCrossLayerWorkspace()
	{
		var workspace = new TemporaryDirectory();
		workspace.CreateFile("api/.gitignore", "logs/\n.git-owned/\n");
		workspace.CreateFile("api/App.csproj", "<Project />\n");
		workspace.CreateFile("api/src/Program.cs", "Console.WriteLine(\"api\");\n");
		workspace.CreateFile("api/logs/runtime.log", "git ignored log\n");
		workspace.CreateFile("api/.git-owned/payload.txt", "git owned dot folder\n");
		workspace.CreateFile("api/bin/Debug/api.dll", "smart ignored binary\n");
		workspace.CreateFile("api/.api-dot/settings.json", "{}\n");

		workspace.CreateFile("web/package.json", "{}\n");
		workspace.CreateFile("web/src/app.ts", "export const ok = true;\n");
		workspace.CreateFile("web/node_modules/pkg/index.js", "smart ignored package\n");
		workspace.CreateFile("web/.cache/cache.json", "{}\n");

		workspace.CreateFile("general/visible.txt", "visible\n");
		workspace.CreateFile("general/.config/settings.json", "{}\n");
		workspace.CreateFile("general/.env", "APP_ENV=test\n");
		workspace.CreateDirectory("general/empty-root");
		workspace.CreateFile("general/empty.txt", string.Empty);
		workspace.CreateFile("general/README", "extensionless\n");
		workspace.CreateFile("general/file.", "trailing dot extensionless\n");

		workspace.CreateFile("archive/docs/readme.md", "# archive\n");
		workspace.CreateFile("archive/.old/notes.md", "# old notes\n");
		workspace.CreateFile("archive/.old/.nested/payload.txt", "nested dot folder\n");
		workspace.CreateFile("archive/plain-empty/only-empty.txt", string.Empty);

		if (OperatingSystem.IsWindows())
		{
			var hiddenRoot = workspace.CreateDirectory("general/hidden-root");
			workspace.CreateFile("general/hidden-root/inside.txt", "hidden folder content\n");
			MarkHidden(hiddenRoot);

			var hiddenDotRoot = workspace.CreateDirectory("general/.hidden-dot-root");
			workspace.CreateFile("general/.hidden-dot-root/inside.txt", "hidden dot content\n");
			MarkHidden(hiddenDotRoot);

			var hiddenFile = workspace.CreateFile("general/hidden-file.secret", "hidden file content\n");
			MarkHidden(hiddenFile);
		}

		return workspace;
	}

	private static SelectionRefreshSnapshot ComputeConvergedSnapshot(
		WorkflowServices services,
		string rootPath,
		SelectionRefreshContext context)
	{
		var currentContext = context;
		var previous = services.Engine.ComputeFullRefreshSnapshot(currentContext, TestContext.Current.CancellationToken);
		for (var pass = 0; pass < MaximumConvergencePasses; pass++)
		{
			currentContext = BuildConvergedContext(rootPath, previous, currentContext) with
			{
				CaptureTreeInventory = context.CaptureTreeInventory
			};
			var next = services.Engine.ComputeFullRefreshSnapshot(
				currentContext,
				TestContext.Current.CancellationToken);
			if (SnapshotsMatch(previous, next))
				return next;

			previous = next;
		}

		currentContext = BuildConvergedContext(rootPath, previous, currentContext) with
		{
			CaptureTreeInventory = context.CaptureTreeInventory
		};
		var final = services.Engine.ComputeFullRefreshSnapshot(currentContext, TestContext.Current.CancellationToken);
		AssertEquivalentVisibleSnapshots(previous, final);
		return final;
	}

	private static bool SnapshotsMatch(SelectionRefreshSnapshot expected, SelectionRefreshSnapshot actual)
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

	private static SelectionRefreshContext BuildScenarioContext(
		string rootPath,
		SelectionRefreshSnapshot baseline,
		CrossLayerScenario scenario)
	{
		var context = CreateContextFromSnapshot(rootPath, baseline) with
		{
			CaptureTreeInventory = scenario.CaptureTreeInventory
		};

		if (scenario.Roots is not null)
			context = ApplyRootSelection(context, baseline, scenario.Roots);
		if (scenario.Extensions is not null)
			context = ApplyExtensionSelection(context, baseline, scenario.Extensions);
		if (scenario.AllIgnoreOff)
			context = ApplyAllIgnoreOptionsOff(context);
		if (scenario.SingleIgnoreOn is not null)
			context = ApplySingleIgnoreOptionOn(context, scenario.SingleIgnoreOn.Value);
		if (scenario.ForcedIgnoreStates is not null)
			context = ApplyForcedIgnoreStates(context, scenario.ForcedIgnoreStates);

		return context;
	}

	private static SelectionRefreshContext ApplyRootSelection(
		SelectionRefreshContext context,
		SelectionRefreshSnapshot snapshot,
		IReadOnlySet<string> selectedRoots)
	{
		var rootStates = snapshot.RootOptions?.ToDictionary(
			option => option.Name,
			option => selectedRoots.Contains(option.Name),
			PathComparer.Default) ?? new Dictionary<string, bool>(PathComparer.Default);

		return context with
		{
			AllRootFoldersChecked = false,
			RootSelectionInitialized = true,
			RootSelectionCache = new HashSet<string>(selectedRoots, PathComparer.Default),
			RootOptionStateCache = rootStates
		};
	}

	private static SelectionRefreshContext ApplyExtensionSelection(
		SelectionRefreshContext context,
		SelectionRefreshSnapshot snapshot,
		IReadOnlySet<string> selectedExtensions)
	{
		var extensionStates = snapshot.ExtensionOptions.ToDictionary(
			option => option.Name,
			option => selectedExtensions.Contains(option.Name),
			StringComparer.OrdinalIgnoreCase);

		return context with
		{
			AllExtensionsChecked = false,
			ExtensionsSelectionInitialized = true,
			ExtensionsSelectionCache = new HashSet<string>(selectedExtensions, StringComparer.OrdinalIgnoreCase),
			ExtensionOptionStateCache = extensionStates
		};
	}

	private static SelectionRefreshContext ApplyAllIgnoreOptionsOff(SelectionRefreshContext context)
	{
		var stateCache = new Dictionary<IgnoreOptionId, bool>(context.IgnoreOptionStateCache);
		foreach (var optionId in Enum.GetValues<IgnoreOptionId>())
			stateCache[optionId] = false;

		return context with
		{
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = new HashSet<IgnoreOptionId>(),
			IgnoreOptionStateCache = stateCache,
			IgnoreAllPreference = false,
			IgnoreOptionStateCacheIsComplete = true
		};
	}

	private static SelectionRefreshContext ApplySingleIgnoreOptionOn(
		SelectionRefreshContext context,
		IgnoreOptionId optionId)
	{
		var stateCache = new Dictionary<IgnoreOptionId, bool>(context.IgnoreOptionStateCache);
		foreach (var existingOptionId in Enum.GetValues<IgnoreOptionId>())
			stateCache[existingOptionId] = existingOptionId == optionId;

		return context with
		{
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = new HashSet<IgnoreOptionId> { optionId },
			IgnoreOptionStateCache = stateCache,
			IgnoreAllPreference = null,
			IgnoreOptionStateCacheIsComplete = true
		};
	}

	private static SelectionRefreshContext ApplyForcedIgnoreStates(
		SelectionRefreshContext context,
		IReadOnlyDictionary<IgnoreOptionId, bool> forcedStates)
	{
		var stateCache = new Dictionary<IgnoreOptionId, bool>(context.IgnoreOptionStateCache);
		var selected = new HashSet<IgnoreOptionId>(context.IgnoreSelectionCache);
		foreach (var (optionId, isChecked) in forcedStates)
		{
			stateCache[optionId] = isChecked;
			if (isChecked)
				selected.Add(optionId);
			else
				selected.Remove(optionId);
		}

		return context with
		{
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = selected,
			IgnoreOptionStateCache = stateCache,
			IgnoreAllPreference = null,
			IgnoreOptionStateCacheIsComplete = true
		};
	}

	private static void AssertDirectScannerParity(
		WorkflowServices services,
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		CrossLayerScenario scenario)
	{
		var selectedRoots = CollectCheckedRootNames(snapshot);
		var selectedIgnoreOptions = CollectActiveIgnoreOptionIds(snapshot);
		var selectedExtensions = CollectCheckedExtensionNames(snapshot);
		var stableContext = CreateContextFromSnapshot(rootPath, snapshot) with
		{
			CaptureTreeInventory = scenario.CaptureTreeInventory
		};
		var rules = services.IgnoreRulesService.Build(rootPath, selectedIgnoreOptions, selectedRoots);
		var includeDirectoryToggleProbeRoots = ShouldIncludeDirectoryToggleProbeRoots(
			stableContext,
			selectedRoots,
			selectedIgnoreOptions);
		var includeControllerImpactProbeRoots = ShouldIncludeControllerImpactProbeRoots(
			stableContext,
			selectedIgnoreOptions);
		var directScan = new ScanOptionsUseCase(new FileSystemScanner()).GetIgnoreSectionSnapshotForRootFolders(
			rootPath,
			selectedRoots,
			BuildExtensionDiscoveryRules(rules),
			rules,
			ResolveEffectiveExtensionPolicy(stableContext, selectedExtensions),
			includeDirectoryToggleProbeRoots,
			TestContext.Current.CancellationToken,
			includeControllerImpactProbeRoots);

		var directCounts = directScan.Value.EffectiveIgnoreOptionCounts;
		var diagnostic =
			$"{scenario.Name}: direct scanner counts drifted from SelectionRefreshEngine. " +
			$"Published={snapshot.IgnoreOptionCounts}; Direct={directCounts}; " +
			$"Roots=[{string.Join(", ", selectedRoots)}]; Extensions=[{string.Join(", ", selectedExtensions)}]; " +
			$"Ignore=[{string.Join(", ", selectedIgnoreOptions)}]; " +
			$"DirectoryProbe={includeDirectoryToggleProbeRoots}; ControllerProbe={includeControllerImpactProbeRoots}; " +
			$"AllRoots={stableContext.AllRootFoldersChecked}; AllExtensions={stableContext.AllExtensionsChecked};";
		if (scenario.Extensions is null)
		{
			Assert.True(snapshot.IgnoreOptionCounts == directCounts, diagnostic);
		}
		// Explicit extension journeys intentionally preserve proven option evidence across
		// convergence passes. Their published aggregate is therefore not equivalent to a
		// single final-scope scan; labels, tree projection, and repeated refresh are asserted
		// independently by the remaining cross-layer checks in this test.
		if (scenario.Roots is null && scenario.Extensions is null)
		{
			Assert.Equal(
				snapshot.ControllerImpactCounts,
				directScan.Value.ControllerImpactCounts);
		}
		Assert.Equal(snapshot.RootAccessDenied, directScan.RootAccessDenied);
		Assert.Equal(snapshot.HadAccessDenied, directScan.HadAccessDenied);
	}

	private static IgnoreRules BuildExtensionDiscoveryRules(IgnoreRules rules)
	{
		// Extension discovery intentionally sees through file-level ignore options. This
		// mirrors SelectionRefreshEngine and catches drift between UI labels and scanner
		// counts when DotFiles/EmptyFiles/ExtensionlessFiles are active.
		return rules with
		{
			IgnoreHiddenFiles = false,
			IgnoreDotFiles = false,
			IgnoreEmptyFiles = false,
			IgnoreExtensionlessFiles = false
		};
	}

	private static HashSet<IgnoreOptionId> CollectActiveIgnoreOptionIds(SelectionRefreshSnapshot snapshot)
	{
		var selected = CollectCheckedIgnoreOptionIds(snapshot);
		foreach (var (optionId, isChecked) in snapshot.IgnoreOptionStateCache)
		{
			// Full refresh uses the persisted state cache as the active rule source so
			// self-hidden options keep affecting the tree until the user turns them off.
			if (isChecked)
				selected.Add(optionId);
		}

		return selected;
	}

	private static IExtensionInclusionPolicy? ResolveEffectiveExtensionPolicy(
		SelectionRefreshContext context,
		IReadOnlySet<string> selectedExtensions)
	{
		if (context.AllExtensionsChecked)
			return null;

		if (!context.ExtensionsSelectionInitialized)
			return null;

		if (context.ExtensionOptionStateCache is null)
			return new ExtensionSetInclusionPolicy(selectedExtensions);

		// SelectionRefreshEngine uses state-cache semantics for live extension filters:
		// known unchecked extensions stay unchecked, while newly discovered extensions
		// under an expanded ignore branch use the product default for new entries.
		return new ExtensionSelectionInclusionPolicy(
			new SelectionStateResolver(
				context.ExtensionsSelectionCache,
				context.ExtensionOptionStateCache),
			defaultForNewExtension: true);
	}

	private static bool ShouldIncludeControllerImpactProbeRoots(
		SelectionRefreshContext context,
		IReadOnlySet<IgnoreOptionId> selectedIgnoreOptions)
	{
		var hasActiveController =
			selectedIgnoreOptions.Contains(IgnoreOptionId.UseGitIgnore) ||
			selectedIgnoreOptions.Contains(IgnoreOptionId.SmartIgnore);

		if (hasActiveController)
			return true;

		return context.AllRootFoldersChecked ||
		       !ShouldSuppressAllTogglesOverride(context) ||
		       HasCompleteSelectionStateForNewRootLevelToggles(context);
	}

	private static bool ShouldIncludeDirectoryToggleProbeRoots(
		SelectionRefreshContext context,
		IReadOnlyCollection<string> selectedRoots,
		IReadOnlySet<IgnoreOptionId> selectedIgnoreOptions)
	{
		var hasDirectoryToggle =
			selectedIgnoreOptions.Contains(IgnoreOptionId.DotFolders) ||
			selectedIgnoreOptions.Contains(IgnoreOptionId.HiddenFolders);
		var canDiscoverNewRootLevelToggle = CanDiscoverNewRootLevelDirectoryToggle(context);

		if (!hasDirectoryToggle)
		{
			return canDiscoverNewRootLevelToggle ||
			       ContainsDotDirectoryName(context.RootSelectionCache) ||
			       ContainsDotDirectoryName(selectedRoots);
		}

		if (canDiscoverNewRootLevelToggle || !ShouldSuppressAllTogglesOverride(context))
			return true;

		return ContainsDotDirectoryName(context.RootSelectionCache) ||
		       ContainsDotDirectoryName(selectedRoots) ||
		       selectedRoots.Count < context.RootSelectionCache.Count;
	}

	private static bool HasCompleteSelectionStateForNewRootLevelToggles(SelectionRefreshContext context) =>
		context.PreparedSelectionMode != PreparedSelectionMode.Profile ||
		context.RootOptionStateCache is not null ||
		context.IgnoreOptionStateCacheIsComplete;

	private static bool CanDiscoverNewRootLevelDirectoryToggle(SelectionRefreshContext context)
	{
		if (context.AllRootFoldersChecked)
			return HasCompleteSelectionStateForNewRootLevelToggles(context);

		if (!context.RootSelectionInitialized)
			return false;

		return context.RootOptionStateCache is not null &&
		       HasCompleteSelectionStateForNewRootLevelToggles(context);
	}

	private static bool ShouldSuppressAllTogglesOverride(SelectionRefreshContext context) =>
		context.PreparedSelectionMode == PreparedSelectionMode.Profile;

	private static bool ContainsDotDirectoryName(IEnumerable<string> names)
	{
		foreach (var name in names)
		{
			if (IgnoreRuleSemantics.IsDotName(name))
				return true;
		}

		return false;
	}

	private static void AssertIgnoreLabelsMatchPublishedCounts(
		SelectionRefreshSnapshot snapshot,
		CrossLayerScenario scenario)
	{
		foreach (var option in snapshot.IgnoreOptions)
		{
			if (option.Id is IgnoreOptionId.UseGitIgnore or IgnoreOptionId.SmartIgnore ||
                ProjectPresentationCatalog.ContentTransformationOptionIds.Contains(option.Id))
			{
				Assert.DoesNotMatch(@"\(\d+\)$", option.Label);
				continue;
			}

			var count = GetIgnoreCount(snapshot.IgnoreOptionCounts, option.Id);
			Assert.True(count > 0, $"{scenario.Name}: visible basic option {option.Id} must have positive impact.");
			var match = Regex.Match(option.Label, @"\((\d+)\)$");
			Assert.True(match.Success, $"{scenario.Name}: option {option.Id} label must publish its live count. Label: {option.Label}");
			Assert.Equal(count, int.Parse(match.Groups[1].Value));
		}
	}

	private static void AssertRepeatedRefreshIsStable(
		WorkflowServices services,
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		SelectionRefreshContext previousContext,
		CrossLayerScenario scenario)
	{
		var repeatedContext = BuildConvergedContext(rootPath, snapshot, previousContext) with
		{
			// The UI stores aggregate checkbox preferences and hidden option states
			// independently. Re-inferring either from the filtered public list changes a
			// partial selection into "All" and loses explicit states for hidden extensions.
			AllRootFoldersChecked = previousContext.AllRootFoldersChecked,
			AllExtensionsChecked = previousContext.AllExtensionsChecked,
			CaptureTreeInventory = scenario.CaptureTreeInventory
		};
		var repeated = ComputeConvergedSnapshot(
			services,
			rootPath,
			repeatedContext);

		AssertEquivalentVisibleSnapshots(snapshot, repeated);
	}

	private static void AssertEquivalentDynamicSections(
		SelectionRefreshSnapshot expected,
		SelectionRefreshSnapshot actual,
		CrossLayerScenario scenario)
	{
		// Live refresh receives the current root selection from the UI and only rebuilds
		// extension and ignore sections. RootOptions are intentionally outside this check.
		Assert.Equal(expected.ExtensionOptions, actual.ExtensionOptions);
		Assert.Equal(expected.IgnoreOptions, actual.IgnoreOptions);
		Assert.Equal(expected.ExtensionlessEntriesCount, actual.ExtensionlessEntriesCount);
		Assert.Equal(expected.HasIgnoreOptionCounts, actual.HasIgnoreOptionCounts);
		Assert.Equal(expected.IgnoreOptionCounts, actual.IgnoreOptionCounts);
		Assert.Equal(expected.ControllerImpactCounts, actual.ControllerImpactCounts);
		Assert.Equal(expected.RootAccessDenied, actual.RootAccessDenied);
		Assert.Equal(expected.HadAccessDenied, actual.HadAccessDenied);
		Assert.Equal(expected.IgnoreOptionStateCache.Count, actual.IgnoreOptionStateCache.Count);
		foreach (var (optionId, expectedState) in expected.IgnoreOptionStateCache)
		{
			Assert.True(
				actual.IgnoreOptionStateCache.TryGetValue(optionId, out var actualState),
				$"{scenario.Name}: live refresh dropped cached ignore state {optionId}.");
			Assert.Equal(expectedState, actualState);
		}
	}

	private static void AssertTreeInventoryProjectionMatchesDirectTree(
		string rootPath,
		WorkflowServices services,
		SelectionRefreshSnapshot snapshot,
		CrossLayerScenario scenario)
	{
		if (!scenario.CaptureTreeInventory)
			return;

		var inventory = Assert.IsType<ProjectTreeInventorySnapshot>(snapshot.TreeInventory);
		var selectedRoots = CollectCheckedRootNames(snapshot);
		var selectedExtensions = CollectCheckedExtensionNames(snapshot);
		var rules = services.IgnoreRulesService.Build(rootPath, CollectCheckedIgnoreOptionIds(snapshot), selectedRoots);
		var options = new TreeFilterOptions(selectedExtensions, selectedRoots, rules);
		var builder = new TreeBuilder();

		var direct = builder.Build(rootPath, options, TestContext.Current.CancellationToken);
		var projected = builder.Build(inventory, options, TestContext.Current.CancellationToken);

		Assert.Equal(FlattenTree(direct.Root), FlattenTree(projected.Root));
	}

	private static int GetIgnoreCount(IgnoreOptionCounts counts, IgnoreOptionId optionId)
	{
		return optionId switch
		{
			IgnoreOptionId.HiddenFolders => counts.HiddenFolders,
			IgnoreOptionId.HiddenFiles => counts.HiddenFiles,
			IgnoreOptionId.DotFolders => counts.DotFolders,
			IgnoreOptionId.DotFiles => counts.DotFiles,
			IgnoreOptionId.EmptyFolders => counts.EmptyFolders,
			IgnoreOptionId.EmptyFiles => counts.EmptyFiles,
			IgnoreOptionId.ExtensionlessFiles => counts.ExtensionlessFiles,
			_ => throw new ArgumentOutOfRangeException(nameof(optionId), optionId, null)
		};
	}

	private static List<string> FlattenTree(FileSystemNode root)
	{
		var result = new List<string>();
		var pending = new Stack<FileSystemNode>();
		pending.Push(root);
		while (pending.Count > 0)
		{
			var node = pending.Pop();
			result.Add($"{node.FullPath}|{node.IsDirectory}|{node.IsAccessDenied}");
			for (var index = node.Children.Count - 1; index >= 0; index--)
				pending.Push(node.Children[index]);
		}

		return result;
	}

	private static void MarkHidden(string path)
	{
		var attributes = File.GetAttributes(path);
		File.SetAttributes(path, attributes | FileAttributes.Hidden);
	}

	public sealed record CrossLayerScenario(
		string Name,
		IReadOnlySet<string>? Roots = null,
		IReadOnlySet<string>? Extensions = null,
		bool AllIgnoreOff = false,
		IgnoreOptionId? SingleIgnoreOn = null,
		IReadOnlyDictionary<IgnoreOptionId, bool>? ForcedIgnoreStates = null,
		bool CaptureTreeInventory = false)
	{
		public static CrossLayerScenario Defaults(string name, bool captureTreeInventory = false) =>
			new(name, CaptureTreeInventory: captureTreeInventory);

		public static CrossLayerScenario CreateAllIgnoreOff(string name, bool captureTreeInventory = false) =>
			new(name, AllIgnoreOff: true, CaptureTreeInventory: captureTreeInventory);

		public static CrossLayerScenario CreateSingleIgnoreOn(string name, IgnoreOptionId optionId) =>
			new(name, SingleIgnoreOn: optionId);

		public static CrossLayerScenario ForcedIgnoreState(string name, IgnoreOptionId optionId, bool isChecked) =>
			new(name, ForcedIgnoreStates: new Dictionary<IgnoreOptionId, bool> { [optionId] = isChecked });

		public static CrossLayerScenario CreateRoots(string name, string[] roots, bool captureTreeInventory = false) =>
			new(
				name,
				Roots: new HashSet<string>(roots, PathComparer.Default),
				CaptureTreeInventory: captureTreeInventory);

		public static CrossLayerScenario CreateExtensions(string name, string[] extensions, bool captureTreeInventory = false) =>
			new(
				name,
				Extensions: new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase),
				CaptureTreeInventory: captureTreeInventory);

		public static CrossLayerScenario Mixed(
			string name,
			string[]? roots,
			string[]? extensions,
			bool allIgnoreOff = false,
			IReadOnlyDictionary<IgnoreOptionId, bool>? forcedIgnoreStates = null,
			bool captureTreeInventory = false) =>
			new(
				name,
				Roots: roots is null ? null : new HashSet<string>(roots, PathComparer.Default),
				Extensions: extensions is null ? null : new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase),
				AllIgnoreOff: allIgnoreOff,
				ForcedIgnoreStates: forcedIgnoreStates,
				CaptureTreeInventory: captureTreeInventory);

		public override string ToString() => Name;
	}
}
