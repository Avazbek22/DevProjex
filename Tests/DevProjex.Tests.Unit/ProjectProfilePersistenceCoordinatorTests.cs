using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class ProjectProfilePersistenceCoordinatorTests
{
	[Fact]
	public async Task Persist_UsesAppliedSelectionsWithoutWritingCurrentMarkedSecrets()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			viewModel.Extensions.Add(new SelectionOptionViewModel(".md", false));
			viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(
				IgnoreOptionId.HiddenFiles,
				"hidden files",
				true));
			viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(
				IgnoreOptionId.HideSecrets,
				"hide secrets",
				false));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);

			viewModel.Extensions[0].IsChecked = false;
			viewModel.Extensions[1].IsChecked = true;
			viewModel.IgnoreOptions[0].IsChecked = false;
			using var secretSession = new SecretRedactionSession(new EmptySecretDetector());
			Assert.True(secretSession.AddMarkedSecret(new MarkedSecretProfileEntry(
				"001122334455",
				"TOKEN",
				16)));
			var store = new RetryProfileStore(projectPath, failures: 0);
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);

			await persistence.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);

			var saved = store.SavedProfiles[Path.GetFullPath(projectPath)];
			Assert.Equal([".cs"], saved.SelectedExtensions.ToArray());
			Assert.Contains(IgnoreOptionId.HiddenFiles, saved.SelectedIgnoreOptions);
			Assert.Empty(saved.MarkedSecrets!);
		}
	}

	[Fact]
	public async Task Persist_AfterEnsuringHideSecrets_MergesImmediateAppliedOptionWithoutDrafts()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			viewModel.Extensions.Add(new SelectionOptionViewModel(".md", false));
			viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(
				IgnoreOptionId.HideSecrets,
				"hide secrets",
				false));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);

			viewModel.Extensions[0].IsChecked = false;
			viewModel.Extensions[1].IsChecked = true;
			Assert.True(selectionCoordinator.ApplyHideSecretsOverride(true));
			Assert.False(selectionCoordinator.ApplyHideSecretsOverride(true));
			selectionCoordinator.AcceptHideSecretsOverrideAsApplied(projectPath);
			var store = new RetryProfileStore(projectPath, failures: 0);
			using var secretSession = new SecretRedactionSession(new EmptySecretDetector());
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);

			await persistence.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);

			var saved = store.SavedProfiles[Path.GetFullPath(projectPath)];
			Assert.Equal([".cs"], saved.SelectedExtensions.ToArray());
			Assert.Contains(IgnoreOptionId.HideSecrets, saved.SelectedIgnoreOptions);
		}
	}

	[Fact]
	public void PendingWrites_AreRetriedPerProjectWithoutCrossProjectReplacement()
	{
		using var workspace = new TemporaryDirectory();
		var firstProject = workspace.CreateFolder("first");
		var secondProject = workspace.CreateFolder("second");
		var store = new RetryProfileStore(firstProject, failures: 2);
		var queue = new PendingProjectProfileWriteQueue(store);
		var firstProfile = CreateProfile(".cs");
		var secondProfile = CreateProfile(".json");

		queue.Persist(firstProject, firstProfile, DateTimeOffset.UtcNow.AddMinutes(-1));
		Assert.Equal(1, queue.Count);

		queue.Persist(secondProject, secondProfile, DateTimeOffset.UtcNow);

		Assert.Equal(1, queue.Count);
		Assert.DoesNotContain(Path.GetFullPath(firstProject), store.SavedProfiles.Keys);
		Assert.Equal([".json"], store.SavedProfiles[Path.GetFullPath(secondProject)].SelectedExtensions.ToArray());

		queue.Flush();

		Assert.Equal(0, queue.Count);
		Assert.Equal([".cs"], store.SavedProfiles[Path.GetFullPath(firstProject)].SelectedExtensions.ToArray());
	}

	[Fact]
	public async Task PendingWrites_DoNotFlushAProjectBlockedByProfileLoading()
	{
		using var workspace = new TemporaryDirectory();
		var blockedProject = workspace.CreateFolder("blocked");
		var writableProject = workspace.CreateFolder("writable");
		var store = new RetryProfileStore(blockedProject, failures: 1);
		var queue = new PendingProjectProfileWriteQueue(store);
		queue.Persist(blockedProject, CreateProfile(".cs"), DateTimeOffset.UtcNow);
		Assert.Equal(1, queue.Count);

		await queue.PersistAsync(
			writableProject,
			CreateProfile(".json"),
			DateTimeOffset.UtcNow,
			path => !PathComparer.Default.Equals(path, blockedProject),
			TestContext.Current.CancellationToken);

		Assert.Equal(1, queue.Count);
		Assert.DoesNotContain(Path.GetFullPath(blockedProject), store.SavedProfiles.Keys);
		Assert.Equal(
			[".json"],
			store.SavedProfiles[Path.GetFullPath(writableProject)].SelectedExtensions.ToArray());
	}

	[Fact]
	public async Task PersistentMarkWriter_RetriesTheSameTypedDeltaUntilDurableSuccess()
	{
		var snapshot = new PersistentSecretMarksSnapshot(
			7,
			[new MarkedSecretProfileEntry("001122334455", "TOKEN", 12)]);
		var store = new RetryMarkStore(
			new PersistentSecretMarkWriteResult(PersistentSecretMarkStoreStatus.TemporarilyUnavailable, null),
			new PersistentSecretMarkWriteResult(PersistentSecretMarkStoreStatus.WriteFailed, null),
			new PersistentSecretMarkWriteResult(PersistentSecretMarkStoreStatus.Success, snapshot));
		var writer = new PersistentSecretMarkDeltaWriter(
			store,
			static (_, _) => Task.CompletedTask,
			[TimeSpan.Zero, TimeSpan.Zero]);
		var delta = PersistentSecretMarkDelta.Add(snapshot.Marks.Single());

		var result = await writer.ApplyAsync(
			@"C:\Project",
			delta,
			TestContext.Current.CancellationToken);

		Assert.True(result.Succeeded);
		Assert.Equal(3, store.AppliedDeltas.Count);
		Assert.All(store.AppliedDeltas, applied => Assert.Equal(delta, applied));
	}

	[Fact]
	public async Task PersistentMarkWriter_CancellationDuringBackoffStopsBeforeAnotherWrite()
	{
		var store = new RetryMarkStore(
			new PersistentSecretMarkWriteResult(
				PersistentSecretMarkStoreStatus.TemporarilyUnavailable,
				null));
		var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var writer = new PersistentSecretMarkDeltaWriter(
			store,
			async (_, cancellationToken) =>
			{
				delayStarted.TrySetResult();
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			},
			[TimeSpan.FromSeconds(1)]);
		using var cancellation = new CancellationTokenSource();
		var write = writer.ApplyAsync(
			@"C:\Project",
			PersistentSecretMarkDelta.Add(
				new MarkedSecretProfileEntry("001122334455", "TOKEN", 12)),
			cancellation.Token);
		await delayStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
		Assert.Single(store.AppliedDeltas);
	}

	[Fact]
	public async Task PersistentMarkWriter_SerializesDeltasFromOneWindowInIssueOrder()
	{
		var store = new BlockingOrderedMarkStore();
		var writer = new PersistentSecretMarkDeltaWriter(store, retryDelays: []);
		var mark = new MarkedSecretProfileEntry("001122334455", "TOKEN", 12);
		var add = PersistentSecretMarkDelta.Add(mark);
		var remove = PersistentSecretMarkDelta.Remove(
			new PersistentSecretMarkId(mark.H, mark.Length));

		var first = writer.ApplyAsync("project", add, TestContext.Current.CancellationToken);
		await store.FirstEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
		var second = writer.ApplyAsync("project", remove, TestContext.Current.CancellationToken);
		await Task.Delay(50, TestContext.Current.CancellationToken);

		Assert.Equal(1, store.CallCount);
		store.ReleaseFirst.TrySetResult();
		await Task.WhenAll(first, second);
		Assert.Equal([add, remove], store.AppliedDeltas);
	}

	[Fact]
	public async Task MarkWriteCompletingAfterProjectSwitch_DoesNotReplaceTheNewProjectSnapshot()
	{
		using var workspace = new TemporaryDirectory();
		var firstProject = workspace.CreateFolder("first");
		var secondProject = workspace.CreateFolder("second");
		var activeProject = firstProject;
		var store = new BlockingOrderedMarkStore();
		var (viewModel, selection) = CreateSelectionCoordinator(firstProject);
		using (selection)
		using (var session = new SecretRedactionSession(new EmptySecretDetector()))
		{
			var coordinator = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selection,
				store,
				session,
				() => activeProject);
			var firstMark = new MarkedSecretProfileEntry("001122334455", "FIRST", 12);
			var secondMark = new MarkedSecretProfileEntry("66778899aabb", "SECOND", 16);
			var write = coordinator.ApplyMarkDeltaAsync(
				firstProject,
				PersistentSecretMarkDelta.Add(firstMark),
				TestContext.Current.CancellationToken);
			await store.FirstEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

			activeProject = secondProject;
			session.ReplacePersistentMarks(
				secondProject,
				new PersistentSecretMarksSnapshot(4, [secondMark]));
			store.ReleaseFirst.TrySetResult();
			Assert.True((await write).Succeeded);

			Assert.Equal(secondMark, Assert.Single(session.GetMarkedSecrets()));
		}
	}

	[Fact]
	public async Task TemporaryProfileLookup_BlocksPersistUntilAProfileLoadsSuccessfully()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			var store = new StatusProfileStore(
				new ProjectProfileLookupResult(ProjectProfileLookupStatus.TemporarilyUnavailable, null),
				new ProjectProfileLookupResult(
					ProjectProfileLookupStatus.Found,
					new ProjectSelectionProfile([], [".json"], [])));
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);

			var unavailable = await persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			await persistence.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);
			var found = await persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			await persistence.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);

			Assert.Equal(ProjectProfileLookupStatus.TemporarilyUnavailable, unavailable.Status);
			Assert.True(found.HasProfile);
			Assert.Equal(1, store.SaveCount);
		}
	}

	[Fact]
	public async Task ProfileLookupInProgress_BlocksPersistBeforeTheStoreReturnsAStatus()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		using (var store = new BlockingLookupProfileStore())
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);
			var load = persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			Assert.True(store.Entered.Wait(
				TimeSpan.FromSeconds(5),
				TestContext.Current.CancellationToken));

			await persistence.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);
			Assert.Equal(0, store.SaveCount);

			store.Release.Set();
			Assert.Equal(ProjectProfileLookupStatus.Missing, (await load).Status);
			await persistence.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);
			Assert.Equal(1, store.SaveCount);
		}
	}

	[Fact]
	public async Task CanceledMarksReload_RestoresThePreviousPersistableLoadState()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		using (var cancellation = new CancellationTokenSource())
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			var store = new CancelingMarksReloadStore();
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);
			Assert.Equal(
				ProjectProfileLookupStatus.Missing,
				(await persistence.LoadSnapshotAsync(
					projectPath,
					TestContext.Current.CancellationToken)).Status);

			var canceledReload = persistence.LoadSnapshotAsync(projectPath, cancellation.Token);
			await store.SecondMarksLoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
			cancellation.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledReload);
			await persistence.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);

			Assert.Equal(1, store.SaveCount);
		}
	}

	[Fact]
	public async Task InvalidProfileStorage_BlocksAutomaticPersist()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		{
			var store = new StatusProfileStore(
				new ProjectProfileLookupResult(ProjectProfileLookupStatus.InvalidStorage, null));
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);

			var snapshot = await persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			await persistence.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);

			Assert.Equal(ProjectProfileLookupStatus.InvalidStorage, snapshot.Status);
			Assert.Equal(0, store.SaveCount);
		}
	}

	[Fact]
	public async Task MissingSelectionProfile_LoadsIndependentPersistentMarks()
	{
		using var workspace = new TemporaryDirectory();
		var projectPath = workspace.CreateFolder("project");
		var appDataPath = workspace.CreateFolder("app-data");
		var store = new ProjectProfileStore(() => appDataPath);
		var mark = new MarkedSecretProfileEntry("001122334455", "TOKEN", 12);
		var write = await store.AddMarkAsync(
			projectPath,
			mark,
			TestContext.Current.CancellationToken);
		Assert.True(write.Succeeded);
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector(), store))
		{
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);

			var snapshot = await persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);

			Assert.Equal(ProjectProfileLookupStatus.Missing, snapshot.Status);
			Assert.Null(snapshot.Profile);
			Assert.Equal(mark, Assert.Single(snapshot.PersistentMarks!.Marks));
		}
	}

	[Fact]
	public async Task UnavailableMarkStore_BlocksSelectionPersistUntilMarksLoad()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		using (var secretSession = new SecretRedactionSession(new EmptySecretDetector()))
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);
			var mark = new MarkedSecretProfileEntry("001122334455", "TOKEN", 12);
			var store = new StatusProfileAndMarkStore(
				new PersistentSecretMarksLoadResult(
					PersistentSecretMarkStoreStatus.TemporarilyUnavailable,
					null),
				new PersistentSecretMarksLoadResult(
					PersistentSecretMarkStoreStatus.Success,
					new PersistentSecretMarksSnapshot(4, [mark])));
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);

			var unavailable = await persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			await persistence.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);
			var loaded = await persistence.LoadSnapshotAsync(
				projectPath,
				TestContext.Current.CancellationToken);
			await persistence.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);

			Assert.Equal(ProjectProfileLookupStatus.TemporarilyUnavailable, unavailable.Status);
			Assert.Equal(ProjectProfileLookupStatus.Missing, loaded.Status);
			Assert.Equal(mark, Assert.Single(loaded.PersistentMarks!.Marks));
			Assert.Equal(1, store.SaveCount);
		}
	}

	[Fact]
	public async Task IndependentGuiCoordinators_MergeMarkDeltasWithoutSelectionSaveResurrection()
	{
		using var workspace = new TemporaryDirectory();
		var projectPath = workspace.CreateFolder("project");
		var appDataPath = workspace.CreateFolder("app-data");
		var storeA = new ProjectProfileStore(() => appDataPath);
		var storeB = new ProjectProfileStore(() => appDataPath);
		var (viewModelA, selectionA) = CreateSelectionCoordinator(projectPath);
		var (viewModelB, selectionB) = CreateSelectionCoordinator(projectPath);
		using (selectionA)
		using (selectionB)
		using (var sessionA = new SecretRedactionSession(new EmptySecretDetector(), storeA))
		using (var sessionB = new SecretRedactionSession(new EmptySecretDetector(), storeB))
		{
			viewModelA.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			viewModelB.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			selectionA.AcceptCurrentSelectionsAsApplied(projectPath);
			selectionB.AcceptCurrentSelectionsAsApplied(projectPath);
			var coordinatorA = new ProjectProfilePersistenceCoordinator(
				viewModelA,
				selectionA,
				storeA,
				sessionA);
			var coordinatorB = new ProjectProfilePersistenceCoordinator(
				viewModelB,
				selectionB,
				storeB,
				sessionB);
			await coordinatorA.LoadSnapshotAsync(projectPath, TestContext.Current.CancellationToken);
			await coordinatorB.LoadSnapshotAsync(projectPath, TestContext.Current.CancellationToken);

			var markA = new MarkedSecretProfileEntry("001122334455", "A", 12);
			var markB = new MarkedSecretProfileEntry("66778899aabb", "B", 16);
			var staleAddA = PersistentSecretMarkDelta.Add(
				markA,
				sessionA.PersistentMarksStoreRevision);
			var addedA = await coordinatorA.ApplyMarkDeltaAsync(
				projectPath,
				staleAddA,
				TestContext.Current.CancellationToken);
			Assert.True(addedA.Succeeded);
			Assert.True((await coordinatorB.ApplyMarkDeltaAsync(
				projectPath,
				PersistentSecretMarkDelta.Add(
					markB,
					sessionB.PersistentMarksStoreRevision),
				TestContext.Current.CancellationToken)).Succeeded);
			Assert.True((await coordinatorA.ApplyMarkDeltaAsync(
				projectPath,
				PersistentSecretMarkDelta.Remove(
					new PersistentSecretMarkId(markA.H, markA.Length),
					addedA.Snapshot!.Revision),
				TestContext.Current.CancellationToken)).Succeeded);

			await coordinatorB.PersistIfNeededAsync(projectPath, TestContext.Current.CancellationToken);
			Assert.True((await coordinatorB.ApplyMarkDeltaAsync(
				projectPath,
				staleAddA,
				TestContext.Current.CancellationToken)).Succeeded);

			var reopened = await new ProjectProfileStore(() => appDataPath)
				.LoadMarksAsync(projectPath, TestContext.Current.CancellationToken);
			Assert.True(reopened.Succeeded);
			Assert.Equal(markB, Assert.Single(reopened.Snapshot!.Marks));
		}
	}

	private static ProjectSelectionProfile CreateProfile(string extension) => new(
		SelectedRootFolders: [],
		SelectedExtensions: [extension],
		SelectedIgnoreOptions: []);

	private static (MainWindowViewModel ViewModel, SelectionSyncCoordinator Coordinator)
		CreateSelectionCoordinator(string projectPath)
	{
		var catalog = new StubLocalizationCatalog(
			new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
			{
				[AppLanguage.En] = new Dictionary<string, string>()
			});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var viewModel = new MainWindowViewModel(localization, new HelpContentProvider());
		var coordinator = new SelectionSyncCoordinator(
			viewModel,
			new ScanOptionsUseCase(LegacyWorkspaceScannerTestAdapter.Adapt(new StubFileSystemScanner())),
			new FilterOptionSelectionService(),
			new IgnoreOptionsService(localization),
			_ => new IgnoreRules(
				false,
				false,
				false,
				false,
				new HashSet<string>(),
				new HashSet<string>()),
			_ => false,
			() => projectPath);
		return (viewModel, coordinator);
	}

	private sealed class RetryProfileStore(string failingPath, int failures) : IProjectProfileStore
	{
		private readonly string _failingPath = Path.GetFullPath(failingPath);
		private int _remainingFailures = failures;

		public Dictionary<string, ProjectSelectionProfile> SavedProfiles { get; } =
			new(PathComparer.Default);

		public bool TrySaveProfile(
			string localProjectPath,
			ProjectSelectionProfile profile,
			DateTimeOffset updatedUtc)
		{
			var path = Path.GetFullPath(localProjectPath);
			if (PathComparer.Default.Equals(path, _failingPath) && _remainingFailures-- > 0)
				return false;

			SavedProfiles[path] = ProjectSelectionProfileBuilder.Clone(profile);
			return true;
		}

		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile) =>
			TrySaveProfile(localProjectPath, profile, DateTimeOffset.UtcNow);

		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile) =>
			_ = TrySaveProfile(localProjectPath, profile);

		public bool EnsureStorageExists() => true;

		public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile) =>
			SavedProfiles.TryGetValue(Path.GetFullPath(localProjectPath), out profile!);

		public void ClearAllProfiles() => SavedProfiles.Clear();
	}

	private sealed class EmptySecretDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) => [];
	}

	private sealed class RetryMarkStore(params PersistentSecretMarkWriteResult[] results) :
		IPersistentSecretMarkStore
	{
		private readonly Queue<PersistentSecretMarkWriteResult> _results = new(results);

		public List<PersistentSecretMarkDelta> AppliedDeltas { get; } = [];

		public ValueTask<PersistentSecretMarksLoadResult> LoadMarksAsync(
			string localProjectPath,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(new PersistentSecretMarksLoadResult(
				PersistentSecretMarkStoreStatus.Success,
				PersistentSecretMarksSnapshot.Empty));

		public ValueTask<PersistentSecretMarkWriteResult> AddMarkAsync(
			string localProjectPath,
			MarkedSecretProfileEntry mark,
			CancellationToken cancellationToken = default) =>
			ApplyMarkDeltaAsync(localProjectPath, PersistentSecretMarkDelta.Add(mark), cancellationToken);

		public ValueTask<PersistentSecretMarkWriteResult> RemoveMarkAsync(
			string localProjectPath,
			PersistentSecretMarkId markId,
			CancellationToken cancellationToken = default) =>
			ApplyMarkDeltaAsync(localProjectPath, PersistentSecretMarkDelta.Remove(markId), cancellationToken);

		public ValueTask<PersistentSecretMarkWriteResult> ApplyMarkDeltaAsync(
			string localProjectPath,
			PersistentSecretMarkDelta delta,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			AppliedDeltas.Add(delta);
			return ValueTask.FromResult(_results.Dequeue());
		}
	}

	private sealed class BlockingOrderedMarkStore : IProjectProfileStore, IPersistentSecretMarkStore
	{
		private int _callCount;

		public TaskCompletionSource FirstEntered { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseFirst { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public List<PersistentSecretMarkDelta> AppliedDeltas { get; } = [];
		public int CallCount => Volatile.Read(ref _callCount);

		public ProjectProfileLookupResult LookupProfile(string localProjectPath, TimeSpan lockTimeout) =>
			new(ProjectProfileLookupStatus.Missing, null);

		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile) => true;

		public bool TrySaveProfile(
			string localProjectPath,
			ProjectSelectionProfile profile,
			DateTimeOffset updatedUtc) => true;

		public bool EnsureStorageExists() => true;

		public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile)
		{
			profile = null!;
			return false;
		}

		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile)
		{
		}

		public void ClearAllProfiles()
		{
		}

		public ValueTask<PersistentSecretMarksLoadResult> LoadMarksAsync(
			string localProjectPath,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<PersistentSecretMarkWriteResult> AddMarkAsync(
			string localProjectPath,
			MarkedSecretProfileEntry mark,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<PersistentSecretMarkWriteResult> RemoveMarkAsync(
			string localProjectPath,
			PersistentSecretMarkId markId,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public async ValueTask<PersistentSecretMarkWriteResult> ApplyMarkDeltaAsync(
			string localProjectPath,
			PersistentSecretMarkDelta delta,
			CancellationToken cancellationToken = default)
		{
			var call = Interlocked.Increment(ref _callCount);
			lock (AppliedDeltas)
				AppliedDeltas.Add(delta);
			if (call == 1)
			{
				FirstEntered.TrySetResult();
				await ReleaseFirst.Task.WaitAsync(cancellationToken);
			}

			return new PersistentSecretMarkWriteResult(
				PersistentSecretMarkStoreStatus.Success,
				PersistentSecretMarksSnapshot.Empty);
		}
	}

	private sealed class StatusProfileStore(params ProjectProfileLookupResult[] lookups) :
		IProjectProfileStore
	{
		private readonly Queue<ProjectProfileLookupResult> _lookups = new(lookups);

		public int SaveCount { get; private set; }

		public ProjectProfileLookupResult LookupProfile(string localProjectPath, TimeSpan lockTimeout) =>
			_lookups.Dequeue();

		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile)
		{
			SaveCount++;
			return true;
		}

		public bool TrySaveProfile(
			string localProjectPath,
			ProjectSelectionProfile profile,
			DateTimeOffset updatedUtc) =>
			TrySaveProfile(localProjectPath, profile);

		public bool EnsureStorageExists() => true;

		public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile)
		{
			var result = LookupProfile(localProjectPath, TimeSpan.Zero);
			profile = result.Profile!;
			return result.Status == ProjectProfileLookupStatus.Found;
		}

		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile) =>
			TrySaveProfile(localProjectPath, profile);

		public void ClearAllProfiles()
		{
		}
	}

	private sealed class CancelingMarksReloadStore : IProjectProfileStore, IPersistentSecretMarkStore
	{
		private int _marksLoadCount;

		public TaskCompletionSource SecondMarksLoadStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public int SaveCount { get; private set; }

		public ProjectProfileLookupResult LookupProfile(string localProjectPath, TimeSpan lockTimeout) =>
			new(ProjectProfileLookupStatus.Missing, null);

		public async ValueTask<PersistentSecretMarksLoadResult> LoadMarksAsync(
			string localProjectPath,
			CancellationToken cancellationToken = default)
		{
			if (Interlocked.Increment(ref _marksLoadCount) == 1)
			{
				return new PersistentSecretMarksLoadResult(
					PersistentSecretMarkStoreStatus.Success,
					PersistentSecretMarksSnapshot.Empty);
			}

			SecondMarksLoadStarted.TrySetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			throw new InvalidOperationException("The canceled mark load unexpectedly resumed.");
		}

		public ValueTask<PersistentSecretMarkWriteResult> AddMarkAsync(
			string localProjectPath,
			MarkedSecretProfileEntry mark,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<PersistentSecretMarkWriteResult> RemoveMarkAsync(
			string localProjectPath,
			PersistentSecretMarkId markId,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<PersistentSecretMarkWriteResult> ApplyMarkDeltaAsync(
			string localProjectPath,
			PersistentSecretMarkDelta delta,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile)
		{
			SaveCount++;
			return true;
		}

		public bool TrySaveProfile(
			string localProjectPath,
			ProjectSelectionProfile profile,
			DateTimeOffset updatedUtc) =>
			TrySaveProfile(localProjectPath, profile);

		public bool EnsureStorageExists() => true;

		public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile)
		{
			profile = null!;
			return false;
		}

		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile) =>
			TrySaveProfile(localProjectPath, profile);

		public void ClearAllProfiles()
		{
		}
	}

	private sealed class BlockingLookupProfileStore : IProjectProfileStore, IDisposable
	{
		public ManualResetEventSlim Entered { get; } = new();
		public ManualResetEventSlim Release { get; } = new();
		public int SaveCount { get; private set; }

		public ProjectProfileLookupResult LookupProfile(string localProjectPath, TimeSpan lockTimeout)
		{
			Entered.Set();
			if (!Release.Wait(TimeSpan.FromSeconds(5)))
				throw new TimeoutException("The controlled profile lookup was not released.");
			return new ProjectProfileLookupResult(ProjectProfileLookupStatus.Missing, null);
		}

		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile)
		{
			SaveCount++;
			return true;
		}

		public bool TrySaveProfile(
			string localProjectPath,
			ProjectSelectionProfile profile,
			DateTimeOffset updatedUtc) =>
			TrySaveProfile(localProjectPath, profile);

		public bool EnsureStorageExists() => true;

		public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile)
		{
			profile = null!;
			return false;
		}

		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile) =>
			TrySaveProfile(localProjectPath, profile);

		public void ClearAllProfiles()
		{
		}

		public void Dispose()
		{
			Release.Set();
			Entered.Dispose();
			Release.Dispose();
		}
	}

	private sealed class StatusProfileAndMarkStore(
		params PersistentSecretMarksLoadResult[] markLookups) :
		IProjectProfileStore,
		IPersistentSecretMarkStore
	{
		private readonly Queue<PersistentSecretMarksLoadResult> _markLookups = new(markLookups);

		public int SaveCount { get; private set; }

		public ProjectProfileLookupResult LookupProfile(string localProjectPath, TimeSpan lockTimeout) =>
			new(ProjectProfileLookupStatus.Missing, null);

		public ValueTask<PersistentSecretMarksLoadResult> LoadMarksAsync(
			string localProjectPath,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.FromResult(_markLookups.Dequeue());
		}

		public ValueTask<PersistentSecretMarkWriteResult> AddMarkAsync(
			string localProjectPath,
			MarkedSecretProfileEntry mark,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<PersistentSecretMarkWriteResult> RemoveMarkAsync(
			string localProjectPath,
			PersistentSecretMarkId markId,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public ValueTask<PersistentSecretMarkWriteResult> ApplyMarkDeltaAsync(
			string localProjectPath,
			PersistentSecretMarkDelta delta,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile)
		{
			SaveCount++;
			return true;
		}

		public bool TrySaveProfile(
			string localProjectPath,
			ProjectSelectionProfile profile,
			DateTimeOffset updatedUtc) =>
			TrySaveProfile(localProjectPath, profile);

		public bool EnsureStorageExists() => true;

		public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile)
		{
			profile = null!;
			return false;
		}

		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile) =>
			TrySaveProfile(localProjectPath, profile);

		public void ClearAllProfiles()
		{
		}
	}
}
