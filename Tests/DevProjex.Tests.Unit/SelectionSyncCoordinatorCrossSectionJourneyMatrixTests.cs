using DevProjex.Application.Models;
using DevProjex.Infrastructure.FileSystem;
using DevProjex.Tests.Shared.ProjectLoadWorkflow;
using static DevProjex.Tests.Shared.ProjectLoadWorkflow.ProjectLoadWorkflowRefreshHarness;

namespace DevProjex.Tests.Unit;

[Collection("AvaloniaUI")]
public sealed class SelectionSyncCoordinatorCrossSectionJourneyMatrixTests
{
	[AvaloniaTheory]
	[MemberData(nameof(Journeys))]
	public async Task PublicSettingsEvents_LongCrossSectionJourney_MatchesIndependentWorkflowOracleAtEveryStep(
		string journeyName)
	{
		using var workspace = SettingsIslandWorkspace.Create();
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, workspace.RootPath);

		await coordinator.RefreshRootAndDependentsAsync(
			workspace.RootPath,
			TestContext.Current.CancellationToken);
		HookAllOptionListeners(coordinator, viewModel);

		var oracle = SettingsIslandOracle.Create(workspace.RootPath);
		AssertIslandMatchesOracle(viewModel, coordinator, oracle.CurrentSnapshot, $"{journeyName}: baseline");
		await AssertTreeAndMetricsMatchOracleAsync(
			workspace.RootPath,
			viewModel,
			coordinator,
			oracle.CurrentSnapshot,
			$"{journeyName}: baseline");

		var checkpointCount = 0;
		var actions = GetJourney(journeyName);
		for (var stepIndex = 0; stepIndex < actions.Count; stepIndex++)
		{
			var action = actions[stepIndex];
			var stepName = $"{journeyName}: step {stepIndex + 1}/{actions.Count} ({action})";

			if (action.Kind == SettingsActionKind.Checkpoint)
			{
				await AssertTreeAndMetricsMatchOracleAsync(
					workspace.RootPath,
					viewModel,
					coordinator,
					oracle.CurrentSnapshot,
					stepName);
				AssertIslandMatchesOracle(viewModel, coordinator, oracle.CurrentSnapshot, stepName);
				checkpointCount++;
				continue;
			}

			ExecuteSettingsAction(viewModel, coordinator, workspace.RootPath, action);
			oracle.Apply(action);
			await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);
			var expectedSnapshot = oracle.Recompute();

			AssertIslandMatchesOracle(viewModel, coordinator, expectedSnapshot, stepName);
			await AssertIslandRemainsStableAsync(viewModel, coordinator, stepName);
		}

		Assert.True(
			checkpointCount >= 2,
			$"Journey '{journeyName}' must verify more than one complete tree/metrics checkpoint.");
	}

	public static IEnumerable<object[]> Journeys()
	{
		foreach (var journeyName in JourneyNames)
			yield return [journeyName];
	}

	private static readonly string[] JourneyNames =
	[
		"ignore-expansion-with-hidden-root-state",
		"master-checkbox-wave-with-explicit-preferences",
		"dynamic-empty-root-disappear-reappear",
		"file-filter-and-dot-root-cross-section-round-trip"
	];

	private static IReadOnlyList<SettingsAction> GetJourney(string journeyName)
	{
		return journeyName switch
		{
			"ignore-expansion-with-hidden-root-state" =>
			[
				SettingsAction.ToggleIgnore(IgnoreOptionId.EmptyFiles),
				SettingsAction.ToggleExtension(".txt"),
				SettingsAction.ToggleRoot("gamma"),
				SettingsAction.ToggleIgnore(IgnoreOptionId.UseGitIgnore),
				SettingsAction.ToggleRoot("artifacts"),
				SettingsAction.ToggleExtension(".log"),
				SettingsAction.Checkpoint(),
				SettingsAction.ToggleRoot("gamma"),
				SettingsAction.ToggleExtension(".txt"),
				SettingsAction.ToggleIgnore(IgnoreOptionId.EmptyFiles),
				SettingsAction.ToggleExtension(".log"),
				SettingsAction.ToggleIgnore(IgnoreOptionId.UseGitIgnore),
				SettingsAction.Checkpoint(),
				SettingsAction.ToggleIgnore(IgnoreOptionId.UseGitIgnore),
				SettingsAction.Checkpoint(),
				SettingsAction.ToggleIgnore(IgnoreOptionId.UseGitIgnore),
				SettingsAction.Checkpoint()
			],
			"master-checkbox-wave-with-explicit-preferences" =>
			[
				SettingsAction.SetAllIgnore(false),
				SettingsAction.SetAllRoots(false),
				SettingsAction.ToggleRoot("alpha"),
				SettingsAction.ToggleRoot("gamma"),
				SettingsAction.SetAllExtensions(false),
				SettingsAction.ToggleExtension(".cs"),
				SettingsAction.ToggleExtension(".md"),
				SettingsAction.ToggleExtension(".txt"),
				SettingsAction.ToggleIgnore(IgnoreOptionId.SmartIgnore),
				SettingsAction.ToggleIgnore(IgnoreOptionId.UseGitIgnore),
				SettingsAction.ToggleIgnore(IgnoreOptionId.DotFolders),
				SettingsAction.ToggleIgnore(IgnoreOptionId.EmptyFolders),
				SettingsAction.Checkpoint(),
				SettingsAction.SetAllRoots(true),
				SettingsAction.SetAllExtensions(true),
				SettingsAction.SetAllIgnore(true),
				SettingsAction.Checkpoint(),
				SettingsAction.SetAllIgnore(false),
				SettingsAction.Checkpoint(),
				SettingsAction.SetAllIgnore(true),
				SettingsAction.Checkpoint()
			],
			"dynamic-empty-root-disappear-reappear" =>
			[
				SettingsAction.ToggleIgnore(IgnoreOptionId.EmptyFolders),
				SettingsAction.ToggleRoot("delta-empty"),
				SettingsAction.ToggleRoot("beta"),
				SettingsAction.ToggleIgnore(IgnoreOptionId.EmptyFolders),
				SettingsAction.ToggleRoot("alpha"),
				SettingsAction.ToggleRoot("alpha"),
				SettingsAction.ToggleExtension(".cs"),
				SettingsAction.ToggleIgnore(IgnoreOptionId.EmptyFiles),
				SettingsAction.Checkpoint(),
				SettingsAction.ToggleIgnore(IgnoreOptionId.EmptyFolders),
				SettingsAction.ToggleRoot("delta-empty"),
				SettingsAction.ToggleRoot("beta"),
				SettingsAction.ToggleExtension(".cs"),
				SettingsAction.ToggleIgnore(IgnoreOptionId.EmptyFiles),
				SettingsAction.ToggleIgnore(IgnoreOptionId.EmptyFolders),
				SettingsAction.Checkpoint()
			],
			"file-filter-and-dot-root-cross-section-round-trip" =>
			[
				SettingsAction.ToggleExtension(".md"),
				SettingsAction.ToggleExtension(".txt"),
				SettingsAction.ToggleIgnore(IgnoreOptionId.ExtensionlessFiles),
				SettingsAction.ToggleIgnore(IgnoreOptionId.DotFiles),
				SettingsAction.ToggleIgnore(IgnoreOptionId.EmptyFiles),
				SettingsAction.Checkpoint(),
				SettingsAction.ToggleRoot("gamma"),
				SettingsAction.ToggleIgnore(IgnoreOptionId.DotFolders),
				SettingsAction.Checkpoint(),
				SettingsAction.ToggleExtension(".txt"),
				SettingsAction.ToggleIgnore(IgnoreOptionId.DotFolders),
				SettingsAction.ToggleIgnore(IgnoreOptionId.DotFolders),
				SettingsAction.ToggleRoot(".root-dot"),
				SettingsAction.Checkpoint(),
				SettingsAction.ToggleIgnore(IgnoreOptionId.DotFolders),
				SettingsAction.ToggleRoot("gamma"),
				SettingsAction.ToggleExtension(".md"),
				SettingsAction.ToggleIgnore(IgnoreOptionId.ExtensionlessFiles),
				SettingsAction.ToggleIgnore(IgnoreOptionId.DotFiles),
				SettingsAction.ToggleIgnore(IgnoreOptionId.EmptyFiles),
				SettingsAction.Checkpoint(),
				SettingsAction.ToggleIgnore(IgnoreOptionId.DotFolders),
				SettingsAction.Checkpoint(),
				SettingsAction.ToggleIgnore(IgnoreOptionId.DotFolders),
				SettingsAction.Checkpoint()
			],
			_ => throw new ArgumentOutOfRangeException(nameof(journeyName), journeyName, null)
		};
	}

	private static void ExecuteSettingsAction(
		MainWindowViewModel viewModel,
		SelectionSyncCoordinator coordinator,
		string rootPath,
		SettingsAction action)
	{
		switch (action.Kind)
		{
			case SettingsActionKind.ToggleRoot:
				var rootOption = Assert.Single(viewModel.RootFolders, option =>
					string.Equals(option.Name, action.Name, StringComparison.Ordinal));
				rootOption.IsChecked = !rootOption.IsChecked;
				break;
			case SettingsActionKind.ToggleExtension:
				var extensionOption = Assert.Single(viewModel.Extensions, option =>
					string.Equals(option.Name, action.Name, StringComparison.OrdinalIgnoreCase));
				extensionOption.IsChecked = !extensionOption.IsChecked;
				break;
			case SettingsActionKind.ToggleIgnore:
				var ignoreOption = Assert.Single(viewModel.IgnoreOptions, option =>
					option.Id == action.IgnoreOptionId);
				ignoreOption.IsChecked = !ignoreOption.IsChecked;
				break;
			case SettingsActionKind.SetAllRoots:
				Assert.NotEqual(action.IsChecked!.Value, viewModel.AllRootFoldersChecked);
				coordinator.HandleRootAllChanged(action.IsChecked.Value, rootPath);
				break;
			case SettingsActionKind.SetAllExtensions:
				Assert.NotEqual(action.IsChecked!.Value, viewModel.AllExtensionsChecked);
				coordinator.HandleExtensionsAllChanged(action.IsChecked.Value);
				break;
			case SettingsActionKind.SetAllIgnore:
				Assert.NotEqual(action.IsChecked!.Value, viewModel.AllIgnoreChecked);
				coordinator.HandleIgnoreAllChanged(action.IsChecked.Value, rootPath);
				break;
			case SettingsActionKind.Checkpoint:
				throw new InvalidOperationException("Checkpoints are handled by the journey runner.");
			default:
				throw new ArgumentOutOfRangeException(nameof(action), action, null);
		}
	}

	private static void AssertIslandMatchesOracle(
		MainWindowViewModel viewModel,
		SelectionSyncCoordinator coordinator,
		SelectionRefreshSnapshot expected,
		string stepName)
	{
		var expectedRoots = expected.RootOptions?
			.Select(static option => (option.Name, option.IsChecked))
			.ToArray() ?? [];
		var actualRoots = viewModel.RootFolders
			.Select(static option => (option.Name, option.IsChecked))
			.ToArray();
		AssertSequenceEqual(expectedRoots, actualRoots, $"{stepName}: root section");

		var expectedExtensions = expected.EffectiveExtensionOptions
			.Select(static option => (option.Name, option.IsChecked))
			.ToArray();
		var actualExtensions = viewModel.Extensions
			.Select(static option => (option.Name, option.IsChecked))
			.ToArray();
		AssertSequenceEqual(expectedExtensions, actualExtensions, $"{stepName}: extension section");

		var expectedIgnore = expected.IgnoreOptions
			.Select(static option => (option.Id, option.Label, option.IsChecked))
			.ToArray();
		var actualIgnore = viewModel.IgnoreOptions
			.Select(static option => (option.Id, option.Label, option.IsChecked))
			.ToArray();
		AssertSequenceEqual(expectedIgnore, actualIgnore, $"{stepName}: ignore section");

		Assert.Equal(expectedRoots.Length == 0 || expectedRoots.All(static option => option.IsChecked),
			viewModel.AllRootFoldersChecked);
		Assert.Equal(expectedExtensions.Length > 0 && expectedExtensions.All(static option => option.IsChecked),
			viewModel.AllExtensionsChecked);
		Assert.Equal(expectedIgnore.Length > 0 && expectedIgnore.All(static option => option.IsChecked),
			viewModel.AllIgnoreChecked);

		Assert.Equal(expectedRoots.Length,
			expectedRoots.Select(static option => option.Name).Distinct(PathComparer.Default).Count());
		Assert.Equal(expectedExtensions.Length,
			expectedExtensions.Select(static option => option.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
		Assert.Equal(expectedIgnore.Length, expectedIgnore.Select(static option => option.Id).Distinct().Count());

		var selectedIgnoreIds = coordinator.GetSelectedIgnoreOptionIds()
			.OrderBy(static optionId => optionId)
			.ToArray();
		var expectedSelectedIgnoreIds = expectedIgnore
			.Where(static option => option.IsChecked)
			.Select(static option => option.Id)
			.OrderBy(static optionId => optionId)
			.ToArray();
		AssertSequenceEqual(expectedSelectedIgnoreIds, selectedIgnoreIds, $"{stepName}: runtime ignore selection");

		AssertVisibleAdvancedIgnoreOptionsCarryExactCounts(expected);
	}

	private static async Task AssertIslandRemainsStableAsync(
		MainWindowViewModel viewModel,
		SelectionSyncCoordinator coordinator,
		string stepName)
	{
		var before = CaptureIslandFingerprint(viewModel);
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);
		await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(static () => { });
		var after = CaptureIslandFingerprint(viewModel);
		Assert.True(
			string.Equals(before, after, StringComparison.Ordinal),
			$"{stepName}: island changed after the public refresh queue reported idle.");
	}

	private static async Task AssertTreeAndMetricsMatchOracleAsync(
		string rootPath,
		MainWindowViewModel viewModel,
		SelectionSyncCoordinator coordinator,
		SelectionRefreshSnapshot expected,
		string stepName)
	{
		var actualSelectedRoots = viewModel.RootFolders
			.Where(static option => option.IsChecked)
			.Select(static option => option.Name)
			.ToHashSet(PathComparer.Default);
		var actualSelectedExtensions = viewModel.Extensions
			.Where(static option => option.IsChecked)
			.Select(static option => option.Name)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var actualSelectedIgnoreOptions = coordinator.GetSelectedIgnoreOptionIds();

		var actualMetrics = await ProjectLoadWorkflowRuntime.ComputeMetricsAsync(
			rootPath,
			actualSelectedRoots,
			actualSelectedExtensions,
			actualSelectedIgnoreOptions,
			TestContext.Current.CancellationToken);
		var expectedMetrics = await ComputeMetricsFromSnapshotAsync(rootPath, expected);

		Assert.True(
			actualMetrics == expectedMetrics,
			$"{stepName}: tree/content metrics drifted. Expected={expectedMetrics}; Actual={actualMetrics}.");

		AssertTreeFilterContracts(
			rootPath,
			actualSelectedRoots,
			actualSelectedExtensions,
			actualSelectedIgnoreOptions,
			viewModel.RootFolders);
	}

	private static void AssertTreeFilterContracts(
		string rootPath,
		IReadOnlySet<string> selectedRoots,
		IReadOnlySet<string> selectedExtensions,
		IReadOnlyCollection<IgnoreOptionId> selectedIgnoreOptions,
		IEnumerable<SelectionOptionViewModel> rootOptions)
	{
		var ignoreRulesService = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();
		var ignoreRules = ignoreRulesService.Build(rootPath, selectedIgnoreOptions, selectedRoots);
		var buildResult = ProjectLoadWorkflowRuntime.CreateBuildTreeUseCase().Execute(
			new BuildTreeRequest(
				rootPath,
				new TreeFilterOptions(selectedExtensions, selectedRoots, ignoreRules)),
			CancellationToken.None);
		var relativePaths = FlattenRelativePaths(rootPath, buildResult.Root);

		foreach (var uncheckedRoot in rootOptions.Where(static option => !option.IsChecked))
		{
			Assert.DoesNotContain(relativePaths, relativePath =>
				relativePath.Equals(uncheckedRoot.Name, PathComparison) ||
				relativePath.StartsWith(uncheckedRoot.Name + "/", PathComparison));
		}

		if (ignoreRules.IsGitIgnored(
			    Path.Combine(rootPath, "artifacts"),
			    isDirectory: true,
			    "artifacts"))
		{
			Assert.DoesNotContain(relativePaths, static path => path.StartsWith("artifacts/", PathComparison));
		}

		if (ignoreRules.IsGitIgnored(
			    Path.Combine(rootPath, "alpha", "runtime.log"),
			    isDirectory: false,
			    "runtime.log"))
			Assert.DoesNotContain("alpha/runtime.log", relativePaths);

		if (ignoreRules.IsSmartIgnoredDirectory(
			    Path.Combine(rootPath, "alpha", "bin"),
			    "bin"))
			Assert.DoesNotContain(relativePaths, static path => path.StartsWith("alpha/bin/", PathComparison));

		if (ignoreRules.IsSmartIgnoredDirectory(
			    Path.Combine(rootPath, "beta", "node_modules"),
			    "node_modules"))
		{
			Assert.DoesNotContain(relativePaths, static path => path.StartsWith("beta/node_modules/", PathComparison));
		}

		if (selectedIgnoreOptions.Contains(IgnoreOptionId.DotFolders))
		{
			Assert.DoesNotContain(relativePaths, static path =>
				path.StartsWith(".root-dot/", PathComparison) ||
				path.StartsWith("alpha/.private/", PathComparison) ||
				path.StartsWith("beta/.cache/", PathComparison) ||
				path.StartsWith("gamma/.drafts/", PathComparison));
		}

		if (selectedIgnoreOptions.Contains(IgnoreOptionId.EmptyFiles))
		{
			Assert.DoesNotContain("alpha/src/empty.cs", relativePaths);
			Assert.DoesNotContain("gamma/empty.txt", relativePaths);
			Assert.DoesNotContain("empty-root-file.txt", relativePaths);
		}

		if (selectedIgnoreOptions.Contains(IgnoreOptionId.ExtensionlessFiles))
		{
			Assert.DoesNotContain("LICENSE", relativePaths);
			Assert.DoesNotContain("gamma/README", relativePaths);
		}
	}

	private static HashSet<string> FlattenRelativePaths(string rootPath, TreeNodeDescriptor root)
	{
		var paths = new HashSet<string>(StringComparer.Ordinal);
		var stack = new Stack<TreeNodeDescriptor>(root.Children.Reverse());
		while (stack.Count > 0)
		{
			var node = stack.Pop();
			paths.Add(Path.GetRelativePath(rootPath, node.FullPath).Replace(Path.DirectorySeparatorChar, '/'));
			for (var index = node.Children.Count - 1; index >= 0; index--)
				stack.Push(node.Children[index]);
		}

		return paths;
	}

	private static void AssertVisibleAdvancedIgnoreOptionsCarryExactCounts(SelectionRefreshSnapshot snapshot)
	{
		foreach (var option in snapshot.IgnoreOptions)
		{
			if (option.Id is IgnoreOptionId.SmartIgnore or IgnoreOptionId.UseGitIgnore)
				continue;

			var expectedCount = GetIgnoreOptionCount(snapshot.IgnoreOptionCounts, option.Id);
			Assert.True(expectedCount > 0, $"Visible ignore option '{option.Id}' must have a positive count.");
			Assert.EndsWith($"({expectedCount})", option.Label, StringComparison.Ordinal);
		}
	}

	private static int GetIgnoreOptionCount(IgnoreOptionCounts counts, IgnoreOptionId optionId)
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
			_ => 0
		};
	}

	private static string CaptureIslandFingerprint(MainWindowViewModel viewModel)
	{
		return string.Join(
			"|",
			viewModel.AllRootFoldersChecked,
			viewModel.AllExtensionsChecked,
			viewModel.AllIgnoreChecked,
			string.Join(";", viewModel.RootFolders.Select(static option => $"{option.Name}:{option.IsChecked}")),
			string.Join(";", viewModel.Extensions.Select(static option => $"{option.Name}:{option.IsChecked}")),
			string.Join(";", viewModel.IgnoreOptions.Select(static option => $"{option.Id}:{option.Label}:{option.IsChecked}")));
	}

	private static void AssertSequenceEqual<T>(
		IReadOnlyList<T> expected,
		IReadOnlyList<T> actual,
		string assertionName)
	{
		Assert.True(
			expected.SequenceEqual(actual),
			$"{assertionName} drifted.{Environment.NewLine}" +
			$"Expected=[{string.Join(", ", expected)}]{Environment.NewLine}" +
			$"Actual=[{string.Join(", ", actual)}]");
	}

	private static void HookAllOptionListeners(
		SelectionSyncCoordinator coordinator,
		MainWindowViewModel viewModel)
	{
		coordinator.HookOptionListeners(viewModel.RootFolders);
		coordinator.HookOptionListeners(viewModel.Extensions);
		coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);
	}

	private static MainWindowViewModel CreateViewModel()
	{
		var localization = ProjectLoadWorkflowRuntime.CreateLocalizationService();
		return new MainWindowViewModel(localization, new HelpContentProvider());
	}

	private static SelectionSyncCoordinator CreateCoordinator(MainWindowViewModel viewModel, string rootPath)
	{
		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var filterSelectionService = new FilterOptionSelectionService();
		var ignoreOptionsService = ProjectLoadWorkflowRuntime.CreateIgnoreOptionsService();
		var ignoreRulesService = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();

		return new SelectionSyncCoordinator(
			viewModel,
			scanOptions,
			filterSelectionService,
			ignoreOptionsService,
			(path, selectedIgnoreOptions, selectedRoots) =>
				ignoreRulesService.Build(path, selectedIgnoreOptions, selectedRoots),
			(path, selectedRoots) => ignoreRulesService.GetIgnoreOptionsAvailability(path, selectedRoots) with
			{
				ShowAdvancedCounts = true
			},
			_ => false,
			() => rootPath);
	}

	private enum SettingsActionKind
	{
		ToggleRoot,
		ToggleExtension,
		ToggleIgnore,
		SetAllRoots,
		SetAllExtensions,
		SetAllIgnore,
		Checkpoint
	}

	private sealed record SettingsAction(
		SettingsActionKind Kind,
		string? Name = null,
		IgnoreOptionId? IgnoreOptionId = null,
		bool? IsChecked = null)
	{
		public static SettingsAction ToggleRoot(string name) => new(SettingsActionKind.ToggleRoot, Name: name);
		public static SettingsAction ToggleExtension(string name) => new(SettingsActionKind.ToggleExtension, Name: name);
		public static SettingsAction ToggleIgnore(IgnoreOptionId optionId) =>
			new(SettingsActionKind.ToggleIgnore, IgnoreOptionId: optionId);
		public static SettingsAction SetAllRoots(bool isChecked) =>
			new(SettingsActionKind.SetAllRoots, IsChecked: isChecked);
		public static SettingsAction SetAllExtensions(bool isChecked) =>
			new(SettingsActionKind.SetAllExtensions, IsChecked: isChecked);
		public static SettingsAction SetAllIgnore(bool isChecked) =>
			new(SettingsActionKind.SetAllIgnore, IsChecked: isChecked);
		public static SettingsAction Checkpoint() => new(SettingsActionKind.Checkpoint);

		public override string ToString()
		{
			return Kind switch
			{
				SettingsActionKind.ToggleRoot or SettingsActionKind.ToggleExtension => $"{Kind}:{Name}",
				SettingsActionKind.ToggleIgnore => $"{Kind}:{IgnoreOptionId}",
				SettingsActionKind.SetAllRoots or SettingsActionKind.SetAllExtensions or SettingsActionKind.SetAllIgnore =>
					$"{Kind}:{IsChecked}",
				_ => Kind.ToString()
			};
		}
	}

	private sealed class SettingsIslandOracle
	{
		private readonly string _rootPath;
		private readonly WorkflowServices _services;
		private readonly Dictionary<string, bool> _rootStates;
		private readonly Dictionary<string, bool> _extensionStates;
		private readonly Dictionary<IgnoreOptionId, bool> _ignoreStates;
		private bool _allRootsChecked;
		private bool _allExtensionsChecked;
		private bool? _ignoreAllPreference;
		private RefreshRoute _nextRefreshRoute;

		private SettingsIslandOracle(
			string rootPath,
			WorkflowServices services,
			SelectionRefreshSnapshot baseline)
		{
			_rootPath = rootPath;
			_services = services;
			CurrentSnapshot = baseline;
			_rootStates = baseline.RootOptions?.ToDictionary(
				static option => option.Name,
				static option => option.IsChecked,
				PathComparer.Default) ?? new Dictionary<string, bool>(PathComparer.Default);
			_extensionStates = baseline.ExtensionOptions.ToDictionary(
				static option => option.Name,
				static option => option.IsChecked,
				StringComparer.OrdinalIgnoreCase);
			_ignoreStates = new Dictionary<IgnoreOptionId, bool>(baseline.IgnoreOptionStateCache);
			_allRootsChecked = baseline.RootOptions is null ||
			                   baseline.RootOptions.Count == 0 ||
			                   baseline.RootOptions.All(static option => option.IsChecked);
			_allExtensionsChecked = baseline.EffectiveExtensionOptions.Count > 0 &&
			                        baseline.EffectiveExtensionOptions.All(static option => option.IsChecked);
		}

		public SelectionRefreshSnapshot CurrentSnapshot { get; private set; }

		public static SettingsIslandOracle Create(string rootPath)
		{
			var services = CreateServices();
			var baseline = services.Engine.ComputeFullRefreshSnapshot(
				CreateDefaultContext(rootPath) with { CaptureTreeInventory = true },
				CancellationToken.None);
			return new SettingsIslandOracle(rootPath, services, baseline);
		}

		public void Apply(SettingsAction action)
		{
			switch (action.Kind)
			{
				case SettingsActionKind.ToggleRoot:
					ToggleVisibleRoot(action.Name!);
					_nextRefreshRoute = RefreshRoute.Live;
					break;
				case SettingsActionKind.ToggleExtension:
					ToggleVisibleExtension(action.Name!);
					_nextRefreshRoute = RefreshRoute.Live;
					break;
				case SettingsActionKind.ToggleIgnore:
					ToggleVisibleIgnore(action.IgnoreOptionId!.Value);
					_nextRefreshRoute = IsLiveIgnoreOption(action.IgnoreOptionId.Value)
						? RefreshRoute.Live
						: RefreshRoute.Full;
					break;
				case SettingsActionKind.SetAllRoots:
					SetAllVisibleRoots(action.IsChecked!.Value);
					_nextRefreshRoute = RefreshRoute.Live;
					break;
				case SettingsActionKind.SetAllExtensions:
					SetAllVisibleExtensions(action.IsChecked!.Value);
					_nextRefreshRoute = RefreshRoute.Live;
					break;
				case SettingsActionKind.SetAllIgnore:
					SetAllIgnoreOptions(action.IsChecked!.Value);
					_nextRefreshRoute = RefreshRoute.Full;
					break;
				case SettingsActionKind.Checkpoint:
					throw new InvalidOperationException("Checkpoint does not mutate the settings oracle.");
				default:
					throw new ArgumentOutOfRangeException(nameof(action), action, null);
			}
		}

		public SelectionRefreshSnapshot Recompute()
		{
			var context = CreateContext(captureTreeInventory: _nextRefreshRoute == RefreshRoute.Full);
			var computedSnapshot = _nextRefreshRoute == RefreshRoute.Full
				? _services.Engine.ComputeFullRefreshSnapshot(context, CancellationToken.None)
				: _services.Engine.ComputeLiveRefreshSnapshot(
					context,
					CollectCurrentlySelectedVisibleRoots(),
					CancellationToken.None);
			var snapshot = computedSnapshot.RootOptions is null
				? computedSnapshot with
				{
					RootOptions = CurrentSnapshot.RootOptions?
						.Select(option => new SelectionOption(
							option.Name,
							_rootStates.GetValueOrDefault(option.Name)))
						.ToArray()
				}
				: computedSnapshot;

			MergeNewlyDiscoveredState(snapshot);
			SynchronizeMasterStates(snapshot);
			CurrentSnapshot = snapshot;
			return snapshot;
		}

		private SelectionRefreshContext CreateContext(bool captureTreeInventory)
		{
			var visibleIgnoreIds = CurrentSnapshot.IgnoreOptions
				.Select(static option => option.Id)
				.ToHashSet();
			var selectedIgnoreIds = _ignoreStates
				.Where(pair => pair.Value && visibleIgnoreIds.Contains(pair.Key))
				.Select(static pair => pair.Key)
				.ToHashSet();

			return new SelectionRefreshContext(
				Path: _rootPath,
				PreparedSelectionMode: PreparedSelectionMode.Defaults,
				AllRootFoldersChecked: _allRootsChecked && !_rootStates.Values.Contains(false),
				AllExtensionsChecked: _allExtensionsChecked && !_extensionStates.Values.Contains(false),
				RootSelectionInitialized: true,
				RootSelectionCache: _rootStates.Where(static pair => pair.Value)
					.Select(static pair => pair.Key)
					.ToHashSet(PathComparer.Default),
				ExtensionsSelectionInitialized: true,
				ExtensionsSelectionCache: _extensionStates.Where(static pair => pair.Value)
					.Select(static pair => pair.Key)
					.ToHashSet(StringComparer.OrdinalIgnoreCase),
				IgnoreSelectionInitialized: true,
				IgnoreSelectionCache: selectedIgnoreIds,
				IgnoreOptionStateCache: new Dictionary<IgnoreOptionId, bool>(_ignoreStates),
				IgnoreAllPreference: _ignoreAllPreference,
				CurrentSnapshotState: new IgnoreSectionSnapshotState(
					CurrentSnapshot.HasIgnoreOptionCounts,
					CurrentSnapshot.IgnoreOptionCounts,
					CurrentSnapshot.ControllerImpactCounts,
					CurrentSnapshot.ExtensionlessEntriesCount > 0,
					CurrentSnapshot.ExtensionlessEntriesCount),
				RootOptionStateCache: new Dictionary<string, bool>(_rootStates, PathComparer.Default),
				ExtensionOptionStateCache: new Dictionary<string, bool>(
					_extensionStates,
					StringComparer.OrdinalIgnoreCase),
				IgnoreOptionStateCacheIsComplete: true,
				CaptureTreeInventory: captureTreeInventory);
		}

		private HashSet<string> CollectCurrentlySelectedVisibleRoots()
		{
			return (CurrentSnapshot.RootOptions ?? [])
				.Where(option => _rootStates.GetValueOrDefault(option.Name))
				.Select(static option => option.Name)
				.ToHashSet(PathComparer.Default);
		}

		private static bool IsLiveIgnoreOption(IgnoreOptionId optionId)
		{
			return optionId is IgnoreOptionId.HiddenFiles
				or IgnoreOptionId.DotFiles
				or IgnoreOptionId.EmptyFiles
				or IgnoreOptionId.ExtensionlessFiles;
		}

		private void ToggleVisibleRoot(string name)
		{
			Assert.Contains(CurrentSnapshot.RootOptions ?? [], option =>
				string.Equals(option.Name, name, StringComparison.Ordinal));
			_rootStates[name] = !_rootStates[name];
			_allRootsChecked = (CurrentSnapshot.RootOptions ?? [])
				.All(option => _rootStates.GetValueOrDefault(option.Name));
		}

		private void ToggleVisibleExtension(string name)
		{
			Assert.Contains(CurrentSnapshot.EffectiveExtensionOptions, option =>
				string.Equals(option.Name, name, StringComparison.OrdinalIgnoreCase));
			_extensionStates[name] = !_extensionStates[name];
			_allExtensionsChecked = CurrentSnapshot.EffectiveExtensionOptions.Count > 0 &&
			                        CurrentSnapshot.EffectiveExtensionOptions.All(option =>
				                        _extensionStates.GetValueOrDefault(option.Name));
		}

		private void ToggleVisibleIgnore(IgnoreOptionId optionId)
		{
			Assert.Contains(CurrentSnapshot.IgnoreOptions, option => option.Id == optionId);
			_ignoreStates[optionId] = !_ignoreStates[optionId];
			_ignoreAllPreference = null;
		}

		private void SetAllVisibleRoots(bool isChecked)
		{
			foreach (var option in CurrentSnapshot.RootOptions ?? [])
				_rootStates[option.Name] = isChecked;
			_allRootsChecked = isChecked;
		}

		private void SetAllVisibleExtensions(bool isChecked)
		{
			foreach (var option in CurrentSnapshot.EffectiveExtensionOptions)
				_extensionStates[option.Name] = isChecked;
			_allExtensionsChecked = isChecked;
		}

		private void SetAllIgnoreOptions(bool isChecked)
		{
			foreach (var optionId in _ignoreStates.Keys.ToArray())
				_ignoreStates[optionId] = isChecked;
			_ignoreAllPreference = isChecked;
		}

		private void MergeNewlyDiscoveredState(SelectionRefreshSnapshot snapshot)
		{
			foreach (var option in snapshot.RootOptions ?? [])
				AssertOrAddState(_rootStates, option.Name, option.IsChecked);

			foreach (var option in snapshot.ExtensionOptions)
				AssertOrAddState(_extensionStates, option.Name, option.IsChecked);

			foreach (var (optionId, isChecked) in snapshot.IgnoreOptionStateCache)
			{
				if (_ignoreStates.TryGetValue(optionId, out var expectedState))
					Assert.Equal(expectedState, isChecked);
				else
					_ignoreStates[optionId] = isChecked;
			}
		}

		private static void AssertOrAddState(
			IDictionary<string, bool> states,
			string name,
			bool actualState)
		{
			if (states.TryGetValue(name, out var expectedState))
			{
				Assert.True(
					expectedState == actualState,
					$"Oracle state for '{name}' changed unexpectedly. Expected={expectedState}; Actual={actualState}.");
				return;
			}

			states[name] = actualState;
		}

		private void SynchronizeMasterStates(SelectionRefreshSnapshot snapshot)
		{
			_allRootsChecked = snapshot.RootOptions is null ||
			                   snapshot.RootOptions.Count == 0 ||
			                   snapshot.RootOptions.All(static option => option.IsChecked);
			_allExtensionsChecked = snapshot.EffectiveExtensionOptions.Count > 0 &&
			                        snapshot.EffectiveExtensionOptions.All(static option => option.IsChecked);
		}


		private enum RefreshRoute
		{
			Live,
			Full
		}
	}

	private sealed class SettingsIslandWorkspace : IDisposable
	{
		private SettingsIslandWorkspace(string rootPath)
		{
			RootPath = rootPath;
		}

		public string RootPath { get; }

		public static SettingsIslandWorkspace Create()
		{
			var rootPath = Path.Combine(
				Path.GetTempPath(),
				"DevProjex",
				"SettingsIslandMatrix",
				Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(rootPath);
			Seed(rootPath);
			return new SettingsIslandWorkspace(rootPath);
		}

		public void Dispose()
		{
			try
			{
				if (Directory.Exists(RootPath))
					Directory.Delete(RootPath, recursive: true);
			}
			catch
			{
				// Best effort cleanup for files briefly retained by the test runner.
			}
		}

		private static void Seed(string rootPath)
		{
			WriteFile(rootPath, ".gitignore", "artifacts/\n*.tmp\n");

			WriteFile(rootPath, Path.Combine("alpha", ".gitignore"), "*.log\n!keep.log\n.generated/\n");
			WriteFile(rootPath, Path.Combine("alpha", "Alpha.csproj"), "<Project />\n");
			WriteFile(rootPath, Path.Combine("alpha", "src", "Program.cs"), BuildCSharpFile("Alpha", "Program", 8));
			WriteFile(rootPath, Path.Combine("alpha", "src", "Features", "Orders", "CreateOrder.cs"),
				BuildCSharpFile("Alpha.Features.Orders", "CreateOrder", 6));
			WriteFile(rootPath, Path.Combine("alpha", "src", "empty.cs"), string.Empty);
			WriteFile(rootPath, Path.Combine("alpha", "runtime.log"), "ignored runtime log\n");
			WriteFile(rootPath, Path.Combine("alpha", "keep.log"), "explicitly retained log\n");
			WriteFile(rootPath, Path.Combine("alpha", ".generated", "state.json"), "{}\n");
			WriteFile(rootPath, Path.Combine("alpha", "bin", "Debug", "net10.0", "Alpha.dll"), "binary\n");
			WriteFile(rootPath, Path.Combine("alpha", ".private", "nested", "secret.cs"), "class Secret {}\n");
			Directory.CreateDirectory(Path.Combine(rootPath, "alpha", "src", "Empty", "Nested", "Leaf"));

			WriteFile(rootPath, Path.Combine("beta", "package.json"), "{}\n");
			WriteFile(rootPath, Path.Combine("beta", "src", "app.ts"), "export const app = true;\n");
			WriteFile(rootPath, Path.Combine("beta", "src", "config.json"), "{ \"enabled\": true }\n");
			WriteFile(rootPath, Path.Combine("beta", "node_modules", "pkg", "dist", "index.js"),
				"module.exports = {};\n");
			WriteFile(rootPath, Path.Combine("beta", ".cache", "v1", "cache.json"), "{}\n");

			WriteFile(rootPath, Path.Combine("gamma", "guide.md"), BuildMarkdown("Settings island", 7));
			WriteFile(rootPath, Path.Combine("gamma", "notes.txt"), "one\ntwo\nthree\n");
			WriteFile(rootPath, Path.Combine("gamma", "README"), "extensionless documentation\n");
			WriteFile(rootPath, Path.Combine("gamma", "empty.txt"), string.Empty);
			WriteFile(rootPath, Path.Combine("gamma", ".drafts", "deep", "draft.md"), "# draft\n");

			WriteFile(rootPath, Path.Combine("artifacts", "reports", "2026", "summary.txt"), "ignored report\n");
			WriteFile(rootPath, Path.Combine("artifacts", "logs", "build.log"), "ignored build log\n");
			WriteFile(rootPath, Path.Combine(".root-dot", "nested", "deep", "visible.txt"), "dot root payload\n");
			WriteFile(rootPath, ".env", "MODE=test\n");
			WriteFile(rootPath, "LICENSE", "extensionless root file\n");
			WriteFile(rootPath, "empty-root-file.txt", string.Empty);
			Directory.CreateDirectory(Path.Combine(rootPath, "delta-empty", "level-1", "level-2", "level-3"));

			var hiddenRoot = Path.Combine(rootPath, "hidden-root");
			WriteFile(rootPath, Path.Combine("hidden-root", "nested", "hidden.txt"), "hidden root payload\n");
			TryMarkHidden(hiddenRoot);

			var hiddenFile = Path.Combine(rootPath, "gamma", "hidden-note.txt");
			WriteFile(rootPath, Path.Combine("gamma", "hidden-note.txt"), "hidden file payload\n");
			TryMarkHidden(hiddenFile);
		}

		private static void WriteFile(string rootPath, string relativePath, string content)
		{
			var fullPath = Path.Combine(rootPath, relativePath);
			var directoryPath = Path.GetDirectoryName(fullPath);
			if (!string.IsNullOrWhiteSpace(directoryPath))
				Directory.CreateDirectory(directoryPath);

			File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		}

		private static string BuildMarkdown(string title, int itemCount)
		{
			var builder = new StringBuilder();
			builder.AppendLine($"# {title}");
			for (var index = 1; index <= itemCount; index++)
				builder.AppendLine($"- item {index}");
			return builder.ToString();
		}

		private static string BuildCSharpFile(string @namespace, string typeName, int methodCount)
		{
			var builder = new StringBuilder();
			builder.AppendLine($"namespace {@namespace};");
			builder.AppendLine($"public sealed class {typeName}");
			builder.AppendLine("{");
			for (var index = 1; index <= methodCount; index++)
				builder.AppendLine($"    public int Value{index} => {index};");
			builder.AppendLine("}");
			return builder.ToString();
		}

		private static void TryMarkHidden(string path)
		{
			try
			{
				File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
			}
			catch
			{
				// Hidden attributes depend on the host filesystem; the remaining matrix stays portable.
			}
		}
	}

	private static StringComparison PathComparison =>
		OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
