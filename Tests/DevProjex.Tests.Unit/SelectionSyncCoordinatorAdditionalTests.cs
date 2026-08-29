using DevProjex.Application.Models;
using DevProjex.Application.Context;
using DevProjex.Application.Secrets;
using DevProjex.Avalonia.Collections;

namespace DevProjex.Tests.Unit;

[Collection("AvaloniaUI")]
public sealed class SelectionSyncCoordinatorAdditionalTests
{
	[Fact]
	public void MomentaryGuiGitModeUsesRadioPresentationAndPersistsTheStickyMode()
	{
		using var project = new TemporaryDirectory();
		project.CreateFolder(".git");
		project.CreateFile(".gitignore", "*.tmp\n");
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(
			viewModel,
			currentPathProvider: () => project.Path,
			availabilityProvider: static (_, _) => new IgnoreOptionsAvailability(
				IncludeGitIgnore: true,
				IncludeSmartIgnore: true,
				IncludeTrackedGitFilesOnly: true));

		coordinator.PopulateIgnoreOptionsForRootSelection([], project.Path);
		Assert.Equal(
			[
				GitFilteringMode.None,
				GitFilteringMode.RespectGitIgnore,
				GitFilteringMode.TrackedFilesOnly,
				GitFilteringMode.Staged,
				GitFilteringMode.Changes
			],
			viewModel.GitFilteringModes.Select(static option => option.Mode));
		Assert.DoesNotContain(
			viewModel.PathIgnoreOptions,
			static option => GitFilteringModeResolver.IsGitFilteringOption(option.Id));

		coordinator.HandleGitFilteringModeChanged(GitFilteringMode.Staged, project.Path);

		Assert.Equal(GitFilteringMode.Staged, coordinator.ActiveGitFilteringMode);
		Assert.Equal(GitFilteringMode.Staged, viewModel.SelectedGitFilteringModeOption?.Mode);
		var persisted = coordinator.GetPersistableSelectedIgnoreOptionIds();
		Assert.Contains(IgnoreOptionId.UseGitIgnore, persisted);
		Assert.DoesNotContain(IgnoreOptionId.TrackedGitFilesOnly, persisted);
		var states = coordinator.SnapshotIgnoreOptionStatesForPersistence();
		Assert.True(states![IgnoreOptionId.UseGitIgnore]);
		Assert.False(states[IgnoreOptionId.TrackedGitFilesOnly]);
	}

	[Fact]
	public void AppliedTrackedMode_RemainsFailClosedWhenItsOptionIsNoLongerVisible()
	{
		const string projectPath = @"C:\Project";
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => projectPath);
		var inventory = new ProjectTreeInventorySnapshot([], false, false);
		var snapshot = new SelectionRefreshSnapshot(
			RootOptions: [],
			ExtensionOptions: [],
			IgnoreOptions: [],
			ExtensionlessEntriesCount: 0,
			HasIgnoreOptionCounts: true,
			IgnoreOptionCounts: IgnoreOptionCounts.Empty,
			ControllerImpactCounts: IgnoreControllerImpactCounts.Empty,
			IgnoreOptionStateCache: new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.TrackedGitFilesOnly] = true
			},
			RootAccessDenied: false,
			HadAccessDenied: false,
			TreeInventory: inventory,
			SelectedIgnoreOptions: new HashSet<IgnoreOptionId>
			{
				IgnoreOptionId.TrackedGitFilesOnly
			});

		ApplySelectionRefreshSnapshot(coordinator, snapshot);
		coordinator.AcceptCurrentSelectionsAsApplied(projectPath, inventory);

		Assert.Contains(
			IgnoreOptionId.TrackedGitFilesOnly,
			coordinator.GetSelectedIgnoreOptionIds());
		var diagnostic = Assert.IsType<ContextDiagnostic>(
			coordinator.GetAppliedGitReadinessDiagnostic(projectPath));
		Assert.Equal(ProjectContextGitReadiness.UnavailableDiagnosticCode, diagnostic.Code);
		Assert.Equal(ContextDiagnosticSeverity.Error, diagnostic.Severity);
	}

	[Fact]
	public void GitReadiness_DistinguishesReadableEmptyIndexFromPartialAndUnavailableScopes()
	{
		var readableEmpty = ProjectContextGitReadiness.Evaluate(
			GitFilteringMode.TrackedFilesOnly,
			discoveredTrackedIndexCount: 1,
			unavailableTrackedIndexCount: 0);
		var partial = ProjectContextGitReadiness.Evaluate(
			GitFilteringMode.TrackedFilesOnly,
			discoveredTrackedIndexCount: 2,
			unavailableTrackedIndexCount: 1);
		var unavailable = ProjectContextGitReadiness.Evaluate(
			GitFilteringMode.TrackedFilesOnly,
			discoveredTrackedIndexCount: 1,
			unavailableTrackedIndexCount: 1);

		Assert.True(readableEmpty.IsReady);
		Assert.Null(readableEmpty.CreateDiagnostic("project"));
		Assert.True(partial.IsReady);
		Assert.Equal(
			ProjectContextGitReadiness.PartialDiagnosticCode,
			partial.CreateDiagnostic("project")?.Code);
		Assert.False(unavailable.IsReady);
		Assert.Equal(
			ProjectContextGitReadiness.UnavailableDiagnosticCode,
			unavailable.CreateDiagnostic("project")?.Code);
	}

	[Fact]
	public void AppliedTrackedMode_PreservesPartialReadinessWarningForOutputGuards()
	{
		const string projectPath = @"C:\Project";
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => projectPath);
		var snapshot = new SelectionRefreshSnapshot(
			RootOptions: [],
			ExtensionOptions: [],
			IgnoreOptions: [],
			ExtensionlessEntriesCount: 0,
			HasIgnoreOptionCounts: true,
			IgnoreOptionCounts: IgnoreOptionCounts.Empty,
			ControllerImpactCounts: IgnoreControllerImpactCounts.Empty,
			IgnoreOptionStateCache: new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.TrackedGitFilesOnly] = true
			},
			RootAccessDenied: false,
			HadAccessDenied: true,
			SelectedIgnoreOptions: new HashSet<IgnoreOptionId>
			{
				IgnoreOptionId.TrackedGitFilesOnly
			});
		var inventory = new ProjectTreeInventorySnapshot(
			[],
			rootAccessDenied: false,
			hadAccessDenied: true,
			discoveredGitTrackedPathIndexes:
			[
				new GitTrackedPathIndex(projectPath, []),
				GitTrackedPathIndex.Unavailable(@"C:\Project\nested")
			]);

		ApplySelectionRefreshSnapshot(coordinator, snapshot);
		coordinator.AcceptCurrentSelectionsAsApplied(projectPath, inventory);

		var diagnostic = Assert.IsType<ContextDiagnostic>(
			coordinator.GetAppliedGitReadinessDiagnostic(projectPath));
		Assert.Equal(ProjectContextGitReadiness.PartialDiagnosticCode, diagnostic.Code);
		Assert.Equal(ContextDiagnosticSeverity.Warning, diagnostic.Severity);
		Assert.True(coordinator.AppliedGitReadiness.IsReady);
	}

	[Fact]
	public void AcceptHideSecretsOnlyChange_PreservesTreeDependentGitReadiness()
	{
		const string projectPath = @"C:\Project";
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => projectPath);
		var snapshot = WithGitMode(
			CreateReversibleSelectionRefreshSnapshot(
				uncheckedIgnoreOption: IgnoreOptionId.HideSecrets),
			useGitIgnore: false,
			trackedOnly: true);
		var inventory = new ProjectTreeInventorySnapshot(
			[],
			rootAccessDenied: false,
			hadAccessDenied: true,
			discoveredGitTrackedPathIndexes:
			[
				new GitTrackedPathIndex(projectPath, []),
				GitTrackedPathIndex.Unavailable(@"C:\Project\nested")
			]);

		ApplySelectionRefreshSnapshot(coordinator, snapshot);
		HookAllOptionListeners(coordinator, viewModel);
		coordinator.AcceptCurrentSelectionsAsApplied(projectPath, inventory);
		var appliedGitReadiness = coordinator.AppliedGitReadiness;

		Assert.True(coordinator.ApplyHideSecretsOverride(true));
		Assert.True(viewModel.HasPendingFilterSettingsChanges);
		Assert.True(coordinator.TryAcceptHideSecretsOnlyChangeAsApplied(projectPath));

		Assert.False(viewModel.HasPendingFilterSettingsChanges);
		Assert.Same(appliedGitReadiness, coordinator.AppliedGitReadiness);
		Assert.False(coordinator.TryAcceptHideSecretsOnlyChangeAsApplied(projectPath));
	}

	[Fact]
	public void AcceptHideSecretsOnlyChange_RejectsAdditionalTransformationChange()
	{
		const string projectPath = @"C:\Project";
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => projectPath);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(
				uncheckedIgnoreOption: IgnoreOptionId.HideSecrets));
		HookAllOptionListeners(coordinator, viewModel);
		coordinator.AcceptCurrentSelectionsAsApplied(projectPath);

		Assert.True(coordinator.ApplyHideSecretsOverride(true));
		Assert.True(coordinator.ApplyCompressCodeOverride(false));

		Assert.False(coordinator.TryAcceptHideSecretsOnlyChangeAsApplied(projectPath));
		Assert.True(viewModel.HasPendingFilterSettingsChanges);
	}

	[Fact]
	public void PendingApplyState_IgnoresMasterCheckboxChangesWithoutAnEffectiveSelectionChange()
	{
		const string projectPath = @"C:\Project";
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => projectPath);
		coordinator.AcceptCurrentSelectionsAsApplied(projectPath);

		coordinator.HandleIgnoreAllChanged(isChecked: false, currentPath: null);

		Assert.False(viewModel.HasPendingFilterSettingsChanges);
	}

	[Fact]
	public void PendingApplyState_DynamicOptionProjectionReturningToBaselineStopsAttention()
	{
		const string projectPath = @"C:\Project";
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".md", true));
		using var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => projectPath);
		coordinator.UpdateExtensionsSelectionCache();
		coordinator.AcceptCurrentSelectionsAsApplied(projectPath);

		coordinator.ApplyExtensionScan([".cs"]);
		Assert.True(viewModel.HasPendingFilterSettingsChanges);

		coordinator.ApplyExtensionScan([".cs", ".md"]);
		Assert.False(viewModel.HasPendingFilterSettingsChanges);
	}

	[Fact]
	public void HandleExtensionsAllChanged_ChecksAllExtensionOptions()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", false));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".md", false));

		var coordinator = CreateCoordinator(viewModel);

		coordinator.HandleExtensionsAllChanged(true);

		Assert.True(viewModel.AllExtensionsChecked);
		Assert.All(viewModel.Extensions, option => Assert.True(option.IsChecked));
	}

	[Fact]
	public void HandleIgnoreAllChanged_ChecksAllIgnoreOptions()
	{
		var viewModel = CreateViewModel();
		viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.HiddenFolders, "hidden folders", false));
		viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.DotFolders, "dot folders", false));

		var coordinator = CreateCoordinator(viewModel);

		coordinator.HandleIgnoreAllChanged(true, currentPath: null);

		Assert.True(viewModel.AllIgnoreChecked);
		Assert.All(viewModel.IgnoreOptions, option => Assert.True(option.IsChecked));
	}

	[Fact]
	public void HandleIgnoreAllChanged_OffOnCyclePreservesTrackedGitFilteringMode()
	{
		const string projectPath = @"C:\Project";
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(
			viewModel,
			currentPathProvider: () => projectPath,
			availabilityProvider: (_, _) => new IgnoreOptionsAvailability(
				IncludeGitIgnore: true,
				IncludeSmartIgnore: true,
				IncludeTrackedGitFilesOnly: true));
		coordinator.ApplyProjectProfileSelections(
			projectPath,
			new ProjectSelectionProfile(
				SelectedRootFolders: [],
				SelectedExtensions: [],
				SelectedIgnoreOptions:
				[
					IgnoreOptionId.TrackedGitFilesOnly,
					IgnoreOptionId.SmartIgnore
				],
				IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.UseGitIgnore] = false,
					[IgnoreOptionId.TrackedGitFilesOnly] = true,
					[IgnoreOptionId.SmartIgnore] = true
				}));
		coordinator.PopulateIgnoreOptionsForRootSelection([], projectPath);
		coordinator.ConsumePreparedSelectionForPath(projectPath);

		Assert.False(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.UseGitIgnore).IsChecked);
		Assert.True(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.TrackedGitFilesOnly).IsChecked);

		coordinator.HandleIgnoreAllChanged(isChecked: false, currentPath: null);

		Assert.All(viewModel.IgnoreOptions, static option => Assert.False(option.IsChecked));

		coordinator.HandleIgnoreAllChanged(isChecked: true, currentPath: null);

		Assert.False(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.UseGitIgnore).IsChecked);
		Assert.True(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.TrackedGitFilesOnly).IsChecked);
		Assert.True(viewModel.AllIgnoreChecked);
	}

	[Fact]
	public void GitFilteringCheckboxes_AllowActiveModeToBeClearedAndKeepSmartIgnoreIndependent()
	{
		const string projectPath = @"C:\Project";
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(
			viewModel,
			currentPathProvider: () => null,
			availabilityProvider: (_, _) => new IgnoreOptionsAvailability(
				IncludeGitIgnore: true,
				IncludeSmartIgnore: true,
				IncludeTrackedGitFilesOnly: true));
		coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);
		coordinator.ApplyProjectProfileSelections(
			projectPath,
			new ProjectSelectionProfile(
				SelectedRootFolders: [],
				SelectedExtensions: [],
				SelectedIgnoreOptions:
				[
					IgnoreOptionId.UseGitIgnore,
					IgnoreOptionId.SmartIgnore
				]));
		coordinator.PopulateIgnoreOptionsForRootSelection([], projectPath);
		coordinator.ConsumePreparedSelectionForPath(projectPath);

		var useGitIgnore = viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.UseGitIgnore);
		var trackedOnly = viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.TrackedGitFilesOnly);
		var smartIgnore = viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.SmartIgnore);
		Assert.True(useGitIgnore.IsChecked);
		Assert.False(trackedOnly.IsChecked);
		Assert.True(smartIgnore.IsChecked);

		trackedOnly.IsChecked = true;
		Assert.False(useGitIgnore.IsChecked);
		Assert.True(trackedOnly.IsChecked);

		// An active Git checkbox may be cleared. None is a valid Git-filtering mode;
		// it must not be normalized back to the other checkbox or affect Smart Ignore.
		trackedOnly.IsChecked = false;
		Assert.False(useGitIgnore.IsChecked);
		Assert.False(trackedOnly.IsChecked);
		Assert.True(smartIgnore.IsChecked);
		Assert.Equal(
			[IgnoreOptionId.SmartIgnore],
			coordinator.GetSelectedIgnoreOptionIds());
		var persistedStates = Assert.IsAssignableFrom<IReadOnlyDictionary<IgnoreOptionId, bool>>(
			coordinator.SnapshotIgnoreOptionStatesForPersistence());
		Assert.False(persistedStates[IgnoreOptionId.UseGitIgnore]);
		Assert.False(persistedStates[IgnoreOptionId.TrackedGitFilesOnly]);
		Assert.True(persistedStates[IgnoreOptionId.SmartIgnore]);

		coordinator.HandleIgnoreAllChanged(isChecked: false, currentPath: null);

		Assert.False(useGitIgnore.IsChecked);
		Assert.False(trackedOnly.IsChecked);
		Assert.Empty(coordinator.GetSelectedIgnoreOptionIds());
	}

	[Fact]
	public void IgnoreReset_UnsubscribesRemovedItemsAndDoesNotDuplicateRetainedSubscriptions()
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel);
		coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);
		var options = Assert.IsType<ResettableObservableCollection<IgnoreOptionViewModel>>(viewModel.IgnoreOptions);
		var removed = new IgnoreOptionViewModel(IgnoreOptionId.DotFolders, "removed", true);
		var retained = new IgnoreOptionViewModel(IgnoreOptionId.SmartIgnore, "retained", true);

		options.ReplaceAll([removed]);
		options.ReplaceAll([retained]);
		options.ReplaceAll([retained]);
		options.ReplaceAll([retained]);

		Assert.Equal(0, GetEventSubscriberCount(removed, nameof(IgnoreOptionViewModel.CheckedChanged)));
		Assert.Equal(1, GetEventSubscriberCount(retained, nameof(IgnoreOptionViewModel.CheckedChanged)));
	}

	[Fact]
	public void RelabelIgnoreOptions_UpdatesOnlyPresentationWithoutAvailabilityScanOrStateMutation()
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		var viewModel = new MainWindowViewModel(localization, new HelpContentProvider());
		viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(
			IgnoreOptionId.SmartIgnore,
			"Smart ignore",
			isChecked: false));
		var availabilityCalls = 0;
		using var coordinator = new SelectionSyncCoordinator(
			viewModel,
			new ScanOptionsUseCase(new StubFileSystemScanner()),
			new FilterOptionSelectionService(),
			new IgnoreOptionsService(localization),
			(_, _, _) => new IgnoreRules(
				false,
				false,
				false,
				false,
				new HashSet<string>(),
				new HashSet<string>()),
			(_, _) =>
			{
				availabilityCalls++;
				return new IgnoreOptionsAvailability(
					IncludeGitIgnore: false,
					IncludeSmartIgnore: true);
			},
			_ => false,
			() => @"C:\Project");
		var revisionBefore = coordinator.CurrentSelectionRevision;

		localization.SetLanguage(AppLanguage.Ru);
		coordinator.RelabelIgnoreOptions(showAdvancedCounts: true);

		var option = Assert.Single(viewModel.IgnoreOptions);
		Assert.Equal("Умное исключение", option.Label);
		Assert.False(option.IsChecked);
		Assert.Equal(revisionBefore, coordinator.CurrentSelectionRevision);
		Assert.Equal(0, availabilityCalls);
	}

	[AvaloniaFact]
	public void RelabelIgnoreOptions_UsesAppliedRedactionStateInsteadOfCheckboxDrafts()
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel);
		ApplySelectionRefreshSnapshot(coordinator, CreateReversibleSelectionRefreshSnapshot());
		var hideSecrets = Assert.Single(
			viewModel.IgnoreOptions,
			static option => option.Id == IgnoreOptionId.HideSecrets);
		var hidePrivateData = Assert.Single(
			viewModel.IgnoreOptions,
			static option => option.Id == IgnoreOptionId.HidePrivateData);
		hideSecrets.IsChecked = false;
		hidePrivateData.IsChecked = true;

		coordinator.RelabelIgnoreOptions(
			showAdvancedCounts: true,
			secretRedactionsCount: 2,
			secretScanState: SecretScanState.Completed,
			secretMatchesCount: 3,
			privateDataRedactionsCount: 5,
			privateDataMatchesCount: 7,
			hideSecretsApplied: true,
			hidePrivateDataApplied: false);

		Assert.Equal("Hide secrets (3/2)", hideSecrets.Label);
		Assert.Equal("Hide private data", hidePrivateData.Label);

		hideSecrets.IsChecked = true;
		hidePrivateData.IsChecked = false;
		coordinator.RelabelIgnoreOptions(
			showAdvancedCounts: true,
			secretRedactionsCount: 2,
			secretScanState: SecretScanState.Completed,
			secretMatchesCount: 3,
			privateDataRedactionsCount: 5,
			privateDataMatchesCount: 7,
			hideSecretsApplied: false,
			hidePrivateDataApplied: true);

		Assert.Equal("Hide secrets", hideSecrets.Label);
		Assert.Equal("Hide private data (7/5)", hidePrivateData.Label);
	}

	[AvaloniaFact]
	public async Task ProjectCheckpoint_Restore_InvalidatesLateSelectionRefresh()
	{
		const string path = @"C:\Project";
		using var scanStarted = new ManualResetEventSlim();
		using var releaseScan = new ManualResetEventSlim();
		var scanner = new CountingRootSelectionSnapshotScanner
		{
			RootSelectionSnapshot = CreateDriftedRootSelectionScanData(),
			BeforeRootSelectionSnapshot = _ =>
			{
				scanStarted.Set();
				if (!releaseScan.Wait(TimeSpan.FromSeconds(3)))
					throw new TimeoutException("The controlled selection scan was not released.");
			}
		};
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		GetPrivateSession(coordinator).LastLoadedPath = path;
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));
		HookAllOptionListeners(coordinator, viewModel);
		var checkpoint = coordinator.CaptureProjectCheckpoint();

		try
		{
			viewModel.IgnoreOptions.Single(
				static option => option.Id == IgnoreOptionId.HiddenFiles).IsChecked = false;
			Assert.True(await Task.Run(
				() => scanStarted.Wait(TimeSpan.FromSeconds(2)),
				TestContext.Current.CancellationToken));

			coordinator.RestoreProjectCheckpoint(checkpoint);
			releaseScan.Set();
			await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

			Assert.True(viewModel.IgnoreOptions.Single(
				static option => option.Id == IgnoreOptionId.HiddenFiles).IsChecked);
			Assert.Equal(433, GetPrivateIgnoreOptionCounts(coordinator).EmptyFolders);
			Assert.DoesNotContain(viewModel.Extensions, static option => option.Name == ".txt");
		}
		finally
		{
			releaseScan.Set();
		}
	}

	[AvaloniaFact]
	public async Task GitScopeModesRemainUnavailableWhenRepositoryExistsButGitCliDoesNot()
	{
		var viewModel = CreateViewModel();
		var availabilityChecks = 0;
		using var coordinator = CreateCoordinator(
			viewModel,
			currentPathProvider: () => @"C:\Project",
			availabilityProvider: static (_, _) => new IgnoreOptionsAvailability(
				IncludeGitIgnore: true,
				IncludeSmartIgnore: true,
				IncludeTrackedGitFilesOnly: true),
			gitAvailabilityResolver: _ =>
			{
				availabilityChecks++;
				return Task.FromResult(false);
			});

		await coordinator.EnsureGitCliAvailabilityAsync(TestContext.Current.CancellationToken);
		await coordinator.EnsureGitCliAvailabilityAsync(TestContext.Current.CancellationToken);
		coordinator.PopulateIgnoreOptionsForRootSelection([], @"C:\Project");

		Assert.Equal(1, availabilityChecks);
		Assert.Equal(
			[GitFilteringMode.None, GitFilteringMode.RespectGitIgnore],
			viewModel.GitFilteringModes.Select(static option => option.Mode));
	}

	[AvaloniaFact]
	public void ProjectCheckpoint_Restore_CompleteStateAfterIncompleteScan_AllowsPersistenceAgain()
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => path);
		GetPrivateSession(coordinator).LastLoadedPath = path;
		var snapshot = CreateReversibleSelectionRefreshSnapshot();
		ApplySelectionRefreshSnapshot(coordinator, snapshot);
		var completeCheckpoint = coordinator.CaptureProjectCheckpoint();

		ApplySelectionRefreshSnapshotWithCompleteness(
			coordinator,
			snapshot,
			cacheIsComplete: false);
		Assert.False(coordinator.IsSelectionStateCompleteForPersistence);

		coordinator.RestoreProjectCheckpoint(completeCheckpoint);

		Assert.True(coordinator.IsSelectionStateCompleteForPersistence);
	}

	[AvaloniaFact]
	public void ProjectCheckpoint_Restore_IncompleteStateAfterProjectSwitch_BlocksPersistenceAgain()
	{
		const string projectA = @"C:\ProjectA";
		const string projectB = @"C:\ProjectB";
		var currentPath = projectA;
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => currentPath);
		var session = GetPrivateSession(coordinator);
		session.LastLoadedPath = projectA;
		var snapshot = CreateReversibleSelectionRefreshSnapshot();
		ApplySelectionRefreshSnapshotWithCompleteness(
			coordinator,
			snapshot,
			cacheIsComplete: false);
		var incompleteCheckpoint = coordinator.CaptureProjectCheckpoint();
		Assert.False(coordinator.IsSelectionStateCompleteForPersistence);

		currentPath = projectB;
		session.LastLoadedPath = projectB;
		ApplySelectionRefreshSnapshot(coordinator, snapshot);
		Assert.True(coordinator.IsSelectionStateCompleteForPersistence);

		currentPath = projectA;
		coordinator.RestoreProjectCheckpoint(incompleteCheckpoint);

		Assert.False(coordinator.IsSelectionStateCompleteForPersistence);
	}

	[AvaloniaFact]
	public void SelectionRollback_RestoresIncompletePersistenceBoundary()
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => path);
		GetPrivateSession(coordinator).LastLoadedPath = path;
		var snapshot = CreateReversibleSelectionRefreshSnapshot();
		ApplySelectionRefreshSnapshotWithCompleteness(
			coordinator,
			snapshot,
			cacheIsComplete: false);
		var incompleteSnapshot = GetStableSelectionSnapshot(coordinator);

		ApplySelectionRefreshSnapshot(coordinator, snapshot);
		Assert.True(coordinator.IsSelectionStateCompleteForPersistence);

		RestoreStableSelectionSnapshot(coordinator, incompleteSnapshot);

		Assert.False(coordinator.IsSelectionStateCompleteForPersistence);
	}

	[AvaloniaFact]
	public async Task ProjectCheckpoint_Restore_DirtyCheckpoint_RequeuesConvergenceOnFreshGate()
	{
		const string path = @"C:\Project";
		using var scanStarted = new ManualResetEventSlim();
		using var releaseStaleScan = new ManualResetEventSlim();
		var liveScanCount = 0;
		var scanner = new CountingRootSelectionSnapshotScanner
		{
			BeforeRootSelectionSnapshot = _ =>
			{
				if (Interlocked.Increment(ref liveScanCount) != 1)
					return;

				scanStarted.Set();
				releaseStaleScan.Wait(TestContext.Current.CancellationToken);
			}
		};
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		GetPrivateSession(coordinator).LastLoadedPath = path;
		ApplySelectionRefreshSnapshot(coordinator, CreateReversibleSelectionRefreshSnapshot());
		HookAllOptionListeners(coordinator, viewModel);

		try
		{
			viewModel.IgnoreOptions.Single(
				static option => option.Id == IgnoreOptionId.HiddenFiles).IsChecked = false;
			Assert.True(scanStarted.Wait(
				TimeSpan.FromSeconds(2),
				TestContext.Current.CancellationToken));
			var dirtyCheckpoint = coordinator.CaptureProjectCheckpoint();

			coordinator.RestoreProjectCheckpoint(dirtyCheckpoint);
			await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken)
				.WaitAsync(TimeSpan.FromSeconds(2));

			Assert.False(IsSelectionRefreshDirty(coordinator));
			Assert.True(scanner.TotalScanCount > 1);
		}
		finally
		{
			releaseStaleScan.Set();
		}
	}

	[AvaloniaFact]
	public async Task ProjectCheckpoint_Restore_DetachesLateFaultFromRestoredProjectIdleBoundary()
	{
		const string path = @"C:\Project";
		using var scanStarted = new ManualResetEventSlim();
		using var releaseScan = new ManualResetEventSlim();
		using var staleFaultRaised = new ManualResetEventSlim();
		var scanInvocationCount = 0;
		var scanner = new CountingRootSelectionSnapshotScanner
		{
			BeforeRootSelectionSnapshot = _ =>
			{
				if (Interlocked.Increment(ref scanInvocationCount) != 1)
					return;

				scanStarted.Set();
				releaseScan.Wait(TestContext.Current.CancellationToken);
				staleFaultRaised.Set();
				throw new InvalidOperationException("stale project refresh failed after rollback");
			}
		};
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		GetPrivateSession(coordinator).LastLoadedPath = path;
		ApplySelectionRefreshSnapshot(coordinator, CreateReversibleSelectionRefreshSnapshot());
		HookAllOptionListeners(coordinator, viewModel);
		var checkpoint = coordinator.CaptureProjectCheckpoint();

		viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.HiddenFiles).IsChecked = false;
		Assert.True(await Task.Run(
			() => scanStarted.Wait(TimeSpan.FromSeconds(2)),
			TestContext.Current.CancellationToken));

		coordinator.RestoreProjectCheckpoint(checkpoint);
		typeof(SelectionSyncCoordinator)
			.GetField("_stableSelectionSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
			.SetValue(coordinator, null);
		typeof(SelectionSyncCoordinator)
			.GetField("_reversibleSelectionSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
			.SetValue(coordinator, null);
		var restoredProjectRefresh = coordinator.UpdateLiveOptionsForProjectScopeAsync(
			path,
			TestContext.Current.CancellationToken);
		await restoredProjectRefresh.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Equal(2, Volatile.Read(ref scanInvocationCount));

		releaseScan.Set();
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);
		Assert.True(staleFaultRaised.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

		Assert.True(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.HiddenFiles).IsChecked);
	}

	[Fact]
	public async Task UpdateLiveOptionsForProjectScopeIfDirtyAsync_AfterSnapshotApply_DoesNotRunRedundantSnapshot()
	{
		var viewModel = CreateViewModel();
		var path = @"C:\Project";
		var scanner = new CountingRootSelectionSnapshotScanner();
		var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		MarkSelectionRefreshDirty(coordinator);

		ApplySelectionRefreshSnapshot(
			coordinator,
			new SelectionRefreshSnapshot(
				RootOptions: [new SelectionOption("src", true)],
				ExtensionOptions: [new SelectionOption(".cs", true)],
				IgnoreOptions: [],
				ExtensionlessEntriesCount: 0,
				HasIgnoreOptionCounts: true,
				IgnoreOptionCounts: IgnoreOptionCounts.Empty,
				ControllerImpactCounts: IgnoreControllerImpactCounts.Empty,
				IgnoreOptionStateCache: new Dictionary<IgnoreOptionId, bool>(),
				RootAccessDenied: false,
				HadAccessDenied: false));

		await coordinator.UpdateLiveOptionsForProjectScopeIfDirtyAsync(path, cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(0, scanner.RootSelectionSnapshotCount);
	}

	[Fact]
	public void ApplySelectionRefreshSnapshot_InvalidatesOlderStandaloneIgnoreAvailabilityRefreshes()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		var beforeVersion = GetPrivateIgnoreOptionsVersion(coordinator);

		ApplySelectionRefreshSnapshot(
			coordinator,
			new SelectionRefreshSnapshot(
				RootOptions: [new SelectionOption("src", true)],
				ExtensionOptions: [new SelectionOption(".cs", true)],
				IgnoreOptions:
				[
					new ResolvedIgnoreOptionState(IgnoreOptionId.UseGitIgnore, "Use .gitignore", true, true),
					new ResolvedIgnoreOptionState(IgnoreOptionId.DotFolders, "dot folders (100)", true, true)
				],
				ExtensionlessEntriesCount: 0,
				HasIgnoreOptionCounts: true,
				IgnoreOptionCounts: new IgnoreOptionCounts(DotFolders: 100),
				ControllerImpactCounts: new IgnoreControllerImpactCounts(GitIgnore: 150),
				IgnoreOptionStateCache: new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.UseGitIgnore] = true,
					[IgnoreOptionId.DotFolders] = true
				},
				RootAccessDenied: false,
				HadAccessDenied: false));

		var afterVersion = GetPrivateIgnoreOptionsVersion(coordinator);

		// Standalone async availability refreshes compare this version before mutating
		// the UI. Count-driven snapshots must advance it to preserve one authoritative
		// ignore state after live/full refreshes.
		Assert.True(afterVersion > beforeVersion);
		Assert.Contains(viewModel.IgnoreOptions, option =>
			option.Id == IgnoreOptionId.DotFolders &&
			option.Label == "dot folders (100)" &&
			option.IsChecked);
	}

	[Fact]
	public void PopulateExtensionsForRootSelectionAsync_DoesNotDropCachedSelections()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", false));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".md", true));

		var coordinator = CreateCoordinator(viewModel);
		coordinator.UpdateExtensionsSelectionCache();

		coordinator.ApplyExtensionScan([".cs"]);
		coordinator.ApplyExtensionScan([".cs", ".md"]);

		var md = viewModel.Extensions.Single(option => option.Name == ".md");
		Assert.True(md.IsChecked);
	}

	[Fact]
	public void PopulateExtensionsForRootSelectionAsync_EmptyRoots_DoesNotClearCachedSelections()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", false));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".md", true));

		var coordinator = CreateCoordinator(viewModel);
		coordinator.UpdateExtensionsSelectionCache();

		coordinator.ApplyExtensionScan([]);
		coordinator.ApplyExtensionScan([".cs", ".md"]);

		var md = viewModel.Extensions.Single(option => option.Name == ".md");
		Assert.True(md.IsChecked);
	}

	[Fact]
	public void SnapshotExtensionOptionStatesForPersistence_KeepsHiddenManualStates()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".log", false));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));

		var coordinator = CreateCoordinator(viewModel);
		coordinator.UpdateExtensionsSelectionCache();

		coordinator.ApplyExtensionScan([".cs"]);

		var states = coordinator.SnapshotExtensionOptionStatesForPersistence();

		Assert.NotNull(states);
		Assert.True(states!.TryGetValue(".log", out var logChecked));
		Assert.False(logChecked);
		Assert.True(states.TryGetValue(".cs", out var csChecked));
		Assert.True(csChecked);
	}

	[Fact]
	public void ApplyExtensionScan_UpdatesExtensionsFromScanResults()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".old", true));

		var coordinator = CreateCoordinator(viewModel);

		coordinator.ApplyExtensionScan([".cs", ".md", ".root"]);

		var names = viewModel.Extensions.Select(option => option.Name).ToList();
		Assert.Contains(".root", names);
		Assert.Contains(".cs", names);
		Assert.Contains(".md", names);
		Assert.DoesNotContain(".old", names);
	}

	[Fact]
	public void ApplyExtensionScan_PreservesCachedExtensionSelections()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".md", true));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".txt", false));
		viewModel.AllExtensionsChecked = false;

		var coordinator = CreateCoordinator(viewModel);
		coordinator.UpdateExtensionsSelectionCache();

		coordinator.ApplyExtensionScan([".md", ".txt"]);

		var md = viewModel.Extensions.Single(option => option.Name == ".md");
		var txt = viewModel.Extensions.Single(option => option.Name == ".txt");
		Assert.True(md.IsChecked);
		Assert.False(txt.IsChecked);
	}

	[Fact]
	public void ApplyExtensionScan_NewExtensionDefaultsCheckedWhileKnownUncheckedStaysUnchecked()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", false));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".md", true));
		viewModel.AllExtensionsChecked = false;

		var coordinator = CreateCoordinator(viewModel);
		coordinator.UpdateExtensionsSelectionCache();

		coordinator.ApplyExtensionScan([".cs", ".md", ".json"]);

		Assert.False(viewModel.Extensions.Single(option => option.Name == ".cs").IsChecked);
		Assert.True(viewModel.Extensions.Single(option => option.Name == ".md").IsChecked);
		Assert.True(viewModel.Extensions.Single(option => option.Name == ".json").IsChecked);
		Assert.False(viewModel.AllExtensionsChecked);
	}

	[AvaloniaFact]
	public void ExtensionSingleToggle_UpdatesKnownStateAndDerivedAllFlag()
	{
		const string projectPath = @"C:\Project";
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".md", true));
		using var coordinator = CreateCoordinator(viewModel);
		coordinator.ApplyProjectProfileSelections(
			projectPath,
			new ProjectSelectionProfile(
				SelectedRootFolders: [],
				SelectedExtensions: [".cs", ".md"],
				SelectedIgnoreOptions: [],
				ExtensionStates: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
				{
					[".cs"] = true,
					[".md"] = true,
					[".hidden"] = false
				}));
		coordinator.ConsumePreparedSelectionForPath(projectPath);
		coordinator.HookOptionListeners(viewModel.Extensions);
		var option = viewModel.Extensions.Single(static candidate => candidate.Name == ".cs");

		option.IsChecked = false;

		Assert.False(viewModel.AllExtensionsChecked);
		var uncheckedStates = coordinator.SnapshotExtensionOptionStatesForPersistence();
		Assert.NotNull(uncheckedStates);
		Assert.False(uncheckedStates![".cs"]);
		Assert.False(uncheckedStates[".hidden"]);

		option.IsChecked = true;

		Assert.True(viewModel.AllExtensionsChecked);
		Assert.True(coordinator.SnapshotExtensionOptionStatesForPersistence()![".cs"]);
		Assert.False(coordinator.SnapshotExtensionOptionStatesForPersistence()![".hidden"]);
	}

	[AvaloniaFact]
	public void ExtensionAggregate_BulkAndCheckpointRestore_RebuildVisibleUncheckedCount()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".md", true));
		using var coordinator = CreateCoordinator(viewModel);
		coordinator.HookOptionListeners(viewModel.Extensions);
		coordinator.UpdateExtensionsSelectionCache();

		coordinator.HandleExtensionsAllChanged(isChecked: false);
		var checkpoint = coordinator.CaptureProjectCheckpoint();

		Assert.False(viewModel.AllExtensionsChecked);

		coordinator.HandleExtensionsAllChanged(isChecked: true);
		Assert.True(viewModel.AllExtensionsChecked);

		coordinator.RestoreProjectCheckpoint(checkpoint);
		Assert.False(viewModel.AllExtensionsChecked);

		viewModel.Extensions.Single(static option => option.Name == ".cs").IsChecked = true;
		Assert.False(viewModel.AllExtensionsChecked);
		viewModel.Extensions.Single(static option => option.Name == ".md").IsChecked = true;
		Assert.True(viewModel.AllExtensionsChecked);
	}

	[Fact]
	public void ApplyExtensionScan_WhenCacheNotInitialized_RestoresDefaultCheckedState()
	{
		var viewModel = CreateViewModel();
		viewModel.AllExtensionsChecked = false;
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
		viewModel.Extensions.Add(new SelectionOptionViewModel(".md", false));

		var coordinator = CreateCoordinator(viewModel);
		coordinator.ResetProjectProfileSelections("C:\\ProjectB");

		coordinator.ApplyExtensionScan([".cs", ".json"]);

		Assert.All(viewModel.Extensions, option => Assert.True(option.IsChecked));
	}

	[Fact]
	public void ApplyExtensionScan_EmptyScan_ClearsExtensionsAndAllFlag()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
		viewModel.AllExtensionsChecked = true;

		var coordinator = CreateCoordinator(viewModel);

		coordinator.ApplyExtensionScan([]);

		Assert.Empty(viewModel.Extensions);
		Assert.False(viewModel.AllExtensionsChecked);
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_EmptyRoots_StillPopulatesIgnoreOptions()
	{
		// Emulate a root-level extensionless entry so the coordinator has a real
		// count-driven reason to keep one advanced option visible.
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		ApplyIgnoreCounts(coordinator, new IgnoreOptionCounts(ExtensionlessFiles: 1));

		coordinator.PopulateIgnoreOptionsForRootSelection([], "C:\\ProjectA");

		Assert.Contains(viewModel.IgnoreOptions, option => option.Id == IgnoreOptionId.ExtensionlessFiles);
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_PreservesIgnoreSelections()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		ApplyIgnoreCounts(coordinator, new IgnoreOptionCounts(HiddenFolders: 1, HiddenFiles: 1));
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], "C:\\ProjectA");
		coordinator.HandleIgnoreAllChanged(false, currentPath: null);
		viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.HiddenFolders).IsChecked = true;
		viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.HiddenFiles).IsChecked = false;
		coordinator.UpdateIgnoreSelectionCache();

		ApplyIgnoreCounts(coordinator, new IgnoreOptionCounts(HiddenFolders: 1, HiddenFiles: 1));
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], "C:\\ProjectA");

		var hiddenFolders = viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.HiddenFolders);
		var hiddenFiles = viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.HiddenFiles);
		Assert.True(hiddenFolders.IsChecked);
		Assert.False(hiddenFiles.IsChecked);
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_WhenGitIgnoreExists_AddsUseGitIgnoreOption()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), $"devprojex-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempRoot);
		File.WriteAllText(Path.Combine(tempRoot, ".gitignore"), "bin/");
		try
		{
			var viewModel = CreateViewModel();
			var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
			var scanner = new StubFileSystemScanner();
			var scanOptions = new ScanOptionsUseCase(LegacyWorkspaceScannerTestAdapter.Adapt(scanner));
			var coordinator = new SelectionSyncCoordinator(
				viewModel,
				scanOptions,
				new FilterOptionSelectionService(),
				new IgnoreOptionsService(localization),
				_ => new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
				_ => false,
				() => tempRoot);

			coordinator.PopulateIgnoreOptionsForRootSelection(["src"], tempRoot);

			Assert.Contains(viewModel.IgnoreOptions, option => option.Id == IgnoreOptionId.UseGitIgnore);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_WhenGitIgnoreMissing_DoesNotAddUseGitIgnoreOption()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), $"devprojex-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempRoot);
		try
		{
			var viewModel = CreateViewModel();
			var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
			var scanner = new StubFileSystemScanner();
			var scanOptions = new ScanOptionsUseCase(LegacyWorkspaceScannerTestAdapter.Adapt(scanner));
			var coordinator = new SelectionSyncCoordinator(
				viewModel,
				scanOptions,
				new FilterOptionSelectionService(),
				new IgnoreOptionsService(localization),
				_ => new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
				_ => false,
				() => tempRoot);

			coordinator.PopulateIgnoreOptionsForRootSelection(["src"], tempRoot);

			Assert.DoesNotContain(viewModel.IgnoreOptions, option => option.Id == IgnoreOptionId.UseGitIgnore);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void ApplyProjectProfileSelections_PreparedReadPreservesUnavailableSelections()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: [IgnoreOptionId.DotFiles, IgnoreOptionId.HiddenFiles]);

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		var selected = coordinator.GetSelectedIgnoreOptionIds();
		var persistedStates = coordinator.SnapshotIgnoreOptionStatesForPersistence();

		Assert.Equal(
			new[] { IgnoreOptionId.DotFiles, IgnoreOptionId.HiddenFiles }.Order(),
			selected.Order());
		Assert.NotNull(persistedStates);
		Assert.True(persistedStates![IgnoreOptionId.DotFiles]);
		Assert.True(persistedStates[IgnoreOptionId.HiddenFiles]);
	}

	[Fact]
	public void ApplyProjectProfileSelections_PreservesExtensionSelectionInNextScan()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [".md"],
			SelectedIgnoreOptions: []);

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		coordinator.ApplyExtensionScan([".cs", ".md"]);

		Assert.False(viewModel.Extensions.Single(option => option.Name == ".cs").IsChecked);
		Assert.True(viewModel.Extensions.Single(option => option.Name == ".md").IsChecked);
	}

	[Fact]
	public void ApplyProjectProfileSelections_PreventsAllExtensionsOverride()
	{
		var viewModel = CreateViewModel();
		// Intentionally keep default AllExtensionsChecked=true to verify fix.
		var coordinator = CreateCoordinator(viewModel);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [".md"],
			SelectedIgnoreOptions: []);

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		coordinator.ApplyExtensionScan([".cs", ".md", ".json"]);

		Assert.False(viewModel.AllExtensionsChecked);
		Assert.False(viewModel.Extensions.Single(option => option.Name == ".cs").IsChecked);
		Assert.True(viewModel.Extensions.Single(option => option.Name == ".md").IsChecked);
		Assert.False(viewModel.Extensions.Single(option => option.Name == ".json").IsChecked);
	}

	[Fact]
	public void ApplyProjectProfileSelections_MissingSavedExtensions_FallsBackToDefaultsForAvailable()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [".removed-ext"],
			SelectedIgnoreOptions: []);

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		coordinator.ApplyExtensionScan([".cs", ".json"]);

		Assert.True(viewModel.Extensions.Single(option => option.Name == ".cs").IsChecked);
		Assert.True(viewModel.Extensions.Single(option => option.Name == ".json").IsChecked);
		Assert.True(viewModel.AllExtensionsChecked);
	}

	[Fact]
	public void ApplyProjectProfileSelections_EmptySavedExtensions_StillKeepsAllUnchecked()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: []);

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		coordinator.ApplyExtensionScan([".cs", ".json"]);

		Assert.False(viewModel.Extensions.Single(option => option.Name == ".cs").IsChecked);
		Assert.False(viewModel.Extensions.Single(option => option.Name == ".json").IsChecked);
		Assert.False(viewModel.AllExtensionsChecked);
	}

	[Fact]
	public void ApplyProjectProfileSelections_MissingSavedIgnoreOptions_FallsBackToVisibleDefaults()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: [IgnoreOptionId.UseGitIgnore]);

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		ApplyIgnoreCounts(coordinator, new IgnoreOptionCounts(HiddenFolders: 1, HiddenFiles: 1, DotFolders: 1, DotFiles: 1));
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], "C:\\ProjectA");

		var hiddenFolders = viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.HiddenFolders);
		var hiddenFiles = viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.HiddenFiles);
		var dotFolders = viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.DotFolders);
		var dotFiles = viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.DotFiles);
		Assert.True(hiddenFolders.IsChecked);
		Assert.True(hiddenFiles.IsChecked);
		Assert.True(dotFolders.IsChecked);
		Assert.True(dotFiles.IsChecked);
		Assert.False(viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.HideSecrets).IsChecked);
		Assert.True(viewModel.AllIgnoreChecked);
	}

	[Fact]
	public void ApplyProjectProfileSelections_EmptySavedIgnoreOptions_StillKeepsAllUnchecked()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: []);

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], "C:\\ProjectA");

		Assert.All(viewModel.IgnoreOptions, option => Assert.False(option.IsChecked));
		Assert.False(viewModel.AllIgnoreChecked);
	}

	[Fact]
	public void ResetProjectProfileSelections_ClearsAppliedExtensionCache_AndRestoresDefaults()
	{
		var viewModel = CreateViewModel();
		viewModel.AllExtensionsChecked = false;
		var coordinator = CreateCoordinator(viewModel);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [".md"],
			SelectedIgnoreOptions: []);

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		coordinator.ResetProjectProfileSelections("C:\\ProjectB");
		coordinator.ApplyExtensionScan([".cs", ".md"]);

		Assert.True(viewModel.Extensions.Single(option => option.Name == ".cs").IsChecked);
		Assert.True(viewModel.Extensions.Single(option => option.Name == ".md").IsChecked);
	}

	[AvaloniaFact]
	public async Task ApplyHideSecretsOverride_ChangesOnlyContentTransformationState()
	{
		const string path = @"C:\Project";
		var scanner = new CountingRootSelectionSnapshotScanner();
		var contentTransformationChanges = 0;
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(
			viewModel,
			scanner,
			() => path,
			contentTransformationChanged: () => contentTransformationChanges++);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(
				uncheckedIgnoreOption: IgnoreOptionId.HideSecrets));
		HookAllOptionListeners(coordinator, viewModel);
		var revisionBefore = coordinator.CurrentSelectionRevision;
		var scansBefore = scanner.TotalScanCount;

		Assert.True(coordinator.ApplyHideSecretsOverride(true));
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

		Assert.True(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.HideSecrets).IsChecked);
		Assert.Equal(1, contentTransformationChanges);
		Assert.Equal(revisionBefore, coordinator.CurrentSelectionRevision);
		Assert.Equal(scansBefore, scanner.TotalScanCount);
		Assert.False(coordinator.ApplyHideSecretsOverride(true));
		Assert.Equal(1, contentTransformationChanges);
	}

	[Fact]
	public void AcceptContentRedactionOnlyChange_AcceptsBothRedactionRowsWithoutTreeRefresh()
	{
		const string projectPath = @"C:\Project";
		var scanner = new CountingRootSelectionSnapshotScanner();
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => projectPath);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(
				uncheckedIgnoreOption: IgnoreOptionId.HideSecrets));
		HookAllOptionListeners(coordinator, viewModel);
		coordinator.AcceptCurrentSelectionsAsApplied(projectPath);
		var revisionBefore = coordinator.CurrentSelectionRevision;
		var scansBefore = scanner.TotalScanCount;

		Assert.True(coordinator.ApplyHideSecretsOverride(true));
		Assert.True(coordinator.ApplyHidePrivateDataOverride(false));
		Assert.True(coordinator.TryAcceptContentRedactionOnlyChangeAsApplied(projectPath));

		Assert.False(viewModel.HasPendingFilterSettingsChanges);
		Assert.Equal(revisionBefore, coordinator.CurrentSelectionRevision);
		Assert.Equal(scansBefore, scanner.TotalScanCount);
		Assert.False(coordinator.TryAcceptContentRedactionOnlyChangeAsApplied(projectPath));
	}

	[Fact]
	public void ProjectSwitch_DoesNotOverlayPreviousContentTransformationsOnTargetProfile()
	{
		const string projectA = @"C:\ProjectA";
		const string projectB = @"C:\ProjectB";
		var currentPath = projectA;
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => currentPath);
		var projectASnapshot = CreateReversibleSelectionRefreshSnapshot();
		ApplySelectionRefreshSnapshot(coordinator, projectASnapshot);

		Assert.True(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.HideSecrets).IsChecked);
		Assert.True(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.HidePrivateData).IsChecked);

		currentPath = projectB;
		var projectBProfile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: [],
			IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.HideSecrets] = false,
				[IgnoreOptionId.HidePrivateData] = false
			});
		coordinator.ApplyProjectProfileSelections(projectB, projectBProfile);
		var projectBSnapshot = projectASnapshot with
		{
			IgnoreOptions = projectASnapshot.IgnoreOptions
				.Select(static option => option.Id is IgnoreOptionId.HideSecrets or IgnoreOptionId.HidePrivateData
					? option with { IsChecked = false }
					: option)
				.ToArray(),
			IgnoreOptionStateCache = projectASnapshot.IgnoreOptionStateCache.ToDictionary(
				static pair => pair.Key,
				static pair => pair.Key is IgnoreOptionId.HideSecrets or IgnoreOptionId.HidePrivateData
					? false
					: pair.Value)
		};

		ApplySelectionRefreshSnapshot(coordinator, projectBSnapshot);

		Assert.False(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.HideSecrets).IsChecked);
		Assert.False(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.HidePrivateData).IsChecked);
	}

	[AvaloniaFact]
	public async Task ApplyHidePrivateDataOverride_IsImmediateAndDoesNotScanTheTree()
	{
		const string path = @"C:\Project";
		var scanner = new CountingRootSelectionSnapshotScanner();
		var changes = new List<IgnoreOptionId?>();
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(
			viewModel,
			scanner,
			() => path,
			contentTransformationChangedWithId: changes.Add);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(
				uncheckedIgnoreOption: IgnoreOptionId.HidePrivateData));
		HookAllOptionListeners(coordinator, viewModel);
		var revisionBefore = coordinator.CurrentSelectionRevision;
		var scansBefore = scanner.TotalScanCount;

		Assert.True(coordinator.ApplyHidePrivateDataOverride(true));
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

		Assert.True(viewModel.HidePrivateDataOption?.IsChecked);
		Assert.Equal([IgnoreOptionId.HidePrivateData], changes);
		Assert.Equal(revisionBefore, coordinator.CurrentSelectionRevision);
		Assert.Equal(scansBefore, scanner.TotalScanCount);
	}

	[Fact]
	public void ContentTransformationFastPath_AcceptsCodeAndHideSecretsDraftsTogether()
	{
		const string path = @"C:\Project";
		var scanner = new CountingRootSelectionSnapshotScanner();
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(
				uncheckedIgnoreOption: IgnoreOptionId.HideSecrets));
		HookAllOptionListeners(coordinator, viewModel);
		coordinator.AcceptCurrentSelectionsAsApplied(path);
		var revisionBefore = coordinator.CurrentSelectionRevision;
		var scansBefore = scanner.TotalScanCount;

		Assert.True(coordinator.ApplyHideSecretsOverride(true));
		Assert.True(coordinator.ApplyCompressCodeOverride(false));
		Assert.True(coordinator.TryAcceptContentTransformationOnlyChangeAsApplied(path));

		Assert.True(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.HideSecrets).IsChecked);
		Assert.False(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.CompressCode).IsChecked);
		Assert.False(viewModel.HasPendingFilterSettingsChanges);
		Assert.Equal(revisionBefore, coordinator.CurrentSelectionRevision);
		Assert.Equal(scansBefore, scanner.TotalScanCount);
	}

	[Fact]
	public void ContentTransformationFastPath_RejectsHiddenStructuralDraftState()
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => path);
		ApplySelectionRefreshSnapshot(coordinator, CreateReversibleSelectionRefreshSnapshot());
		HookAllOptionListeners(coordinator, viewModel);
		coordinator.AcceptCurrentSelectionsAsApplied(path);

		GetPrivateSession(coordinator).Extensions.OptionStates[".temporarily-hidden"] = false;
		Assert.True(coordinator.ApplyCompressCodeOverride(false));

		Assert.False(coordinator.TryAcceptContentTransformationOnlyChangeAsApplied(path));
		Assert.True(viewModel.HasPendingFilterSettingsChanges);
	}

	[AvaloniaFact]
	public void ProgrammaticContentTransformationOverrideCallback_IdentifiesTheChangedPipelineStage()
	{
		var changedOptions = new List<IgnoreOptionId?>();
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(
			viewModel,
			currentPathProvider: () => @"C:\Project",
			contentTransformationChangedWithId: changedOptions.Add);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(
				uncheckedIgnoreOption: IgnoreOptionId.HideSecrets));
		HookAllOptionListeners(coordinator, viewModel);
		Assert.True(coordinator.ApplyCompressCodeOverride(false));
		Assert.True(coordinator.ApplyStripCommentsOverride(false));
		Assert.True(coordinator.ApplyStripBlankLinesOverride(false));
		changedOptions.Clear();

		Assert.True(coordinator.ApplyHideSecretsOverride(true));
		Assert.True(coordinator.ApplyCompressCodeOverride(true));
		Assert.True(coordinator.ApplyStripCommentsOverride(true));
		Assert.True(coordinator.ApplyStripBlankLinesOverride(true));

		Assert.Equal(
			[
				IgnoreOptionId.HideSecrets,
				IgnoreOptionId.CompressCode,
				IgnoreOptionId.StripComments,
				IgnoreOptionId.StripBlankLines
			],
			changedOptions);
		Assert.False(MainWindow.RequiresCompressionRefresh(IgnoreOptionId.HideSecrets));
		Assert.True(MainWindow.RequiresCompressionRefresh(IgnoreOptionId.CompressCode));
		Assert.True(MainWindow.RequiresCompressionRefresh(IgnoreOptionId.StripComments));
		Assert.True(MainWindow.RequiresCompressionRefresh(IgnoreOptionId.StripBlankLines));
		Assert.True(MainWindow.RequiresCompressionRefresh(changedOptionId: null));
	}

	[AvaloniaTheory]
	[InlineData(IgnoreOptionId.HideSecrets)]
	[InlineData(IgnoreOptionId.HidePrivateData)]
	public void IndividualContentRedactionCheckbox_StagesWithoutPublishingOrAdvancingRevision(
		IgnoreOptionId optionId)
	{
		const string path = @"C:\Project";
		var changedOptions = new List<IgnoreOptionId?>();
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(
			viewModel,
			currentPathProvider: () => path,
			contentTransformationChangedWithId: changedOptions.Add);
		ApplySelectionRefreshSnapshot(coordinator, CreateReversibleSelectionRefreshSnapshot());
		HookAllOptionListeners(coordinator, viewModel);
		coordinator.AcceptCurrentSelectionsAsApplied(path);
		var option = Assert.Single(viewModel.ContentProcessingOptions, candidate => candidate.Id == optionId);
		var revisionBefore = coordinator.CurrentSelectionRevision;

		option.IsChecked = !option.IsChecked;

		Assert.Empty(changedOptions);
		Assert.Equal(revisionBefore, coordinator.CurrentSelectionRevision);
		Assert.True(viewModel.HasPendingFilterSettingsChanges);
	}

	[AvaloniaFact]
	public void ContentProcessingAll_StagesItsSectionWithoutPublishingThePipeline()
	{
		var changedOptions = new List<IgnoreOptionId?>();
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(
			viewModel,
			currentPathProvider: () => @"C:\Project",
			contentTransformationChangedWithId: changedOptions.Add);
		ApplySelectionRefreshSnapshot(coordinator, CreateReversibleSelectionRefreshSnapshot());
		HookAllOptionListeners(coordinator, viewModel);
		var revisionBefore = coordinator.CurrentSelectionRevision;

		Assert.True(viewModel.AllIgnoreChecked);
		Assert.True(viewModel.AllContentProcessingChecked);

		coordinator.HandleContentProcessingAllChanged(isChecked: false);

		Assert.All(viewModel.ContentProcessingOptions, static option => Assert.False(option.IsChecked));
		Assert.False(viewModel.AllContentProcessingChecked);
		Assert.True(viewModel.AllIgnoreChecked);
		Assert.Empty(changedOptions);
		Assert.Equal(revisionBefore, coordinator.CurrentSelectionRevision);

		coordinator.HandleContentProcessingAllChanged(isChecked: true);

		Assert.All(viewModel.ContentProcessingOptions, static option => Assert.True(option.IsChecked));
		Assert.True(viewModel.AllContentProcessingChecked);
		Assert.True(viewModel.AllIgnoreChecked);
		Assert.Empty(changedOptions);

		var blankLines = Assert.Single(
			viewModel.ContentProcessingOptions,
			static option => option.Id == IgnoreOptionId.StripBlankLines);
		blankLines.IsChecked = false;

		Assert.False(viewModel.AllContentProcessingChecked);
		Assert.True(viewModel.AllIgnoreChecked);
		Assert.Empty(changedOptions);
	}

	[AvaloniaFact]
	public void ProgrammaticContentOverride_RecomputesDerivedAllContentProcessingState()
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(
			viewModel,
			currentPathProvider: () => @"C:\Project");
		ApplySelectionRefreshSnapshot(coordinator, CreateReversibleSelectionRefreshSnapshot());

		Assert.True(viewModel.AllContentProcessingChecked);

		Assert.True(coordinator.ApplyHideSecretsOverride(false));
		Assert.False(viewModel.AllContentProcessingChecked);

		Assert.True(coordinator.ApplyHideSecretsOverride(true));
		Assert.True(viewModel.AllContentProcessingChecked);
	}

	[AvaloniaFact]
	public void ProgrammaticIgnoreOverride_RecomputesDerivedAllIgnoreState()
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(
			viewModel,
			currentPathProvider: () => @"C:\Project");
		ApplySelectionRefreshSnapshot(coordinator, CreateReversibleSelectionRefreshSnapshot());
		var selectedOptions = viewModel.IgnoreOptions
			.Where(static option => option.IsChecked && option.Id != IgnoreOptionId.HiddenFiles)
			.Select(static option => option.Id)
			.ToHashSet();

		coordinator.ApplyIgnoreSelectionOverride(selectedOptions);

		Assert.False(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.HiddenFiles).IsChecked);
		Assert.False(viewModel.AllIgnoreChecked);
		Assert.True(viewModel.AllContentProcessingChecked);
	}

	[AvaloniaFact]
	public void ProjectCheckpoint_Restore_RecomputesDerivedAggregateStateFromRestoredItems()
	{
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(
			viewModel,
			currentPathProvider: () => @"C:\Project");
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(
				uncheckedIgnoreOption: IgnoreOptionId.HideSecrets));
		viewModel.Extensions.Single().IsChecked = false;
		coordinator.UpdateExtensionsSelectionCache();
		var checkpoint = coordinator.CaptureProjectCheckpoint();

		viewModel.AllExtensionsChecked = true;
		viewModel.AllIgnoreChecked = true;
		viewModel.AllContentProcessingChecked = true;
		coordinator.RestoreProjectCheckpoint(checkpoint);

		Assert.False(viewModel.AllExtensionsChecked);
		Assert.False(viewModel.AllContentProcessingChecked);
		Assert.True(viewModel.AllIgnoreChecked);
	}

	[AvaloniaFact]
	public async Task FailedRefreshRollback_AllOffWithTrackedPreference_AllOnRestoresTrackedMode()
	{
		const string path = @"C:\Project";
		var failRefresh = false;
		var scanner = new CountingRootSelectionSnapshotScanner
		{
			BeforeRootSelectionSnapshot = _ =>
			{
				if (failRefresh)
					throw new IOException("Simulated refresh failure.");
			}
		};
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		var trackedSnapshot = WithGitMode(
			CreateReversibleSelectionRefreshSnapshot(),
			useGitIgnore: false,
			trackedOnly: true);
		ApplySelectionRefreshSnapshot(coordinator, trackedSnapshot);
		HookAllOptionListeners(coordinator, viewModel);

		coordinator.HandleIgnoreAllChanged(isChecked: false, currentPath: null);
		var allOffSnapshot = WithGitMode(
			trackedSnapshot,
			useGitIgnore: false,
			trackedOnly: false);
		ApplySelectionRefreshSnapshot(coordinator, allOffSnapshot);
		var rollbackSnapshot = GetStableSelectionSnapshot(coordinator);

		failRefresh = true;
		await Assert.ThrowsAsync<IOException>(() => coordinator.RefreshProjectSelectionAsync(
			path,
			TestContext.Current.CancellationToken));
		RestoreStableSelectionSnapshot(coordinator, rollbackSnapshot);
		coordinator.HandleIgnoreAllChanged(isChecked: true, currentPath: null);

		Assert.False(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.UseGitIgnore).IsChecked);
		Assert.True(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.TrackedGitFilesOnly).IsChecked);
	}

	[Fact]
	public void ReversibleRefresh_CoupledGitModeSnapshotIsNotRestoredForSingleCheckboxClear()
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => path);
		var template = CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433);
		var trackedSnapshot = WithGitMode(template, useGitIgnore: false, trackedOnly: true);
		var gitIgnoreSnapshot = WithGitMode(template, useGitIgnore: true, trackedOnly: false);

		ApplySelectionRefreshSnapshot(coordinator, trackedSnapshot);
		ApplyCurrentSelectionState(coordinator, viewModel, gitIgnoreSnapshot);
		ApplySelectionRefreshSnapshot(
			coordinator,
			gitIgnoreSnapshot,
			retainPreviousSnapshot: true);

		var noGitFilteringSnapshot = WithGitMode(
			gitIgnoreSnapshot,
			useGitIgnore: false,
			trackedOnly: false);
		ApplyCurrentSelectionState(coordinator, viewModel, noGitFilteringSnapshot);

		Assert.False(TryRestoreKnownSelectionSnapshot(
			coordinator,
			path,
			SelectionRefreshOrigin.IgnoreOption,
			IgnoreOptionId.UseGitIgnore));
		Assert.False(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.UseGitIgnore).IsChecked);
		Assert.False(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.TrackedGitFilesOnly).IsChecked);
	}

	[AvaloniaTheory]
	[InlineData(IgnoreOptionId.HiddenFiles)]
	[InlineData(IgnoreOptionId.DotFiles)]
	[InlineData(IgnoreOptionId.EmptyFiles)]
	[InlineData(IgnoreOptionId.ExtensionlessFiles)]
	public async Task PublicRefreshQueue_FileVisibilityToggleCycle_RestoresOriginalEmptyFoldersCounter(
		IgnoreOptionId optionId)
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		var scanner = new CountingRootSelectionSnapshotScanner
		{
			RootSelectionSnapshot = CreateDriftedRootSelectionScanData()
		};
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));
		HookAllOptionListeners(coordinator, viewModel);

		viewModel.IgnoreOptions.Single(option => option.Id == optionId).IsChecked = false;
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

		Assert.Equal(1, scanner.RootSelectionSnapshotCount);
		Assert.Equal(410, GetPrivateIgnoreOptionCounts(coordinator).EmptyFolders);
		Assert.NotEqual(
			"EmptyFolders (433)",
			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFolders).Label);
		Assert.Contains(viewModel.Extensions, option => option.Name == ".txt");

		viewModel.IgnoreOptions.Single(option => option.Id == optionId).IsChecked = true;
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

		Assert.Equal(1, scanner.RootSelectionSnapshotCount);
		Assert.Equal(433, GetPrivateIgnoreOptionCounts(coordinator).EmptyFolders);
		Assert.Equal(
			"EmptyFolders (433)",
			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFolders).Label);
		Assert.DoesNotContain(viewModel.Extensions, option => option.Name == ".txt");

		viewModel.IgnoreOptions.Single(option => option.Id == optionId).IsChecked = false;
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

		Assert.Equal(1, scanner.RootSelectionSnapshotCount);
		Assert.Equal(410, GetPrivateIgnoreOptionCounts(coordinator).EmptyFolders);
		Assert.Contains(viewModel.Extensions, option => option.Name == ".txt");
	}

	[AvaloniaFact]
	public async Task CancelPendingPathRefresh_PreservesHideSecretsWithoutContentNotification()
	{
		const string path = @"C:\Project";
		using var scanStarted = new ManualResetEventSlim();
		using var releaseScan = new ManualResetEventSlim();
		var scanner = new CountingRootSelectionSnapshotScanner
		{
			BeforeRootSelectionSnapshot = _ =>
			{
				scanStarted.Set();
				if (!releaseScan.Wait(TimeSpan.FromSeconds(3)))
					throw new TimeoutException("The controlled selection scan was not released.");
			}
		};
		var contentTransformationChanges = 0;
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(
			viewModel,
			scanner,
			() => path,
			contentTransformationChanged: () => contentTransformationChanges++);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));

		try
		{
			coordinator.HandleIgnoreAllChanged(isChecked: false, path);
			Assert.True(await Task.Run(
				() => scanStarted.Wait(TimeSpan.FromSeconds(2)),
				TestContext.Current.CancellationToken));
			Assert.True(viewModel.IgnoreOptions.Single(
				static option => option.Id == IgnoreOptionId.HideSecrets).IsChecked);
			Assert.Equal(0, contentTransformationChanges);

			Assert.True(coordinator.CancelPendingRefreshes());
			Assert.True(viewModel.IgnoreOptions.Single(
				static option => option.Id == IgnoreOptionId.HideSecrets).IsChecked);
			Assert.Equal(0, contentTransformationChanges);

			releaseScan.Set();
			await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);
			Assert.Equal(0, contentTransformationChanges);
		}
		finally
		{
			releaseScan.Set();
		}
	}

	[AvaloniaFact]
	public async Task CancelledStructuralRefresh_PreservesAppliedHideSecretsAndTransformationDraft()
	{
		const string path = @"C:\Project";
		using var scanStarted = new ManualResetEventSlim();
		using var releaseScan = new ManualResetEventSlim();
		var scanner = new CountingRootSelectionSnapshotScanner
		{
			BeforeRootSelectionSnapshot = cancellationToken =>
			{
				scanStarted.Set();
				WaitHandle.WaitAny(
					[cancellationToken.WaitHandle, releaseScan.WaitHandle],
					TimeSpan.FromSeconds(3));
				cancellationToken.ThrowIfCancellationRequested();
			}
		};
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(
				uncheckedIgnoreOption: IgnoreOptionId.HideSecrets));
		HookAllOptionListeners(coordinator, viewModel);
		coordinator.AcceptCurrentSelectionsAsApplied(path);
		Assert.True(coordinator.ApplyHideSecretsOverride(true));
		Assert.True(coordinator.ApplyCompressCodeOverride(false));
		Assert.True(coordinator.TryAcceptContentTransformationOnlyChangeAsApplied(path));

		try
		{
			viewModel.IgnoreOptions.Single(
				static option => option.Id == IgnoreOptionId.HiddenFiles).IsChecked = false;
			Assert.True(await Task.Run(
				() => scanStarted.Wait(TimeSpan.FromSeconds(2)),
				TestContext.Current.CancellationToken));
			viewModel.IgnoreOptions.Single(
				static option => option.Id == IgnoreOptionId.StripComments).IsChecked = false;

			Assert.True(coordinator.CancelPendingRefreshes());
			releaseScan.Set();
			await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

			Assert.True(viewModel.IgnoreOptions.Single(
				static option => option.Id == IgnoreOptionId.HideSecrets).IsChecked);
			Assert.False(viewModel.IgnoreOptions.Single(
				static option => option.Id == IgnoreOptionId.CompressCode).IsChecked);
			Assert.False(viewModel.IgnoreOptions.Single(
				static option => option.Id == IgnoreOptionId.StripComments).IsChecked);
		}
		finally
		{
			releaseScan.Set();
		}
	}

	[AvaloniaFact]
	public async Task FailedStructuralRefresh_PreservesAppliedHideSecretsAndTransformationDraft()
	{
		const string path = @"C:\Project";
		using var scanStarted = new ManualResetEventSlim();
		using var releaseScan = new ManualResetEventSlim();
		var scanner = new CountingRootSelectionSnapshotScanner
		{
			BeforeRootSelectionSnapshot = _ =>
			{
				scanStarted.Set();
				if (!releaseScan.Wait(TimeSpan.FromSeconds(3)))
					throw new TimeoutException("The controlled selection scan was not released.");
				throw new IOException("controlled refresh failure");
			}
		};
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(
				uncheckedIgnoreOption: IgnoreOptionId.HideSecrets));
		HookAllOptionListeners(coordinator, viewModel);
		coordinator.AcceptCurrentSelectionsAsApplied(path);
		Assert.True(coordinator.ApplyHideSecretsOverride(true));
		Assert.True(coordinator.TryAcceptHideSecretsOnlyChangeAsApplied(path));

		viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.HiddenFiles).IsChecked = false;
		Assert.True(await Task.Run(
			() => scanStarted.Wait(TimeSpan.FromSeconds(2)),
			TestContext.Current.CancellationToken));
		viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.StripBlankLines).IsChecked = false;
		releaseScan.Set();

		await Assert.ThrowsAsync<IOException>(async () =>
			await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken));

		Assert.True(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.HideSecrets).IsChecked);
		Assert.False(viewModel.IgnoreOptions.Single(
			static option => option.Id == IgnoreOptionId.StripBlankLines).IsChecked);
	}

	[AvaloniaFact]
	public async Task PublicRefreshQueue_RapidIgnoreReversal_CancelsStaleScanAndKeepsStableSnapshot()
	{
		const string path = @"C:\Project";
		using var scanStarted = new ManualResetEventSlim();
		using var releaseScan = new ManualResetEventSlim();
		var cancellationObserved = 0;
		var scanner = new CountingRootSelectionSnapshotScanner
		{
			RootSelectionSnapshot = CreateDriftedRootSelectionScanData(),
			BeforeRootSelectionSnapshot = cancellationToken =>
			{
				scanStarted.Set();
				var signal = WaitHandle.WaitAny(
					[cancellationToken.WaitHandle, releaseScan.WaitHandle],
					TimeSpan.FromSeconds(3));
				if (signal == 0)
				{
					Interlocked.Exchange(ref cancellationObserved, 1);
					cancellationToken.ThrowIfCancellationRequested();
				}

				if (signal == WaitHandle.WaitTimeout)
					throw new TimeoutException("The controlled selection scan was not released.");
			}
		};
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));
		HookAllOptionListeners(coordinator, viewModel);

		try
		{
			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFiles).IsChecked = false;
			Assert.True(await Task.Run(
				() => scanStarted.Wait(TimeSpan.FromSeconds(2)),
				TestContext.Current.CancellationToken));

			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFiles).IsChecked = true;
			await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

			Assert.Equal(1, Volatile.Read(ref cancellationObserved));
			Assert.Equal(1, scanner.RootSelectionSnapshotCount);
			Assert.Equal(433, GetPrivateIgnoreOptionCounts(coordinator).EmptyFolders);
			Assert.Equal(
				"EmptyFolders (433)",
				viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFolders).Label);
			Assert.DoesNotContain(viewModel.Extensions, option => option.Name == ".txt");
		}
		finally
		{
			releaseScan.Set();
		}
	}

	[AvaloniaFact]
	public async Task PublicRefreshQueue_AdditionalExtensionChange_InvalidatesEarlierIgnoreRollback()
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		var scanner = new CountingRootSelectionSnapshotScanner
		{
			RootSelectionSnapshot = CreateDriftedRootSelectionScanData()
		};
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));
		HookAllOptionListeners(coordinator, viewModel);

		viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFiles).IsChecked = false;
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);
		Assert.Equal(1, scanner.RootSelectionSnapshotCount);

		viewModel.Extensions.Single(option => option.Name == ".txt").IsChecked = false;
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);
		Assert.Equal(2, scanner.RootSelectionSnapshotCount);

		viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFiles).IsChecked = true;
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

		Assert.Equal(3, scanner.RootSelectionSnapshotCount);
		Assert.Equal(410, GetPrivateIgnoreOptionCounts(coordinator).EmptyFolders);
	}

	[AvaloniaFact]
	public async Task PublicRefreshQueue_PathChangesDuringScan_DoesNotApplyStaleSnapshot()
	{
		const string originalPath = @"C:\ProjectA";
		const string newPath = @"C:\ProjectB";
		var currentPath = originalPath;
		using var scanStarted = new ManualResetEventSlim();
		using var releaseScan = new ManualResetEventSlim();
		var scanner = new CountingRootSelectionSnapshotScanner
		{
			RootSelectionSnapshot = CreateDriftedRootSelectionScanData(),
			BeforeRootSelectionSnapshot = cancellationToken =>
			{
				scanStarted.Set();
				if (!releaseScan.Wait(TimeSpan.FromSeconds(3), cancellationToken))
					throw new TimeoutException("The controlled selection scan was not released.");
			}
		};
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => currentPath);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));
		HookAllOptionListeners(coordinator, viewModel);

		try
		{
			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFiles).IsChecked = false;
			Assert.True(await Task.Run(
				() => scanStarted.Wait(TimeSpan.FromSeconds(2)),
				TestContext.Current.CancellationToken));

			currentPath = newPath;
			releaseScan.Set();
			await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

			Assert.Equal(1, scanner.RootSelectionSnapshotCount);
			Assert.Equal(433, GetPrivateIgnoreOptionCounts(coordinator).EmptyFolders);
			Assert.Equal(
				"EmptyFolders (433)",
				viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFolders).Label);
			Assert.DoesNotContain(viewModel.Extensions, option => option.Name == ".txt");
		}
		finally
		{
			releaseScan.Set();
		}
	}

	[AvaloniaFact]
	public async Task PublicIgnoreAllChange_ExplicitPreferenceRejectsSemanticallyDifferentRollback()
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		var scanner = new CountingRootSelectionSnapshotScanner
		{
			RootSelectionSnapshot = CreateDriftedRootSelectionScanData()
		};
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));
		HookAllOptionListeners(coordinator, viewModel);

		viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFiles).IsChecked = false;
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);
		Assert.Equal(1, scanner.TotalScanCount);
		var scanCountBeforeBulkChange = scanner.TotalScanCount;

		coordinator.HandleIgnoreAllChanged(true, path);
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

		Assert.True(scanner.TotalScanCount > scanCountBeforeBulkChange);
		Assert.True(viewModel.AllIgnoreChecked);
		Assert.All(viewModel.IgnoreOptions, static option => Assert.True(option.IsChecked));
	}

	[Fact]
	public void ReversibleRefresh_ExtensionToggleCycle_RestoresCountsWithoutScanning()
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		var scanner = new CountingRootSelectionSnapshotScanner();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));
		ApplyCurrentSelectionState(
			coordinator,
			viewModel,
			CreateReversibleSelectionRefreshSnapshot(
				extensionChecked: false,
				emptyFolderCount: 410));
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(
				extensionChecked: false,
				emptyFolderCount: 410),
			retainPreviousSnapshot: true);

		ApplyCurrentSelectionState(
			coordinator,
			viewModel,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));

		Assert.True(TryRestoreKnownSelectionSnapshot(
			coordinator,
			path,
			SelectionRefreshOrigin.ExtensionSelection));
		Assert.Equal(0, scanner.TotalScanCount);
		Assert.True(viewModel.Extensions.Single().IsChecked);
		Assert.Equal(
			"EmptyFolders (433)",
			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFolders).Label);

		ApplyCurrentSelectionState(
			coordinator,
			viewModel,
			CreateReversibleSelectionRefreshSnapshot(
				extensionChecked: false,
				emptyFolderCount: 410));

		Assert.True(TryRestoreKnownSelectionSnapshot(
			coordinator,
			path,
			SelectionRefreshOrigin.ExtensionSelection));
		Assert.Equal(0, scanner.TotalScanCount);
		Assert.False(viewModel.Extensions.Single().IsChecked);
		Assert.Equal(
			"EmptyFolders (410)",
			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFolders).Label);
	}

	[Fact]
	public void ReversibleRefresh_HiddenCacheStateDiffers_RejectsCachedSnapshot()
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		var scanner = new CountingRootSelectionSnapshotScanner();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));
		GetPrivateSession(coordinator).Extensions.OptionStates[".hidden"] = true;

		Assert.False(TryRestoreKnownSelectionSnapshot(coordinator, path));
		Assert.Equal(0, scanner.TotalScanCount);
	}

	[Fact]
	public void ReversibleRefresh_IgnoreReversalAfterAdditionalExtensionChange_RejectsCachedSnapshot()
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		using var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => path);
		var enabledSnapshot = CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433);
		var disabledSnapshot = CreateReversibleSelectionRefreshSnapshot(
			IgnoreOptionId.EmptyFiles,
			emptyFolderCount: 410) with
		{
			ExtensionOptions =
			[
				new SelectionOption(".cs", true),
				new SelectionOption(".txt", true)
			]
		};
		ApplySelectionRefreshSnapshot(coordinator, enabledSnapshot);
		ApplyCurrentSelectionState(coordinator, viewModel, disabledSnapshot);
		ApplySelectionRefreshSnapshot(
			coordinator,
			disabledSnapshot,
			retainPreviousSnapshot: true);
		var currentSnapshot = CreateIgnoreReversalCurrentSnapshot(
			disabledSnapshot,
			enabledSnapshot,
			IgnoreOptionId.EmptyFiles) with
		{
			ExtensionOptions =
			[
				new SelectionOption(".cs", true),
				new SelectionOption(".txt", false)
			]
		};
		ApplyCurrentSelectionState(coordinator, viewModel, currentSnapshot);

		Assert.False(TryRestoreKnownSelectionSnapshot(
			coordinator,
			path,
			SelectionRefreshOrigin.IgnoreOption,
			IgnoreOptionId.EmptyFiles));
	}

	[Fact]
	public void StableRefresh_CheckboxStateMatchesButCountLabelDrifted_RestoresStablePresentation()
	{
		const string path = @"C:\Project";
		var viewModel = CreateViewModel();
		var scanner = new CountingRootSelectionSnapshotScanner();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));
		ApplyCurrentSelectionState(
			coordinator,
			viewModel,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 410));

		Assert.True(TryRestoreKnownSelectionSnapshot(coordinator, path));

		Assert.Equal(0, scanner.TotalScanCount);
		Assert.Equal(
			"EmptyFolders (433)",
			viewModel.IgnoreOptions.Single(option => option.Id == IgnoreOptionId.EmptyFolders).Label);
	}

	[Fact]
	public void ReversibleRefresh_DifferentPath_RejectsCachedSnapshot()
	{
		const string path = @"C:\ProjectA";
		var viewModel = CreateViewModel();
		var scanner = new CountingRootSelectionSnapshotScanner();
		using var coordinator = CreateCoordinator(viewModel, scanner, () => path);
		ApplySelectionRefreshSnapshot(
			coordinator,
			CreateReversibleSelectionRefreshSnapshot(emptyFolderCount: 433));

		Assert.False(TryRestoreKnownSelectionSnapshot(coordinator, @"C:\ProjectB"));
		Assert.Equal(0, scanner.TotalScanCount);
	}

	[Fact]
	public void ApplyExtensionOptions_WhenOptionsAreUnchanged_KeepsExistingViewModels()
	{
		var viewModel = CreateViewModel();
		viewModel.AllExtensionsChecked = false;
		var coordinator = CreateCoordinator(viewModel);
		var options = new[]
		{
			new SelectionOption(".cs", true),
			new SelectionOption(".md", false)
		};

		ApplyExtensionOptions(coordinator, options);
		var firstExtension = viewModel.Extensions[0];
		var collectionEvents = 0;
		viewModel.Extensions.CollectionChanged += (_, _) => collectionEvents++;

		ApplyExtensionOptions(coordinator, options);

		Assert.Same(firstExtension, viewModel.Extensions[0]);
		Assert.Equal(0, collectionEvents);
	}

	[Fact]
	public void ApplyExtensionOptions_WhenOnlyCheckedStateChanges_UpdatesExistingViewModels()
	{
		var viewModel = CreateViewModel();
		viewModel.AllExtensionsChecked = false;
		var coordinator = CreateCoordinator(viewModel);
		ApplyExtensionOptions(coordinator, [new SelectionOption(".cs", true), new SelectionOption(".md", false)]);
		var firstExtension = viewModel.Extensions[0];
		var collectionEvents = 0;
		viewModel.Extensions.CollectionChanged += (_, _) => collectionEvents++;

		ApplyExtensionOptions(coordinator, [new SelectionOption(".cs", false), new SelectionOption(".md", true)]);

		Assert.Same(firstExtension, viewModel.Extensions[0]);
		Assert.False(viewModel.Extensions[0].IsChecked);
		Assert.True(viewModel.Extensions[1].IsChecked);
		Assert.Equal(0, collectionEvents);
	}

	[Fact]
	public void ApplyResolvedIgnoreOptions_WhenOptionsAreUnchanged_KeepsExistingViewModels()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		var options = new[]
		{
			new ResolvedIgnoreOptionState(IgnoreOptionId.DotFolders, "dot folders", true, true),
			new ResolvedIgnoreOptionState(IgnoreOptionId.EmptyFiles, "empty files", true, false)
		};
		var stateCache = new Dictionary<IgnoreOptionId, bool>
		{
			[IgnoreOptionId.DotFolders] = true,
			[IgnoreOptionId.EmptyFiles] = false
		};

		ApplyResolvedIgnoreOptions(coordinator, options, stateCache);
		var firstIgnoreOption = viewModel.IgnoreOptions[0];
		var collectionEvents = 0;
		viewModel.IgnoreOptions.CollectionChanged += (_, _) => collectionEvents++;

		ApplyResolvedIgnoreOptions(coordinator, options, stateCache);

		Assert.Same(firstIgnoreOption, viewModel.IgnoreOptions[0]);
		Assert.Equal(0, collectionEvents);
	}

	[Fact]
	public void ApplyResolvedIgnoreOptions_WhenStateAndLabelChange_UpdatesExistingViewModels()
	{
		var viewModel = CreateViewModel();
		var coordinator = CreateCoordinator(viewModel);
		ApplyResolvedIgnoreOptions(
			coordinator,
			[new ResolvedIgnoreOptionState(IgnoreOptionId.DotFolders, "dot folders (1)", true, true)],
			new Dictionary<IgnoreOptionId, bool> { [IgnoreOptionId.DotFolders] = true });
		var firstIgnoreOption = viewModel.IgnoreOptions[0];
		var collectionEvents = 0;
		viewModel.IgnoreOptions.CollectionChanged += (_, _) => collectionEvents++;

		ApplyResolvedIgnoreOptions(
			coordinator,
			[new ResolvedIgnoreOptionState(IgnoreOptionId.DotFolders, "dot folders (2)", true, false)],
			new Dictionary<IgnoreOptionId, bool> { [IgnoreOptionId.DotFolders] = false });

		Assert.Same(firstIgnoreOption, viewModel.IgnoreOptions[0]);
		Assert.Equal("dot folders (2)", firstIgnoreOption.Label);
		Assert.False(firstIgnoreOption.IsChecked);
		Assert.Equal(0, collectionEvents);
	}

	private static MainWindowViewModel CreateViewModel()
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		return new MainWindowViewModel(localization, new HelpContentProvider());
	}

	private static int GetEventSubscriberCount(object instance, string eventName)
	{
		var eventField = instance.GetType().GetField(
			eventName,
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(eventField);
		var handlers = eventField.GetValue(instance) as Delegate;
		return handlers?.GetInvocationList().Length ?? 0;
	}

	private static void HookAllOptionListeners(
		SelectionSyncCoordinator coordinator,
		MainWindowViewModel viewModel)
	{
		coordinator.HookOptionListeners(viewModel.Extensions);
		coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);
	}

	private static IgnoreSectionScanData CreateDriftedRootSelectionScanData()
	{
		var counts = new IgnoreOptionCounts(
			HiddenFolders: 1,
			HiddenFiles: 1,
			DotFolders: 1,
			DotFiles: 1,
			EmptyFolders: 410,
			ExtensionlessFiles: 1,
			EmptyFiles: 1);

		return new IgnoreSectionScanData(
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".txt" },
			counts,
			counts,
			new IgnoreControllerImpactCounts(GitIgnore: 1, SmartIgnore: 1));
	}

	private static SelectionSyncCoordinator CreateCoordinator(
		MainWindowViewModel viewModel,
		IFileSystemScanner? scanner = null,
		Func<string?>? currentPathProvider = null,
		Func<string, IReadOnlyCollection<string>, IgnoreOptionsAvailability>? availabilityProvider = null,
		Action? contentTransformationChanged = null,
		Action<IgnoreOptionId?>? contentTransformationChangedWithId = null,
		Func<CancellationToken, Task<bool>>? gitAvailabilityResolver = null)
	{
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		scanner ??= new StubFileSystemScanner();
		var scanOptions = new ScanOptionsUseCase(LegacyWorkspaceScannerTestAdapter.Adapt(scanner));
		var filterService = new FilterOptionSelectionService();
		var ignoreService = new IgnoreOptionsService(localization);
		Func<string, IgnoreRules> buildIgnoreRules = _ => new IgnoreRules(false,
			false,
			false,
			false,
			new HashSet<string>(),
			new HashSet<string>());

		if (availabilityProvider is null &&
		    contentTransformationChanged is null &&
		    contentTransformationChangedWithId is null &&
		    gitAvailabilityResolver is null)
		{
			return new SelectionSyncCoordinator(
				viewModel,
				scanOptions,
				filterService,
				ignoreService,
				buildIgnoreRules,
				_ => false,
				currentPathProvider ?? (() => null));
		}

		availabilityProvider ??= static (_, _) => new IgnoreOptionsAvailability(
			IncludeGitIgnore: true,
			IncludeSmartIgnore: true);

		return new SelectionSyncCoordinator(
			viewModel,
			scanOptions,
			filterService,
			ignoreService,
			(path, _, _) => buildIgnoreRules(path),
			availabilityProvider,
			_ => false,
			currentPathProvider ?? (() => null),
			contentTransformationChanged: contentTransformationChangedWithId ??
				(contentTransformationChanged is null
					? null
					: _ => contentTransformationChanged()),
			gitAvailabilityResolver: gitAvailabilityResolver);
	}

	[Fact]
	public void ShouldClearCachesForCurrentPath_WithPreparedTargetProfile_ReturnsFalse()
	{
		var result = SelectionRefreshPolicy.ShouldClearCachesForCurrentPath(
			"C:\\ProjectA",
			"C:\\ProjectB",
			"C:\\ProjectB");

		Assert.False(result);
	}

	[Fact]
	public void ShouldClearCachesForCurrentPath_PathSwitchWithoutPreparedProfile_ReturnsTrue()
	{
		var result = SelectionRefreshPolicy.ShouldClearCachesForCurrentPath(
			"C:\\ProjectA",
			null,
			"C:\\ProjectB");

		Assert.True(result);
	}

	[Fact]
	public void ShouldClearCachesForCurrentPath_NoLastLoadedPath_ReturnsFalse()
	{
		var result = SelectionRefreshPolicy.ShouldClearCachesForCurrentPath(
			null,
			null,
			"C:\\ProjectA");

		Assert.False(result);
	}

	[Fact]
	public void ShouldClearCachesForCurrentPath_SamePath_ReturnsFalse()
	{
		var result = SelectionRefreshPolicy.ShouldClearCachesForCurrentPath(
			"C:\\ProjectA",
			null,
			"C:\\ProjectA");

		Assert.False(result);
	}

	[Fact]
	public void ShouldClearCachesForCurrentPath_PreparedPathForAnotherProject_ReturnsTrue()
	{
		var result = SelectionRefreshPolicy.ShouldClearCachesForCurrentPath(
			"C:\\ProjectA",
			"C:\\ProjectC",
			"C:\\ProjectB");

		Assert.True(result);
	}

	[Fact]
	public void ShouldClearCachesForCurrentPath_PreparedPathCaseDifference_UsesPlatformComparer()
	{
		var result = SelectionRefreshPolicy.ShouldClearCachesForCurrentPath(
			"C:\\ProjectA",
			"c:\\projectb",
			"C:\\ProjectB");

		Assert.Equal(!OperatingSystem.IsWindows(), result);
	}

	[Fact]
	public void ShouldSkipRefreshForPreparedPath_PreparedForAnotherProject_ReturnsTrue()
	{
		var shouldSkip = SelectionRefreshPolicy.ShouldSkipRefreshForPreparedPath(
			"C:\\TargetProject",
			"C:\\AnotherProject");

		Assert.True(shouldSkip);
	}

	[Fact]
	public void ShouldSkipRefreshForPreparedPath_PreparedForCurrentProject_ReturnsFalse()
	{
		var shouldSkip = SelectionRefreshPolicy.ShouldSkipRefreshForPreparedPath(
			"C:\\TargetProject",
			"C:\\TargetProject");

		Assert.False(shouldSkip);
	}

	[Fact]
	public void PopulateIgnoreOptionsForRootSelection_WhenPathIsStale_DoesNotMutateOptions()
	{
		var viewModel = CreateViewModel();
		var currentPath = "C:\\ProjectB";
		var coordinator = CreateCoordinator(viewModel, currentPathProvider: () => currentPath);

		coordinator.PopulateIgnoreOptionsForRootSelection([], "C:\\ProjectA");

		Assert.Empty(viewModel.IgnoreOptions);
	}

	[Fact]
	public async Task PopulateExtensionsForRootSelectionAsync_WhenPathIsStale_DoesNotMutateExtensionOptions()
	{
		var viewModel = CreateViewModel();
		viewModel.Extensions.Add(new SelectionOptionViewModel(".keep", true));
		var currentPath = "C:\\ProjectB";
		var scanner = new StubFileSystemScanner
		{
			GetRootFileExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".json" },
				false,
				false)
		};
		var coordinator = CreateCoordinator(viewModel, scanner, () => currentPath);

		await coordinator.PopulateExtensionsForRootSelectionAsync("C:\\ProjectA", [], cancellationToken: TestContext.Current.CancellationToken);

		Assert.Single(viewModel.Extensions);
		Assert.Equal(".keep", viewModel.Extensions[0].Name);
		Assert.True(viewModel.Extensions[0].IsChecked);
	}

	[Fact]
	public void ApplyProjectProfileSelections_EmptyExtensions_StillInitializesExtensionCache()
	{
		var coordinator = CreateCoordinator(CreateViewModel());
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: []);

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);

		var session = GetPrivateSession(coordinator);
		Assert.True(session.Extensions.IsInitialized);
		Assert.Empty(session.Extensions.SelectedNames);
	}

	[Fact]
	public void ResetProjectProfileSelections_StoresPreparedPathForTargetProject()
	{
		var coordinator = CreateCoordinator(CreateViewModel());

		coordinator.ResetProjectProfileSelections("C:\\ProjectB");

		var session = GetPrivateSession(coordinator);
		Assert.True(session.HasPreparedSelectionForPath("C:\\ProjectB"));
	}

	[Fact]
	public void ApplyProjectProfileSelections_PreventsAllIgnoreOverride()
	{
		var viewModel = CreateViewModel();
		// Intentionally keep default AllIgnoreChecked=true to verify fix.
		var coordinator = CreateCoordinator(viewModel);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: [IgnoreOptionId.DotFiles]);

		coordinator.ApplyProjectProfileSelections("C:\\ProjectA", profile);
		ApplyIgnoreCounts(coordinator, new IgnoreOptionCounts(DotFiles: 1, HiddenFolders: 1));
		coordinator.PopulateIgnoreOptionsForRootSelection(["src"], "C:\\ProjectA");

		Assert.False(viewModel.AllIgnoreChecked);
		Assert.Contains(viewModel.IgnoreOptions, option => option.Id == IgnoreOptionId.DotFiles && option.IsChecked);
		Assert.Contains(viewModel.IgnoreOptions, option => option.Id != IgnoreOptionId.DotFiles && !option.IsChecked);
	}

	private sealed class CountingRootSelectionSnapshotScanner
		: IFileSystemScanner, IFileSystemScannerRootSelectionSnapshotProvider
	{
		public int RootSelectionSnapshotCount { get; private set; }
		public int TotalScanCount { get; private set; }
		public Action<CancellationToken>? BeforeRootSelectionSnapshot { get; init; }
		public IgnoreSectionScanData RootSelectionSnapshot { get; init; } = new(
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
			new IgnoreOptionCounts(HiddenFolders: 1, HiddenFiles: 1),
			new IgnoreOptionCounts(HiddenFolders: 1, HiddenFiles: 1));

		public bool CanReadRoot(string rootPath) => true;

		public ScanResult<HashSet<string>> GetExtensions(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			TotalScanCount++;
			return new ScanResult<HashSet<string>>(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
				false,
				false);
		}

		public ScanResult<HashSet<string>> GetRootFileExtensions(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			TotalScanCount++;
			return new ScanResult<HashSet<string>>(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
				false,
				false);
		}

		public ScanResult<List<string>> GetRootFolderNames(
			string rootPath,
			IgnoreRules rules,
			CancellationToken cancellationToken = default)
		{
			TotalScanCount++;
			return new ScanResult<List<string>>(["src"], false, false);
		}

		public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshotForRootSelection(
			string rootPath,
			IReadOnlyCollection<string> selectedRootFolders,
			IgnoreRules extensionDiscoveryRules,
			IgnoreRules effectiveRules,
			IExtensionInclusionPolicy? effectiveExtensionPolicy,
			bool includeDirectoryToggleProbeRoots = false,
			CancellationToken cancellationToken = default,
			bool includeControllerImpactProbeRoots = false)
		{
			RootSelectionSnapshotCount++;
			TotalScanCount++;
			BeforeRootSelectionSnapshot?.Invoke(cancellationToken);
			return new ScanResult<IgnoreSectionScanData>(RootSelectionSnapshot, false, false);
		}
	}

	private static StubLocalizationCatalog CreateCatalog()
	{
		var data = new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Settings.Ignore.SmartIgnore"] = "Smart ignore",
				["Settings.Ignore.HideSecrets"] = "Hide secrets",
				["Settings.Ignore.HidePrivateData"] = "Hide private data",
				["Settings.Ignore.UseGitIgnore"] = "Use .gitignore",
				["Settings.Ignore.TrackedGitFilesOnly"] = "Tracked Git files only",
				["Settings.Ignore.HiddenFolders"] = "Hidden folders",
				["Settings.Ignore.HiddenFiles"] = "Hidden files",
				["Settings.Ignore.DotFolders"] = "dot folders",
				["Settings.Ignore.DotFiles"] = "dot files",
				["Settings.Ignore.EmptyFolders"] = "Empty folders",
				["Settings.Ignore.EmptyFiles"] = "Empty files",
				["Settings.Ignore.ExtensionlessFiles"] = "Extensionless files"
			},
			[AppLanguage.Ru] = new Dictionary<string, string>
			{
				["Settings.Ignore.SmartIgnore"] = "Умное исключение",
				["Settings.Ignore.HideSecrets"] = "Скрывать секреты",
				["Settings.Ignore.HidePrivateData"] = "Скрывать личные данные",
				["Settings.Ignore.UseGitIgnore"] = "Использовать .gitignore",
				["Settings.Ignore.TrackedGitFilesOnly"] = "Только файлы под контролем Git",
				["Settings.Ignore.HiddenFolders"] = "Скрытые папки",
				["Settings.Ignore.HiddenFiles"] = "Скрытые файлы",
				["Settings.Ignore.DotFolders"] = "Папки с точкой",
				["Settings.Ignore.DotFiles"] = "Файлы с точкой",
				["Settings.Ignore.EmptyFolders"] = "Пустые папки",
				["Settings.Ignore.EmptyFiles"] = "Пустые файлы",
				["Settings.Ignore.ExtensionlessFiles"] = "Файлы без расширения"
			}
		};

		return new StubLocalizationCatalog(data);
	}

	private static void ApplyIgnoreCounts(SelectionSyncCoordinator coordinator, IgnoreOptionCounts ignoreCounts)
	{
		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"ApplyExtensionOptions",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method!.Invoke(coordinator, [Array.Empty<SelectionOption>(), 0, ignoreCounts, IgnoreControllerImpactCounts.Empty, true]);
	}

	private static void ApplyScanRootOptions(
		SelectionSyncCoordinator coordinator,
		IReadOnlyList<SelectionOption> options)
	{
		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"ApplyScanRootOptions",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method!.Invoke(coordinator, [options]);
	}

	private static void ApplyExtensionOptions(
		SelectionSyncCoordinator coordinator,
		IReadOnlyList<SelectionOption> options)
	{
		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"ApplyExtensionOptions",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method!.Invoke(
			coordinator,
			[options, 0, IgnoreOptionCounts.Empty, IgnoreControllerImpactCounts.Empty, true]);
	}

	private static void ApplyResolvedIgnoreOptions(
		SelectionSyncCoordinator coordinator,
		IReadOnlyList<ResolvedIgnoreOptionState> options,
		IReadOnlyDictionary<IgnoreOptionId, bool> stateCache)
	{
		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"ApplyResolvedIgnoreOptions",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method!.Invoke(coordinator, [options, stateCache]);
	}

	private static void ApplySelectionRefreshSnapshot(
		SelectionSyncCoordinator coordinator,
		SelectionRefreshSnapshot snapshot,
		bool retainPreviousSnapshot = false)
	{
		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"ApplySelectionRefreshSnapshot",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method!.Invoke(coordinator, [snapshot, retainPreviousSnapshot]);
	}

	private static void ApplySelectionRefreshSnapshotWithCompleteness(
		SelectionSyncCoordinator coordinator,
		SelectionRefreshSnapshot snapshot,
		bool cacheIsComplete)
	{
		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"ApplySelectionRefreshSnapshotWithCompleteness",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method!.Invoke(coordinator, [snapshot, false, cacheIsComplete]);
	}

	private static SelectionRefreshRollbackSnapshot GetStableSelectionSnapshot(
		SelectionSyncCoordinator coordinator)
	{
		var field = typeof(SelectionSyncCoordinator).GetField(
			"_stableSelectionSnapshot",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		return Assert.IsType<SelectionRefreshRollbackSnapshot>(field!.GetValue(coordinator));
	}

	private static void RestoreStableSelectionSnapshot(
		SelectionSyncCoordinator coordinator,
		SelectionRefreshRollbackSnapshot snapshot)
	{
		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"RestoreStableSelectionSnapshot",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method!.Invoke(coordinator, [snapshot]);
	}

	private static void ApplyCurrentSelectionState(
		SelectionSyncCoordinator coordinator,
		MainWindowViewModel viewModel,
		SelectionRefreshSnapshot snapshot)
	{
		viewModel.AllExtensionsChecked = snapshot.EffectiveExtensionOptions.All(static option => option.IsChecked);
		ApplyExtensionOptions(coordinator, snapshot.EffectiveExtensionOptions);
		coordinator.UpdateExtensionsSelectionCache();
		ApplyResolvedIgnoreOptions(
			coordinator,
			snapshot.IgnoreOptions,
			snapshot.IgnoreOptionStateCache);
	}

	private static bool TryRestoreKnownSelectionSnapshot(
		SelectionSyncCoordinator coordinator,
		string path,
		SelectionRefreshOrigin origin = SelectionRefreshOrigin.Unknown,
		IgnoreOptionId? changedIgnoreOptionId = null)
	{
		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"TryRestoreKnownSelectionSnapshot",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		return Assert.IsType<bool>(method!.Invoke(
			coordinator,
			[path, origin, changedIgnoreOptionId]));
	}

	private static SelectionRefreshSnapshot CreateSelectionRefreshSnapshot()
	{
		return new SelectionRefreshSnapshot(
			RootOptions:
			[
				new SelectionOption("src", true),
				new SelectionOption("docs", false)
			],
			ExtensionOptions:
			[
				new SelectionOption(".cs", true),
				new SelectionOption(".md", false)
			],
			IgnoreOptions:
			[
				new ResolvedIgnoreOptionState(IgnoreOptionId.DotFolders, "dot folders", true, true),
				new ResolvedIgnoreOptionState(IgnoreOptionId.EmptyFiles, "empty files", true, false)
			],
			ExtensionlessEntriesCount: 0,
			HasIgnoreOptionCounts: true,
			IgnoreOptionCounts: new IgnoreOptionCounts(DotFolders: 1),
			ControllerImpactCounts: IgnoreControllerImpactCounts.Empty,
			IgnoreOptionStateCache: new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.DotFolders] = true,
				[IgnoreOptionId.EmptyFiles] = false
			},
			RootAccessDenied: false,
			HadAccessDenied: false);
	}

	private static SelectionRefreshSnapshot CreateReversibleSelectionRefreshSnapshot(
		IgnoreOptionId? uncheckedIgnoreOption = null,
		bool rootChecked = true,
		bool extensionChecked = true,
		int emptyFolderCount = 433)
	{
		var ignoreOptions = Enum.GetValues<IgnoreOptionId>()
			.Where(static optionId => optionId != IgnoreOptionId.TrackedGitFilesOnly)
			.Select(optionId => new ResolvedIgnoreOptionState(
				optionId,
				$"{optionId} ({(optionId == IgnoreOptionId.EmptyFolders ? emptyFolderCount : 1)})",
				DefaultChecked: true,
				IsChecked: optionId != uncheckedIgnoreOption))
			.ToArray();
		var ignoreStateCache = ignoreOptions.ToDictionary(
			static option => option.Id,
			static option => option.IsChecked);

		return new SelectionRefreshSnapshot(
			RootOptions: [new SelectionOption("src", rootChecked)],
			ExtensionOptions: [new SelectionOption(".cs", extensionChecked)],
			IgnoreOptions: ignoreOptions,
			ExtensionlessEntriesCount: 1,
			HasIgnoreOptionCounts: true,
			IgnoreOptionCounts: new IgnoreOptionCounts(
				HiddenFolders: 1,
				HiddenFiles: 1,
				DotFolders: 1,
				DotFiles: 1,
				EmptyFolders: emptyFolderCount,
				ExtensionlessFiles: 1,
				EmptyFiles: 1),
			ControllerImpactCounts: new IgnoreControllerImpactCounts(
				GitIgnore: 1,
				SmartIgnore: 1),
			IgnoreOptionStateCache: ignoreStateCache,
			RootAccessDenied: false,
			HadAccessDenied: false);
	}

	private static SelectionRefreshSnapshot CreateIgnoreReversalCurrentSnapshot(
		SelectionRefreshSnapshot stableSnapshot,
		SelectionRefreshSnapshot reversibleSnapshot,
		IgnoreOptionId changedOptionId)
	{
		var reversedState = reversibleSnapshot.IgnoreOptionStateCache[changedOptionId];
		var ignoreOptions = stableSnapshot.IgnoreOptions
			.Select(option => option.Id == changedOptionId
				? option with { IsChecked = reversedState }
				: option)
			.ToArray();
		var stateCache = new Dictionary<IgnoreOptionId, bool>(stableSnapshot.IgnoreOptionStateCache)
		{
			[changedOptionId] = reversedState
		};

		return stableSnapshot with
		{
			IgnoreOptions = ignoreOptions,
			IgnoreOptionStateCache = stateCache
		};
	}

	private static SelectionRefreshSnapshot WithGitMode(
		SelectionRefreshSnapshot snapshot,
		bool useGitIgnore,
		bool trackedOnly)
	{
		var options = snapshot.IgnoreOptions
			.Where(static option => option.Id is not IgnoreOptionId.UseGitIgnore and not IgnoreOptionId.TrackedGitFilesOnly)
			.Prepend(new ResolvedIgnoreOptionState(
				IgnoreOptionId.TrackedGitFilesOnly,
				"Tracked Git files only",
				DefaultChecked: false,
				IsChecked: trackedOnly))
			.Prepend(new ResolvedIgnoreOptionState(
				IgnoreOptionId.UseGitIgnore,
				"Use .gitignore",
				DefaultChecked: true,
				IsChecked: useGitIgnore))
			.ToArray();
		var stateCache = new Dictionary<IgnoreOptionId, bool>(snapshot.IgnoreOptionStateCache)
		{
			[IgnoreOptionId.UseGitIgnore] = useGitIgnore,
			[IgnoreOptionId.TrackedGitFilesOnly] = trackedOnly
		};

		return snapshot with
		{
			IgnoreOptions = options,
			IgnoreOptionStateCache = stateCache
		};
	}

	private static void MarkSelectionRefreshDirty(SelectionSyncCoordinator coordinator)
	{
		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"MarkSelectionRefreshDirty",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method!.Invoke(coordinator, []);
	}

	private static bool IsSelectionRefreshDirty(SelectionSyncCoordinator coordinator)
	{
		var field = typeof(SelectionSyncCoordinator).GetField(
			"_selectionRefreshDirty",
			BindingFlags.NonPublic | BindingFlags.Instance);
		Assert.NotNull(field);
		return (int)field!.GetValue(coordinator)! != 0;
	}

	private static ProjectSelectionSessionState GetPrivateSession(SelectionSyncCoordinator coordinator)
	{
		var field = typeof(SelectionSyncCoordinator).GetField(
			"_session",
			BindingFlags.NonPublic | BindingFlags.Instance);
		Assert.NotNull(field);
		return (ProjectSelectionSessionState)field.GetValue(coordinator)!;
	}

	private static int GetPrivateIgnoreOptionsVersion(SelectionSyncCoordinator coordinator)
	{
		var field = typeof(SelectionSyncCoordinator).GetField(
			"_ignoreOptionsVersion",
			BindingFlags.NonPublic | BindingFlags.Instance);
		Assert.NotNull(field);
		return (int)field.GetValue(coordinator)!;
	}

	private static int GetPrivateRequestVersion(
		SelectionSyncCoordinator coordinator,
		string fieldName)
	{
		var field = typeof(SelectionSyncCoordinator).GetField(
			fieldName,
			BindingFlags.NonPublic | BindingFlags.Instance);
		Assert.NotNull(field);
		return (int)field.GetValue(coordinator)!;
	}

	private static IgnoreOptionCounts GetPrivateIgnoreOptionCounts(SelectionSyncCoordinator coordinator)
	{
		var field = typeof(SelectionSyncCoordinator).GetField(
			"_ignoreOptionCounts",
			BindingFlags.NonPublic | BindingFlags.Instance);
		Assert.NotNull(field);
		return (IgnoreOptionCounts)field.GetValue(coordinator)!;
	}
}
