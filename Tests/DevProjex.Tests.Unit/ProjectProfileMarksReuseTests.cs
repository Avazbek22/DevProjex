using System.Diagnostics;
using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

[Collection("AvaloniaUI")]
public sealed class ProjectProfileMarksReuseTests(ITestOutputHelper output)
{
	[Fact]
	public async Task MigratedProfile_ReusesLookupMarksWithoutReadingTheDatabaseAgain()
	{
		using var fixture = new ProfileFixture(totalMarks: 1000);
		var before = fixture.MarkDocumentParseCount;

		var snapshot = await fixture.LoadAsync();

		fixture.AssertSnapshot(snapshot);
		Assert.Equal(1, fixture.MarkDocumentParseCount - before);
		Assert.Equal(0, fixture.Store.ExplicitMarkLoadCount);
	}

	[Fact]
	public async Task LegacyProfile_PreservesMergedRevisionAndExistingTombstones()
	{
		using var fixture = new ProfileFixture(totalMarks: 1000);
		var added = new MarkedSecretProfileEntry("ffffffffffff", "LEGACY", 12);
		fixture.WriteLegacySelectionProfile([added, fixture.RemovedMark]);

		var snapshot = await fixture.LoadAsync();

		Assert.Equal(ProjectProfileLookupStatus.Found, snapshot.Status);
		Assert.Equal(fixture.InitialRevision + 1, snapshot.PersistentMarks!.Revision);
		Assert.Equal(fixture.MarksPerProject + 1, snapshot.PersistentMarks.Marks.Count);
		Assert.Contains(added, snapshot.PersistentMarks.Marks);
		Assert.DoesNotContain(fixture.RemovedMark, snapshot.PersistentMarks.Marks);
		Assert.Equal(fixture.InitialRevision,
			snapshot.PersistentMarks.StateAppliedRevisions![fixture.RemovedIdentity]);
		Assert.Equal(fixture.InitialRevision + 1,
			snapshot.PersistentMarks.StateAppliedRevisions[new PersistentSecretMarkId(added.H, added.Length)]);
		Assert.DoesNotContain("markedSecrets", File.ReadAllText(fixture.PhysicalStore.GetPath()),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task CustomLookupWithoutMarksSnapshot_RetainsIndependentMarkStoreFallback()
	{
		using var fixture = new ProfileFixture(totalMarks: 1000, omitLookupSnapshot: true);

		var snapshot = await fixture.LoadAsync();

		fixture.AssertSnapshot(snapshot);
		Assert.Equal(1, fixture.Store.ExplicitMarkLoadCount);
	}

	[Fact]
	public async Task MissingSelectionProfile_StillLoadsIndependentMarks()
	{
		using var fixture = new ProfileFixture(totalMarks: 1000, createSelectionProfile: false);
		var before = fixture.MarkDocumentParseCount;

		var snapshot = await fixture.LoadAsync();

		Assert.Equal(ProjectProfileLookupStatus.Missing, snapshot.Status);
		Assert.Null(snapshot.Profile);
		fixture.AssertMarks(snapshot.PersistentMarks!);
		Assert.Equal(1, fixture.Store.ExplicitMarkLoadCount);
		Assert.Equal(1, fixture.MarkDocumentParseCount - before);
	}

	[Fact]
	public async Task LoadedSnapshot_PreservesConcurrentMarkAndSelectionRevisionGates()
	{
		using var fixture = new ProfileFixture(totalMarks: 1000);
		var loaded = await fixture.LoadAsync();
		fixture.AssertSnapshot(loaded);
		var observedLookup = fixture.Store.LastLookup!;
		var mark = loaded.PersistentMarks!.Marks.First();
		var staleAdd = PersistentSecretMarkDelta.Add(mark, loaded.PersistentMarks.Revision);
		var external = new ProjectProfileStore(() => fixture.AppDataPath);
		var removed = await external.RemoveMarkAsync(fixture.ProjectPath,
			new PersistentSecretMarkId(mark.H, mark.Length), TestContext.Current.CancellationToken);
		Assert.True(removed.Succeeded);
		Assert.True(external.TrySaveProfile(fixture.ProjectPath,
			loaded.Profile! with
			{
				SelectedExtensions = [".json"],
				ExtensionStates = new Dictionary<string, bool> { [".json"] = true, [".cs"] = false }
			},
			observedLookup.UpdatedUtc!.Value.AddSeconds(1)));

		var staleSelection = fixture.PhysicalStore.TrySaveProfileWithResult(
			fixture.ProjectPath, loaded.Profile! with { SelectedExtensions = [".md"] },
			observedLookup.UpdatedUtc);
		var staleMark = await fixture.Coordinator.ApplyMarkDeltaAsync(
			fixture.ProjectPath, staleAdd, TestContext.Current.CancellationToken);

		Assert.Equal(ProjectProfileSaveStatus.Conflict, staleSelection.Status);
		Assert.True(staleMark.Succeeded);
		Assert.DoesNotContain(mark, staleMark.Snapshot!.Marks);
		Assert.Equal(removed.Snapshot!.Revision, staleMark.Snapshot.Revision);
		var current = external.LookupProfile(fixture.ProjectPath, TimeSpan.FromSeconds(1));
		Assert.Equal([".json"], current.Profile!.SelectedExtensions);
		var reloaded = await fixture.LoadAsync();
		Assert.Equal(removed.Snapshot.Revision, reloaded.PersistentMarks!.Revision);
		Assert.DoesNotContain(mark, reloaded.PersistentMarks.Marks);
		Assert.Equal([".json"], reloaded.Profile!.SelectedExtensions);
	}

	[Fact]
	public void LookupSnapshot_DoesNotChangeSerializedResponseShape()
	{
		using var fixture = new ProfileFixture(totalMarks: 1000);
		var lookup = fixture.PhysicalStore.LookupProfile(fixture.ProjectPath, TimeSpan.FromSeconds(1));
		Assert.NotEmpty(lookup.PersistentMarks!.StateAppliedRevisions!);
		var withoutSnapshot = lookup with { PersistentMarks = null };

		Assert.Equal(JsonSerializer.Serialize(withoutSnapshot), JsonSerializer.Serialize(lookup));
		var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
		Assert.Equal(JsonSerializer.Serialize(withoutSnapshot, options), JsonSerializer.Serialize(lookup, options));
	}

	[Fact]
	public async Task CancellationAfterLookup_DoesNotPublishAttachedSnapshotOrReloadMarks()
	{
		using var fixture = new ProfileFixture(totalMarks: 1000);
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		fixture.Store.AfterLookup = cancellation.Cancel;

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			fixture.Coordinator.LoadSnapshotAsync(fixture.ProjectPath, cancellation.Token));

		Assert.Equal(0, fixture.Store.ExplicitMarkLoadCount);
		fixture.Store.AfterLookup = null;
		fixture.AssertSnapshot(await fixture.LoadAsync());
	}

	[Fact]
	public async Task RecoveryAfterSuccessfulLoad_RetainsPreviousSnapshotDespiteAttachedNewerMarks()
	{
		using var fixture = new ProfileFixture(totalMarks: 1000);
		var initial = await fixture.LoadAsync();
		fixture.AssertSnapshot(initial);
		var added = new MarkedSecretProfileEntry("ffffffffffff", "NEW", 12);
		var write = await fixture.PhysicalStore.AddMarkAsync(fixture.ProjectPath, added,
			TestContext.Current.CancellationToken);
		Assert.True(write.Succeeded);
		fixture.Store.LookupTransform = result => result with
		{ RecoveryStatus = ProjectProfileLookupStatus.InvalidStorage };

		var recovered = await fixture.LoadAsync();

		Assert.Equal(write.Snapshot!.Revision, fixture.Store.LastLookup!.PersistentMarks!.Revision);
		Assert.Same(initial.PersistentMarks, recovered.PersistentMarks);
		Assert.Same(initial.Profile, recovered.Profile);
		fixture.Store.LookupTransform = null;
		Assert.Equal(write.Snapshot.Revision, (await fixture.LoadAsync()).PersistentMarks!.Revision);
	}

	[Theory(Timeout = 60000)]
	[InlineData(1000)]
	[InlineData(10000)]
	[Trait("Category", "LocalPerformance")]
	public async Task MeasureExistingProfileMarksLoad(int totalMarks)
	{
		Assert.SkipWhen(Environment.GetEnvironmentVariable("DEVPROJEX_PROFILE_MARKS_BENCHMARK") != "1",
			"Set DEVPROJEX_PROFILE_MARKS_BENCHMARK=1 to measure the existing-profile marks load.");

		using var fixture = new ProfileFixture(totalMarks);
		for (var iteration = 0; iteration < 6; iteration++)
		{
			fixture.Store.ResetMeasurement();
			var parsesBefore = fixture.MarkDocumentParseCount;
			var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
			var started = Stopwatch.GetTimestamp();
			var snapshot = await fixture.LoadAsync();
			var totalMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
			var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
			fixture.AssertSnapshot(snapshot);
			output.WriteLine(JsonSerializer.Serialize(new
			{
				TotalActiveMarks = totalMarks,
				Projects = ProfileFixture.ProjectCount,
				fixture.MarksPerProject,
				PrimaryJsonBytes = fixture.JsonBytes,
				BackupJsonBytes = fixture.JsonBytes,
				Iteration = iteration,
				TotalMilliseconds = totalMilliseconds,
				AllocatedBytes = allocatedBytes,
				StateDocumentParses = fixture.MarkDocumentParseCount - parsesBefore,
				fixture.Store.LookupMilliseconds,
				fixture.Store.LookupAllocatedBytes,
				fixture.Store.ExplicitMarkLoadCount,
				fixture.Store.ExplicitMarkLoadMilliseconds,
				fixture.Store.ExplicitMarkLoadAllocatedBytes,
				Revision = snapshot.PersistentMarks!.Revision,
				StateCount = snapshot.PersistentMarks.StateAppliedRevisions!.Count
			}));
		}
	}

	private sealed class ProfileFixture : IDisposable
	{
		internal const int ProjectCount = 20;
		private readonly TemporaryDirectory _workspace = new();
		private readonly SelectionSyncCoordinator _selection;
		private readonly SecretRedactionSession _session;
		private readonly PersistentSecretMarkStore _markStore;

		public ProfileFixture(int totalMarks, bool omitLookupSnapshot = false,
			bool createSelectionProfile = true)
		{
			MarksPerProject = totalMarks / ProjectCount;
			ProjectPath = _workspace.CreateFolder("project-0");
			AppDataPath = _workspace.CreateFolder("app-data");
			PhysicalStore = new ProjectProfileStore(() => AppDataPath);
			if (createSelectionProfile)
				Assert.True(PhysicalStore.TrySaveProfile(ProjectPath, new ProjectSelectionProfile([], [".cs"], [])));
			var database = new PersistentSecretMarkDb { SchemaVersion = 4 };
			for (var projectIndex = 0; projectIndex < ProjectCount; projectIndex++)
			{
				var states = Enumerable.Range(0, MarksPerProject + 1)
					.Select(index => new PersistedSecretMarkState
					{
						Hash = index.ToString("x12", CultureInfo.InvariantCulture),
						Key = "TOKEN_" + index.ToString(CultureInfo.InvariantCulture),
						Length = 12,
						Removed = index == MarksPerProject,
						IssuedUtcTicks = index + 1,
						OperationId = Guid.NewGuid(),
						AppliedRevision = index + 1
					}).ToList();
				database.Projects.Add(PathUtility.Normalize(Path.Combine(_workspace.Path, $"project-{projectIndex}")),
					new PersistedProjectSecretMarks { AppliedRevision = InitialRevision, States = states });
			}
			var json = JsonSerializer.SerializeToUtf8Bytes(database,
				new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
			Assert.True(json.Length < ProjectProfileStorageLimits.MaximumJsonBytes);
			JsonBytes = json.Length;
			var storage = Directory.CreateDirectory(Path.Combine(AppDataPath, "DevProjex")).FullName;
			var marksPath = Path.Combine(storage, "project-secret-marks.json");
			File.WriteAllBytes(marksPath, json);
			File.WriteAllBytes(marksPath + ".bak", json);
			_markStore = (PersistentSecretMarkStore)typeof(ProjectProfileStore)
				.GetField("_persistentMarks", BindingFlags.NonPublic | BindingFlags.Instance)!
				.GetValue(PhysicalStore)!;
			Store = new ObservingStore(PhysicalStore, omitLookupSnapshot);
			var localization = new LocalizationService(new StubLocalizationCatalog(
				new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
				{
					[AppLanguage.En] = new Dictionary<string, string>()
				}), AppLanguage.En);
			var viewModel = new MainWindowViewModel(localization, new HelpContentProvider());
			_selection = new SelectionSyncCoordinator(viewModel,
				new ScanOptionsUseCase(LegacyWorkspaceScannerTestAdapter.Adapt(new StubFileSystemScanner())),
				new FilterOptionSelectionService(), new IgnoreOptionsService(localization),
				_ => new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>()),
				_ => false, () => ProjectPath);
			_session = new SecretRedactionSession(new EmptyDetector(), Store);
			Coordinator = new ProjectProfilePersistenceCoordinator(viewModel, _selection, Store, _session);
		}

		public string ProjectPath { get; }
		public string AppDataPath { get; }
		public int MarksPerProject { get; }
		public int InitialRevision => MarksPerProject + 1;
		public int JsonBytes { get; }
		public ProjectProfileStore PhysicalStore { get; }
		public ObservingStore Store { get; }
		public ProjectProfilePersistenceCoordinator Coordinator { get; }
		public long MarkDocumentParseCount => _markStore.MarkDocumentParseCount;
		public MarkedSecretProfileEntry RemovedMark => new(
			MarksPerProject.ToString("x12", CultureInfo.InvariantCulture), "TOKEN_" + MarksPerProject, 12);
		public PersistentSecretMarkId RemovedIdentity => new(RemovedMark.H, RemovedMark.Length);

		public Task<ProjectProfileLoadSnapshot> LoadAsync() =>
			Coordinator.LoadSnapshotAsync(ProjectPath, TestContext.Current.CancellationToken);

		public void AssertSnapshot(ProjectProfileLoadSnapshot snapshot)
		{
			Assert.Equal(ProjectProfileLookupStatus.Found, snapshot.Status);
			Assert.Equal([".cs"], snapshot.Profile!.SelectedExtensions);
			AssertMarks(snapshot.PersistentMarks!);
			Assert.Equal(snapshot.PersistentMarks!.Marks.OrderBy(mark => mark.H),
				snapshot.Profile.MarkedSecrets!.OrderBy(mark => mark.H));
		}

		public void AssertMarks(PersistentSecretMarksSnapshot snapshot)
		{
			Assert.Equal(InitialRevision, snapshot.Revision);
			Assert.Equal(MarksPerProject, snapshot.Marks.Count);
			Assert.Equal(MarksPerProject + 1, snapshot.StateAppliedRevisions!.Count);
			Assert.Equal(InitialRevision, snapshot.StateAppliedRevisions[RemovedIdentity]);
			Assert.DoesNotContain(RemovedMark, snapshot.Marks);
		}

		public void WriteLegacySelectionProfile(IReadOnlyCollection<MarkedSecretProfileEntry> marks)
		{
			var database = new ProjectProfileDb { SchemaVersion = 3 };
			database.Profiles.Add(PathUtility.Normalize(ProjectPath), new PersistedProjectProfile
			{
				SelectedExtensions = [".cs"],
				MarkedSecrets = marks.ToList(),
				UpdatedUtc = DateTimeOffset.UtcNow
			});
			File.WriteAllText(PhysicalStore.GetPath(), JsonSerializer.Serialize(database,
				new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
		}

		public void Dispose()
		{
			_session.Dispose();
			_selection.Dispose();
			_workspace.Dispose();
		}
	}

	private sealed class ObservingStore(ProjectProfileStore inner, bool omitLookupSnapshot) :
		IProjectProfileStore, IPersistentSecretMarkStore
	{
		public int ExplicitMarkLoadCount { get; private set; }
		public double ExplicitMarkLoadMilliseconds { get; private set; }
		public long ExplicitMarkLoadAllocatedBytes { get; private set; }
		public double LookupMilliseconds { get; private set; }
		public long LookupAllocatedBytes { get; private set; }
		public ProjectProfileLookupResult? LastLookup { get; private set; }
		public Action? AfterLookup { get; set; }
		public Func<ProjectProfileLookupResult, ProjectProfileLookupResult>? LookupTransform { get; set; }

		public void ResetMeasurement()
		{
			ExplicitMarkLoadCount = 0;
			ExplicitMarkLoadMilliseconds = 0;
			ExplicitMarkLoadAllocatedBytes = 0;
			LookupMilliseconds = 0;
			LookupAllocatedBytes = 0;
		}

		public ProjectProfileLookupResult LookupProfile(string localProjectPath, TimeSpan lockTimeout)
		{
			var before = GC.GetAllocatedBytesForCurrentThread();
			var started = Stopwatch.GetTimestamp();
			var result = inner.LookupProfile(localProjectPath, lockTimeout);
			LookupMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
			LookupAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - before;
			result = LookupTransform?.Invoke(result) ?? result;
			LastLookup = result;
			AfterLookup?.Invoke();
			return omitLookupSnapshot
				? new ProjectProfileLookupResult(result.Status, result.Profile, result.UpdatedUtc)
				{ RecoveryStatus = result.RecoveryStatus }
				: result;
		}

		public async ValueTask<PersistentSecretMarksLoadResult> LoadMarksAsync(string localProjectPath,
			CancellationToken cancellationToken = default)
		{
			ExplicitMarkLoadCount++;
			var before = GC.GetTotalAllocatedBytes(precise: true);
			var started = Stopwatch.GetTimestamp();
			var result = await inner.LoadMarksAsync(localProjectPath, cancellationToken);
			ExplicitMarkLoadMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
			ExplicitMarkLoadAllocatedBytes += GC.GetTotalAllocatedBytes(precise: true) - before;
			return result;
		}

		public bool EnsureStorageExists() => inner.EnsureStorageExists();
		public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile) =>
			inner.TryLoadProfile(localProjectPath, out profile);
		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile) =>
			inner.TrySaveProfile(localProjectPath, profile);
		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile, DateTimeOffset updatedUtc) =>
			inner.TrySaveProfile(localProjectPath, profile, updatedUtc);
		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile) =>
			inner.SaveProfile(localProjectPath, profile);
		public ProjectProfileClearStatus ClearAllProfiles() => inner.ClearAllProfiles();
		public ValueTask<PersistentSecretMarkWriteResult> AddMarkAsync(string localProjectPath,
			MarkedSecretProfileEntry mark, CancellationToken cancellationToken = default) =>
			inner.AddMarkAsync(localProjectPath, mark, cancellationToken);
		public ValueTask<PersistentSecretMarkWriteResult> RemoveMarkAsync(string localProjectPath,
			PersistentSecretMarkId markId, CancellationToken cancellationToken = default) =>
			inner.RemoveMarkAsync(localProjectPath, markId, cancellationToken);
		public ValueTask<PersistentSecretMarkWriteResult> ApplyMarkDeltaAsync(string localProjectPath,
			PersistentSecretMarkDelta delta, CancellationToken cancellationToken = default) =>
			inner.ApplyMarkDeltaAsync(localProjectPath, delta, cancellationToken);
	}

	private sealed class EmptyDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(string repositoryRelativePath, string content,
			CancellationToken cancellationToken = default) => [];
	}
}
