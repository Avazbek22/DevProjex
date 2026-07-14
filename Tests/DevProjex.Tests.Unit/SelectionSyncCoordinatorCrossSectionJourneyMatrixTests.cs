using DevProjex.Application.Models;
using DevProjex.Infrastructure.FileSystem;
using DevProjex.Tests.Shared.ProjectLoadWorkflow;
using static DevProjex.Tests.Shared.ProjectLoadWorkflow.ProjectLoadWorkflowRefreshHarness;

namespace DevProjex.Tests.Unit;

[Collection("AvaloniaUI")]
public sealed class SelectionSyncCoordinatorCrossSectionJourneyMatrixTests
{
	private const int PairwiseColumnCapacity = 31;
	private const int PairwiseRowCount = 32;
	private const int CombatStepCount = 96;
	private const int CombatTreeCheckpointInterval = 8;

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

	[AvaloniaFact]
	public async Task PublicSettingsEvents_EveryIgnorePowerSetState_MatchesIslandTreeAndMetrics()
	{
		using var workspace = SettingsIslandWorkspace.Create();
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, workspace.RootPath);
		await InitializeCoordinatorAsync(coordinator, viewModel, workspace.RootPath);

		var oracle = SettingsIslandOracle.Create(workspace.RootPath);
		var ignoreOptionIds = viewModel.IgnoreOptions.Select(static option => option.Id).ToArray();
		Assert.Equal(workspace.ExpectedIgnoreOptionIds.Order(), ignoreOptionIds.Order());

		var baselineFingerprint = CaptureIslandFingerprint(viewModel);
		var visitedMasks = new HashSet<int>();
		var stateVisits = new int[ignoreOptionIds.Length, 2];
		var allMask = (1 << ignoreOptionIds.Length) - 1;
		var currentMask = allMask;

		// Gray code changes exactly one checkbox between adjacent states, so every
		// assertion observes a real public single-toggle transition rather than setup mutation.
		for (var sequenceIndex = 0; sequenceIndex <= allMask; sequenceIndex++)
		{
			var targetMask = allMask ^ (sequenceIndex ^ (sequenceIndex >> 1));
			if (sequenceIndex > 0)
			{
				var changedBit = FindSingleChangedBit(currentMask, targetMask, ignoreOptionIds.Length);
				var action = SettingsAction.ToggleIgnore(ignoreOptionIds[changedBit]);
				await ExecuteActionAndRefreshAsync(
					viewModel,
					coordinator,
					oracle,
					workspace.RootPath,
					action);
			}

			var stepName = $"ignore power-set state {sequenceIndex + 1}/{allMask + 1}, mask={targetMask}";
			Assert.Equal(targetMask, CaptureIgnoreMask(viewModel, ignoreOptionIds, stepName));
			AssertIslandMatchesOracle(viewModel, coordinator, oracle.CurrentSnapshot, stepName);
			await AssertTreeAndMetricsMatchOracleAsync(
				workspace.RootPath,
				viewModel,
				coordinator,
				oracle.CurrentSnapshot,
				stepName);

			Assert.True(visitedMasks.Add(targetMask), $"{stepName}: duplicate Gray-code state.");
			for (var bitIndex = 0; bitIndex < ignoreOptionIds.Length; bitIndex++)
				stateVisits[bitIndex, (targetMask >> bitIndex) & 1]++;
			currentMask = targetMask;
		}

		Assert.Equal(1 << ignoreOptionIds.Length, visitedMasks.Count);
		for (var bitIndex = 0; bitIndex < ignoreOptionIds.Length; bitIndex++)
		{
			Assert.Equal(1 << (ignoreOptionIds.Length - 1), stateVisits[bitIndex, 0]);
			Assert.Equal(1 << (ignoreOptionIds.Length - 1), stateVisits[bitIndex, 1]);
		}
		AssertEveryIgnorePairVisitedAllBooleanCombinations(visitedMasks, ignoreOptionIds);

		await ExecuteActionAndRefreshAsync(
			viewModel,
			coordinator,
			oracle,
			workspace.RootPath,
			SettingsAction.SetAllIgnore(true));
		Assert.Equal(baselineFingerprint, CaptureIslandFingerprint(viewModel));
		await AssertTreeAndMetricsMatchOracleAsync(
			workspace.RootPath,
			viewModel,
			coordinator,
			oracle.CurrentSnapshot,
			"ignore power-set final round-trip");
	}

	[AvaloniaFact]
	public async Task PublicSettingsEvents_WithoutHiddenAttributes_ExcludesOnlyUnavailableOptionsAndMatchesOracle()
	{
		using var workspace = SettingsIslandWorkspace.Create(markHiddenAttributes: false);
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, workspace.RootPath);
		await InitializeCoordinatorAsync(coordinator, viewModel, workspace.RootPath);
		var oracle = SettingsIslandOracle.Create(workspace.RootPath);

		var actualIgnoreOptionIds = viewModel.IgnoreOptions.Select(static option => option.Id).Order().ToArray();
		var expectedIgnoreOptionIds = ResolveExpectedIgnoreOptionIds(
			hiddenFoldersSupported: false,
			hiddenFilesSupported: false).Order().ToArray();

		Assert.Equal(expectedIgnoreOptionIds, actualIgnoreOptionIds);
		AssertIslandMatchesOracle(viewModel, coordinator, oracle.CurrentSnapshot, "hidden attributes unavailable");
		await AssertTreeAndMetricsMatchOracleAsync(
			workspace.RootPath,
			viewModel,
			coordinator,
			oracle.CurrentSnapshot,
			"hidden attributes unavailable");
	}

	[AvaloniaTheory]
	[MemberData(nameof(PairwiseRows))]
	public async Task PublicSettingsEvents_PairwiseAllSectionsBurst_PreservesEveryRequestedStateAndResult(int row)
	{
		using var workspace = SettingsIslandWorkspace.Create();
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, workspace.RootPath);
		await InitializeCoordinatorAsync(coordinator, viewModel, workspace.RootPath);
		var oracle = SettingsIslandOracle.Create(workspace.RootPath);

		await ExecuteActionAndRefreshAsync(
			viewModel,
			coordinator,
			oracle,
			workspace.RootPath,
			SettingsAction.SetAllIgnore(false));

		var rootNames = viewModel.RootFolders.Select(static option => option.Name).ToArray();
		var extensionNames = viewModel.Extensions.Select(static option => option.Name).ToArray();
		var ignoreOptionIds = viewModel.IgnoreOptions.Select(static option => option.Id).ToArray();
		var totalControlCount = rootNames.Length + extensionNames.Length + ignoreOptionIds.Length;
		Assert.InRange(totalControlCount, 3, PairwiseColumnCapacity);
		Assert.Equal(workspace.ExpectedIgnoreOptionIds.Order(), ignoreOptionIds.Order());

		var actions = new List<SettingsAction>(totalControlCount + 2)
		{
			SettingsAction.SetAllRoots(false),
			SettingsAction.SetAllExtensions(false)
		};
		var column = 1;
		var expectedRootStates = BuildPairwiseStates(rootNames, row, ref column, actions, SettingsAction.ToggleRoot);
		var expectedExtensionStates = BuildPairwiseStates(
			extensionNames,
			row,
			ref column,
			actions,
			SettingsAction.ToggleExtension);
		var expectedIgnoreStates = BuildPairwiseIgnoreStates(ignoreOptionIds, row, ref column, actions);
		Assert.Equal(totalControlCount + 1, column);

		ExecuteActionBurst(viewModel, coordinator, oracle, workspace.RootPath, actions);
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);
		var expectedSnapshot = oracle.Recompute();
		var stepName = $"pairwise row {row}/{PairwiseRowCount - 1}";
		AssertIslandMatchesOracle(viewModel, coordinator, expectedSnapshot, stepName);
		AssertOracleStates(oracle, expectedRootStates, expectedExtensionStates, expectedIgnoreStates, stepName);
		await AssertTreeAndMetricsMatchOracleAsync(
			workspace.RootPath,
			viewModel,
			coordinator,
			expectedSnapshot,
			stepName);
		await AssertIslandRemainsStableAsync(viewModel, coordinator, stepName);

		if (!viewModel.AllRootFoldersChecked)
		{
			expectedSnapshot = await ExecuteActionAndRefreshAsync(
				viewModel,
				coordinator,
				oracle,
				workspace.RootPath,
				SettingsAction.SetAllRoots(true));
		}
		if (!viewModel.AllExtensionsChecked)
		{
			expectedSnapshot = await ExecuteActionAndRefreshAsync(
				viewModel,
				coordinator,
				oracle,
				workspace.RootPath,
				SettingsAction.SetAllExtensions(true));
		}
		AssertIslandMatchesOracle(viewModel, coordinator, expectedSnapshot, $"{stepName}: evidence exposed");

		expectedSnapshot = await DisableAllIgnoreOptionsIndividuallyAsync(
			viewModel,
			coordinator,
			oracle,
			workspace.RootPath);
		var restoreSelectionActions = BuildSelectionConvergenceActions(
			viewModel,
			expectedRootStates,
			expectedExtensionStates);
		if (restoreSelectionActions.Count > 0)
		{
			ExecuteActionBurst(
				viewModel,
				coordinator,
				oracle,
				workspace.RootPath,
				restoreSelectionActions);
			await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);
			expectedSnapshot = oracle.Recompute();
		}

		AssertIslandMatchesOracle(viewModel, coordinator, expectedSnapshot, $"{stepName}: all ignores exposed");
		AssertVisibleStates(viewModel.RootFolders, expectedRootStates, PathComparer.Default, $"{stepName}: roots");
		AssertVisibleStates(
			viewModel.Extensions,
			expectedExtensionStates,
			StringComparer.OrdinalIgnoreCase,
			$"{stepName}: extensions");
	}

	[AvaloniaTheory]
	[MemberData(nameof(CombatSeeds))]
	public async Task PublicSettingsEvents_DeterministicCombatStateMachine_RemainsConsistent(
		int seed,
		int stepCount)
	{
		using var workspace = SettingsIslandWorkspace.Create();
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, workspace.RootPath);
		await InitializeCoordinatorAsync(coordinator, viewModel, workspace.RootPath);
		var oracle = SettingsIslandOracle.Create(workspace.RootPath);
		var toggledIgnoreIds = new HashSet<IgnoreOptionId>();
		var visitedFingerprints = new HashSet<string>(StringComparer.Ordinal);
		var actionKinds = new HashSet<SettingsActionKind>();

		for (var stepIndex = 0; stepIndex < stepCount; stepIndex++)
		{
			var action = CreateCombatAction(viewModel, seed, stepIndex);
			if (action.IgnoreOptionId is { } optionId)
				toggledIgnoreIds.Add(optionId);
			actionKinds.Add(action.Kind);

			var expectedSnapshot = await ExecuteActionAndRefreshAsync(
				viewModel,
				coordinator,
				oracle,
				workspace.RootPath,
				action);
			var stepName = $"combat seed {seed}: step {stepIndex + 1}/{stepCount} ({action})";
			AssertIslandMatchesOracle(viewModel, coordinator, expectedSnapshot, stepName);
			visitedFingerprints.Add(CaptureIslandFingerprint(viewModel));

			if (stepIndex % CombatTreeCheckpointInterval == 0 || stepIndex == stepCount - 1)
			{
				await AssertTreeAndMetricsMatchOracleAsync(
					workspace.RootPath,
					viewModel,
					coordinator,
					expectedSnapshot,
					stepName);
			}
			if (stepIndex % 7 == 0)
				await AssertIslandRemainsStableAsync(viewModel, coordinator, stepName);
		}

		Assert.Equal(workspace.ExpectedIgnoreOptionIds.Order(), toggledIgnoreIds.Order());
		Assert.Contains(SettingsActionKind.ToggleRoot, actionKinds);
		Assert.Contains(SettingsActionKind.ToggleExtension, actionKinds);
		Assert.Contains(SettingsActionKind.ToggleIgnore, actionKinds);
		Assert.Contains(SettingsActionKind.SetAllRoots, actionKinds);
		Assert.Contains(SettingsActionKind.SetAllExtensions, actionKinds);
		Assert.Contains(SettingsActionKind.SetAllIgnore, actionKinds);
		Assert.True(
			visitedFingerprints.Count >= stepCount / 2,
			$"Combat seed {seed} produced only {visitedFingerprints.Count} distinct island states.");
	}

	[Fact]
	public void PairwiseBooleanPattern_CoversEveryPairAndPolarity()
	{
		for (var leftColumn = 1; leftColumn <= PairwiseColumnCapacity; leftColumn++)
		{
			for (var rightColumn = leftColumn + 1; rightColumn <= PairwiseColumnCapacity; rightColumn++)
			{
				var combinations = new HashSet<int>();
				for (var row = 0; row < PairwiseRowCount; row++)
				{
					var left = GetPairwiseState(row, leftColumn) ? 1 : 0;
					var right = GetPairwiseState(row, rightColumn) ? 1 : 0;
					combinations.Add((left << 1) | right);
				}
				Assert.Equal(4, combinations.Count);
			}
		}
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public void ResolveExpectedIgnoreOptionIds_ReflectsHostHiddenAttributeCapabilities(
		bool hiddenFoldersSupported,
		bool hiddenFilesSupported)
	{
		var expected = ResolveExpectedIgnoreOptionIds(hiddenFoldersSupported, hiddenFilesSupported);

		Assert.Equal(hiddenFoldersSupported, expected.Contains(IgnoreOptionId.HiddenFolders));
		Assert.Equal(hiddenFilesSupported, expected.Contains(IgnoreOptionId.HiddenFiles));
		Assert.Equal(
			Enum.GetValues<IgnoreOptionId>().Length - (hiddenFoldersSupported ? 0 : 1) - (hiddenFilesSupported ? 0 : 1),
			expected.Count);
		Assert.All(
			Enum.GetValues<IgnoreOptionId>()
				.Except([IgnoreOptionId.HiddenFolders, IgnoreOptionId.HiddenFiles]),
			optionId => Assert.Contains(optionId, expected));
	}

	public static IEnumerable<object[]> PairwiseRows()
	{
		for (var row = 0; row < PairwiseRowCount; row++)
			yield return [row];
	}

	public static IEnumerable<object[]> CombatSeeds()
	{
		foreach (var seed in new[] { 3, 11, 29, 47, 71, 101 })
			yield return [seed, CombatStepCount];
	}

	private static async Task InitializeCoordinatorAsync(
		SelectionSyncCoordinator coordinator,
		MainWindowViewModel viewModel,
		string rootPath)
	{
		await coordinator.RefreshRootAndDependentsAsync(
			rootPath,
			TestContext.Current.CancellationToken);
		HookAllOptionListeners(coordinator, viewModel);
	}

	private static async Task<SelectionRefreshSnapshot> ExecuteActionAndRefreshAsync(
		MainWindowViewModel viewModel,
		SelectionSyncCoordinator coordinator,
		SettingsIslandOracle oracle,
		string rootPath,
		SettingsAction action)
	{
		ExecuteSettingsAction(viewModel, coordinator, rootPath, action);
		oracle.Apply(action);
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);
		return oracle.Recompute();
	}

	private static async Task<SelectionRefreshSnapshot> DisableAllIgnoreOptionsIndividuallyAsync(
		MainWindowViewModel viewModel,
		SelectionSyncCoordinator coordinator,
		SettingsIslandOracle oracle,
		string rootPath)
	{
		var snapshot = oracle.CurrentSnapshot;
		var maximumTransitions = viewModel.IgnoreOptions.Count * 2;
		for (var transition = 0; transition < maximumTransitions; transition++)
		{
			var checkedOption = viewModel.IgnoreOptions.FirstOrDefault(static option => option.IsChecked);
			if (checkedOption is null)
				break;

			snapshot = await ExecuteActionAndRefreshAsync(
				viewModel,
				coordinator,
				oracle,
				rootPath,
				SettingsAction.ToggleIgnore(checkedOption.Id));
		}

		Assert.All(oracle.IgnoreStates, static pair => Assert.False(pair.Value));
		Assert.All(viewModel.IgnoreOptions, static option => Assert.False(option.IsChecked));
		return snapshot;
	}

	private static void ExecuteActionBurst(
		MainWindowViewModel viewModel,
		SelectionSyncCoordinator coordinator,
		SettingsIslandOracle oracle,
		string rootPath,
		IEnumerable<SettingsAction> actions)
	{
		foreach (var action in actions)
		{
			ExecuteSettingsAction(viewModel, coordinator, rootPath, action);
			oracle.Apply(action);
		}
	}

	private static Dictionary<string, bool> BuildPairwiseStates(
		IReadOnlyList<string> names,
		int row,
		ref int column,
		ICollection<SettingsAction> actions,
		Func<string, SettingsAction> actionFactory)
	{
		var states = new Dictionary<string, bool>(PathComparer.Default);
		foreach (var name in names)
		{
			var isChecked = GetPairwiseState(row, column++);
			states.Add(name, isChecked);
			if (isChecked)
				actions.Add(actionFactory(name));
		}
		return states;
	}

	private static Dictionary<IgnoreOptionId, bool> BuildPairwiseIgnoreStates(
		IReadOnlyList<IgnoreOptionId> optionIds,
		int row,
		ref int column,
		ICollection<SettingsAction> actions)
	{
		var states = new Dictionary<IgnoreOptionId, bool>();
		foreach (var optionId in optionIds)
		{
			var isChecked = GetPairwiseState(row, column++);
			states.Add(optionId, isChecked);
			if (isChecked)
				actions.Add(SettingsAction.ToggleIgnore(optionId));
		}
		return states;
	}

	private static IReadOnlyList<SettingsAction> BuildSelectionConvergenceActions(
		MainWindowViewModel viewModel,
		IReadOnlyDictionary<string, bool> expectedRootStates,
		IReadOnlyDictionary<string, bool> expectedExtensionStates)
	{
		var actions = new List<SettingsAction>();
		foreach (var option in viewModel.RootFolders)
		{
			Assert.True(expectedRootStates.TryGetValue(option.Name, out var expectedState));
			if (option.IsChecked != expectedState)
				actions.Add(SettingsAction.ToggleRoot(option.Name));
		}

		foreach (var option in viewModel.Extensions)
		{
			Assert.True(expectedExtensionStates.TryGetValue(option.Name, out var expectedState));
			if (option.IsChecked != expectedState)
				actions.Add(SettingsAction.ToggleExtension(option.Name));
		}
		return actions;
	}

	private static bool GetPairwiseState(int row, int column)
	{
		// Distinct non-zero five-bit linear forms are pairwise independent over GF(2).
		// Across 32 rows each pair therefore produces 00, 01, 10 and 11.
		var value = row & column;
		var parity = 0;
		while (value != 0)
		{
			parity ^= value & 1;
			value >>= 1;
		}
		return parity != 0;
	}

	private static SettingsAction CreateCombatAction(
		MainWindowViewModel viewModel,
		int seed,
		int stepIndex)
	{
		var phase = stepIndex % 12;
		var cycle = stepIndex / 12;
		return phase switch
		{
			0 => SettingsAction.SetAllIgnore(!viewModel.AllIgnoreChecked),
			1 => SettingsAction.SetAllRoots(!viewModel.AllRootFoldersChecked),
			2 => SettingsAction.SetAllExtensions(!viewModel.AllExtensionsChecked),
			>= 3 and <= 6 => SettingsAction.ToggleIgnore(
				SelectCombatIgnoreOption(viewModel, seed + cycle * 4 + phase - 3)),
			>= 7 and <= 8 => SettingsAction.ToggleRoot(
				SelectCombatName(viewModel.RootFolders, seed + cycle * 2 + phase - 7, "root")),
			_ => SettingsAction.ToggleExtension(
				SelectCombatName(viewModel.Extensions, seed + cycle * 3 + phase - 9, "extension"))
		};
	}

	private static IgnoreOptionId SelectCombatIgnoreOption(MainWindowViewModel viewModel, int ordinal)
	{
		Assert.NotEmpty(viewModel.IgnoreOptions);
		return viewModel.IgnoreOptions[PositiveModulo(ordinal, viewModel.IgnoreOptions.Count)].Id;
	}

	private static string SelectCombatName(
		IReadOnlyList<SelectionOptionViewModel> options,
		int ordinal,
		string sectionName)
	{
		Assert.True(options.Count > 0, $"Combat matrix requires at least one visible {sectionName} option.");
		return options[PositiveModulo(ordinal, options.Count)].Name;
	}

	private static int PositiveModulo(int value, int divisor)
	{
		var remainder = value % divisor;
		return remainder < 0 ? remainder + divisor : remainder;
	}

	private static int FindSingleChangedBit(int leftMask, int rightMask, int bitCount)
	{
		var difference = leftMask ^ rightMask;
		Assert.True(difference != 0 && (difference & (difference - 1)) == 0,
			$"Gray-code transition must change exactly one bit. Left={leftMask}; Right={rightMask}.");
		for (var bitIndex = 0; bitIndex < bitCount; bitIndex++)
		{
			if ((difference & (1 << bitIndex)) != 0)
				return bitIndex;
		}
		throw new InvalidOperationException("Changed Gray-code bit was outside the ignore option range.");
	}

	private static int CaptureIgnoreMask(
		MainWindowViewModel viewModel,
		IReadOnlyList<IgnoreOptionId> optionIds,
		string stepName)
	{
		Assert.Equal(optionIds.Count, viewModel.IgnoreOptions.Count);
		var mask = 0;
		for (var bitIndex = 0; bitIndex < optionIds.Count; bitIndex++)
		{
			var option = Assert.Single(viewModel.IgnoreOptions, candidate => candidate.Id == optionIds[bitIndex]);
			if (option.IsChecked)
				mask |= 1 << bitIndex;
		}
		Assert.Equal(optionIds.Count, viewModel.IgnoreOptions.Select(static option => option.Id).Distinct().Count());
		Assert.True(mask >= 0, $"{stepName}: invalid ignore mask.");
		return mask;
	}

	private static void AssertEveryIgnorePairVisitedAllBooleanCombinations(
		IReadOnlySet<int> visitedMasks,
		IReadOnlyList<IgnoreOptionId> optionIds)
	{
		for (var leftBit = 0; leftBit < optionIds.Count; leftBit++)
		{
			for (var rightBit = leftBit + 1; rightBit < optionIds.Count; rightBit++)
			{
				var combinations = visitedMasks
					.Select(mask => (((mask >> leftBit) & 1) << 1) | ((mask >> rightBit) & 1))
					.ToHashSet();
				Assert.True(
					combinations.SetEquals([0, 1, 2, 3]),
					$"Ignore pair {optionIds[leftBit]} + {optionIds[rightBit]} missed a boolean combination.");
			}
		}
	}

	private static void AssertOracleStates(
		SettingsIslandOracle oracle,
		IReadOnlyDictionary<string, bool> expectedRootStates,
		IReadOnlyDictionary<string, bool> expectedExtensionStates,
		IReadOnlyDictionary<IgnoreOptionId, bool> expectedIgnoreStates,
		string stepName)
	{
		AssertDictionaryEqual(expectedRootStates, oracle.RootStates, PathComparer.Default, $"{stepName}: root cache");
		AssertDictionaryEqual(
			expectedExtensionStates,
			oracle.ExtensionStates,
			StringComparer.OrdinalIgnoreCase,
			$"{stepName}: extension cache");
		Assert.Equal(expectedIgnoreStates.OrderBy(static pair => pair.Key), oracle.IgnoreStates.OrderBy(static pair => pair.Key));
	}

	private static void AssertVisibleStates(
		IEnumerable<SelectionOptionViewModel> visibleOptions,
		IReadOnlyDictionary<string, bool> expectedStates,
		StringComparer comparer,
		string assertionName)
	{
		var actual = visibleOptions.ToDictionary(
			static option => option.Name,
			static option => option.IsChecked,
			comparer);
		AssertDictionaryEqual(expectedStates, actual, comparer, assertionName);
	}

	private static void AssertDictionaryEqual(
		IReadOnlyDictionary<string, bool> expected,
		IReadOnlyDictionary<string, bool> actual,
		StringComparer comparer,
		string assertionName)
	{
		var expectedPairs = expected.OrderBy(static pair => pair.Key, comparer).ToArray();
		var actualPairs = actual.OrderBy(static pair => pair.Key, comparer).ToArray();
		AssertSequenceEqual(expectedPairs, actualPairs, assertionName);
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
		var selectedIgnoreSet = selectedIgnoreOptions.ToHashSet();
		bool RootSelected(string name) => selectedRoots.Contains(name);
		bool ExtensionSelected(string extension) => selectedExtensions.Contains(extension);

		foreach (var uncheckedRoot in rootOptions.Where(static option => !option.IsChecked))
		{
			Assert.DoesNotContain(relativePaths, relativePath =>
				relativePath.Equals(uncheckedRoot.Name, PathComparison) ||
				relativePath.StartsWith(uncheckedRoot.Name + "/", PathComparison));
		}

		var artifactsIgnored = ignoreRules.IsGitIgnored(
			Path.Combine(rootPath, "artifacts"),
			isDirectory: true,
			"artifacts") || ignoreRules.IsSmartIgnoredDirectory(
			Path.Combine(rootPath, "artifacts"),
			"artifacts");
		AssertPathVisibility(
			relativePaths,
			"artifacts/reports/2026/summary.txt",
			RootSelected("artifacts") && ExtensionSelected(".txt") && !artifactsIgnored);

		var runtimeLogIgnored = ignoreRules.IsGitIgnored(
			Path.Combine(rootPath, "alpha", "runtime.log"),
			isDirectory: false,
			"runtime.log");
		AssertPathVisibility(
			relativePaths,
			"alpha/runtime.log",
			RootSelected("alpha") && ExtensionSelected(".log") && !runtimeLogIgnored);

		var binIgnored = ignoreRules.IsSmartIgnoredDirectory(
			Path.Combine(rootPath, "alpha", "bin"),
			"bin");
		AssertPathVisibility(
			relativePaths,
			"alpha/bin/Debug/net10.0/Alpha.dll",
			RootSelected("alpha") && ExtensionSelected(".dll") && !binIgnored);

		var nodeModulesIgnored = ignoreRules.IsSmartIgnoredDirectory(
			Path.Combine(rootPath, "beta", "node_modules"),
			"node_modules");
		AssertPathVisibility(
			relativePaths,
			"beta/node_modules/pkg/dist/index.js",
			RootSelected("beta") && ExtensionSelected(".js") && !nodeModulesIgnored);

		var dotFoldersIgnored = selectedIgnoreSet.Contains(IgnoreOptionId.DotFolders);
		AssertPathVisibility(
			relativePaths,
			".root-dot/nested/deep/visible.txt",
			RootSelected(".root-dot") && ExtensionSelected(".txt") && !dotFoldersIgnored);
		AssertPathVisibility(
			relativePaths,
			"alpha/.private/nested/secret.cs",
			RootSelected("alpha") && ExtensionSelected(".cs") && !dotFoldersIgnored);
		AssertPathVisibility(
			relativePaths,
			"gamma/.drafts/deep/draft.md",
			RootSelected("gamma") && ExtensionSelected(".md") && !dotFoldersIgnored);

		var emptyFilesIgnored = selectedIgnoreSet.Contains(IgnoreOptionId.EmptyFiles);
		AssertPathVisibility(
			relativePaths,
			"alpha/src/empty.cs",
			RootSelected("alpha") && ExtensionSelected(".cs") && !emptyFilesIgnored);
		AssertPathVisibility(
			relativePaths,
			"gamma/empty.txt",
			RootSelected("gamma") && ExtensionSelected(".txt") && !emptyFilesIgnored);
		AssertPathVisibility(
			relativePaths,
			"empty-root-file.txt",
			ExtensionSelected(".txt") && !emptyFilesIgnored);

		var extensionlessFilesIgnored = selectedIgnoreSet.Contains(IgnoreOptionId.ExtensionlessFiles);
		AssertPathVisibility(relativePaths, "LICENSE", !extensionlessFilesIgnored);
		AssertPathVisibility(
			relativePaths,
			"gamma/README",
			RootSelected("gamma") && !extensionlessFilesIgnored);

		var dotFilesIgnored = selectedIgnoreSet.Contains(IgnoreOptionId.DotFiles);
		AssertPathVisibility(
			relativePaths,
			"gamma/.secret.txt",
			RootSelected("gamma") && ExtensionSelected(".txt") && !dotFilesIgnored);
		AssertPathVisibility(
			relativePaths,
			".env",
			ExtensionSelected(".env") && !dotFilesIgnored);

		var emptyFoldersIgnored = selectedIgnoreSet.Contains(IgnoreOptionId.EmptyFolders);
		AssertPathVisibility(
			relativePaths,
			"delta-empty/level-1/level-2/level-3",
			RootSelected("delta-empty") && !emptyFoldersIgnored);

		var hiddenRootPath = Path.Combine(rootPath, "hidden-root");
		var hiddenRootSupported = File.GetAttributes(hiddenRootPath).HasFlag(FileAttributes.Hidden);
		AssertPathVisibility(
			relativePaths,
			"hidden-root/nested/hidden.txt",
			RootSelected("hidden-root") &&
			ExtensionSelected(".txt") &&
			(!hiddenRootSupported || !selectedIgnoreSet.Contains(IgnoreOptionId.HiddenFolders)));

		var hiddenFilePath = Path.Combine(rootPath, "gamma", "hidden-note.txt");
		var hiddenFileSupported = File.GetAttributes(hiddenFilePath).HasFlag(FileAttributes.Hidden);
		AssertPathVisibility(
			relativePaths,
			"gamma/hidden-note.txt",
			RootSelected("gamma") &&
			ExtensionSelected(".txt") &&
			(!hiddenFileSupported || !selectedIgnoreSet.Contains(IgnoreOptionId.HiddenFiles)));
	}

	private static void AssertPathVisibility(
		IReadOnlySet<string> relativePaths,
		string relativePath,
		bool shouldBeVisible)
	{
		if (shouldBeVisible)
			Assert.Contains(relativePath, relativePaths);
		else
			Assert.DoesNotContain(relativePath, relativePaths);
	}

	private static IReadOnlyList<IgnoreOptionId> ResolveExpectedIgnoreOptionIds(
		bool hiddenFoldersSupported,
		bool hiddenFilesSupported)
	{
		return Enum.GetValues<IgnoreOptionId>()
			.Where(optionId =>
				(optionId != IgnoreOptionId.HiddenFolders || hiddenFoldersSupported) &&
				(optionId != IgnoreOptionId.HiddenFiles || hiddenFilesSupported))
			.ToArray();
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
		public IReadOnlyDictionary<string, bool> RootStates => _rootStates;
		public IReadOnlyDictionary<string, bool> ExtensionStates => _extensionStates;
		public IReadOnlyDictionary<IgnoreOptionId, bool> IgnoreStates => _ignoreStates;

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
					PromoteRefreshRoute(RefreshRoute.Live);
					break;
				case SettingsActionKind.ToggleExtension:
					ToggleVisibleExtension(action.Name!);
					PromoteRefreshRoute(RefreshRoute.Live);
					break;
				case SettingsActionKind.ToggleIgnore:
					ToggleVisibleIgnore(action.IgnoreOptionId!.Value);
					PromoteRefreshRoute(IsLiveIgnoreOption(action.IgnoreOptionId.Value)
						? RefreshRoute.Live
						: RefreshRoute.Full);
					break;
				case SettingsActionKind.SetAllRoots:
					SetAllVisibleRoots(action.IsChecked!.Value);
					PromoteRefreshRoute(RefreshRoute.Live);
					break;
				case SettingsActionKind.SetAllExtensions:
					SetAllVisibleExtensions(action.IsChecked!.Value);
					PromoteRefreshRoute(RefreshRoute.Live);
					break;
				case SettingsActionKind.SetAllIgnore:
					SetAllIgnoreOptions(action.IsChecked!.Value);
					PromoteRefreshRoute(RefreshRoute.Full);
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
			_nextRefreshRoute = RefreshRoute.Live;
			return snapshot;
		}

		private void PromoteRefreshRoute(RefreshRoute requestedRoute)
		{
			if (requestedRoute == RefreshRoute.Full)
				_nextRefreshRoute = RefreshRoute.Full;
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
		private SettingsIslandWorkspace(
			string rootPath,
			bool hiddenFoldersSupported,
			bool hiddenFilesSupported)
		{
			RootPath = rootPath;
			ExpectedIgnoreOptionIds = ResolveExpectedIgnoreOptionIds(
				hiddenFoldersSupported,
				hiddenFilesSupported);
		}

		public string RootPath { get; }
		public IReadOnlyList<IgnoreOptionId> ExpectedIgnoreOptionIds { get; }

		public static SettingsIslandWorkspace Create(bool markHiddenAttributes = true)
		{
			var rootPath = Path.Combine(
				Path.GetTempPath(),
				"DevProjex",
				"SettingsIslandMatrix",
				Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(rootPath);
			Seed(rootPath, markHiddenAttributes);
			return new SettingsIslandWorkspace(
				rootPath,
				HasHiddenAttribute(Path.Combine(rootPath, "hidden-root")),
				HasHiddenAttribute(Path.Combine(rootPath, "gamma", "hidden-note.txt")));
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

		private static void Seed(string rootPath, bool markHiddenAttributes)
		{
			WriteFile(rootPath, ".gitignore", "artifacts/\n*.tmp\n");
			WriteFile(rootPath, "root-evidence.cs", "class RootEvidence {}\n");
			WriteFile(rootPath, "root-evidence.csproj", "<Project />\n");
			WriteFile(rootPath, "root-evidence.dll", "binary evidence\n");
			WriteFile(rootPath, "root-evidence.js", "module.exports = true;\n");
			WriteFile(rootPath, "root-evidence.json", "{}\n");
			WriteFile(rootPath, "root-evidence.log", "log evidence\n");
			WriteFile(rootPath, "root-evidence.md", "# evidence\n");
			WriteFile(rootPath, "root-evidence.ts", "export const evidence = true;\n");
			WriteFile(rootPath, "root-evidence.txt", "text evidence\n");

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
			WriteFile(rootPath, Path.Combine("gamma", ".secret.txt"), "dot file payload\n");
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
			if (markHiddenAttributes)
				TryMarkHidden(hiddenRoot);

			var hiddenFile = Path.Combine(rootPath, "gamma", "hidden-note.txt");
			WriteFile(rootPath, Path.Combine("gamma", "hidden-note.txt"), "hidden file payload\n");
			if (markHiddenAttributes)
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

		private static bool HasHiddenAttribute(string path)
		{
			try
			{
				return File.GetAttributes(path).HasFlag(FileAttributes.Hidden);
			}
			catch
			{
				return false;
			}
		}
	}

	private static StringComparison PathComparison =>
		OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
