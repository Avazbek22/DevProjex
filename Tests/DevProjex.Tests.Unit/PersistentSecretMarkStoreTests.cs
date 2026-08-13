namespace DevProjex.Tests.Unit;

public sealed class PersistentSecretMarkStoreTests
{
	private const string FirstHash = "001122334455";
	private const string SecondHash = "aabbccddeeff";

	[Fact]
	public async Task IndependentStores_AddDistinctMarks_PreserveBothDeltas()
	{
		using var temporary = new TemporaryDirectory();
		var cancellationToken = TestContext.Current.CancellationToken;
		var project = temporary.CreateFolder("project");
		var storeA = new ProjectProfileStore(() => temporary.Path);
		var storeB = new ProjectProfileStore(() => temporary.Path);

		var first = await storeA.AddMarkAsync(project, Mark(FirstHash, 12), cancellationToken);
		var second = await storeB.AddMarkAsync(project, Mark(SecondHash, 16), cancellationToken);
		var loaded = await new ProjectProfileStore(() => temporary.Path).LoadMarksAsync(project, cancellationToken);

		Assert.True(first.Succeeded);
		Assert.True(second.Succeeded);
		Assert.True(loaded.Succeeded);
		Assert.Equal(2, loaded.Snapshot!.Marks.Count);
		Assert.Contains(loaded.Snapshot.Marks, mark => mark.H == FirstHash);
		Assert.Contains(loaded.Snapshot.Marks, mark => mark.H == SecondHash);
	}

	[Fact]
	public async Task SelectionSave_WithStaleMarkedSecrets_DoesNotResurrectRemovedMark()
	{
		using var temporary = new TemporaryDirectory();
		var cancellationToken = TestContext.Current.CancellationToken;
		var project = temporary.CreateFolder("project");
		var storeA = new ProjectProfileStore(() => temporary.Path);
		var storeB = new ProjectProfileStore(() => temporary.Path);
		var mark = Mark(FirstHash, 12);
		Assert.True((await storeA.AddMarkAsync(project, mark, cancellationToken)).Succeeded);
		Assert.True((await storeA.RemoveMarkAsync(
			project,
			new PersistentSecretMarkId(mark.H, mark.Length),
			cancellationToken)).Succeeded);

		Assert.True(storeB.TrySaveProfile(
			project,
			new ProjectSelectionProfile([], [".cs"], [], MarkedSecrets: [mark])));
		var loaded = await storeA.LoadMarksAsync(project, cancellationToken);

		Assert.True(loaded.Succeeded);
		Assert.Empty(loaded.Snapshot!.Marks);
		var selectionJson = File.ReadAllText(Path.Combine(temporary.Path, "DevProjex", "project-profiles.json"));
		Assert.DoesNotContain("\"markedSecrets\"", selectionJson, StringComparison.Ordinal);
	}

	[Fact]
	public async Task DelayedOlderDeltas_AfterNewerMutations_AreIdempotent()
	{
		using var temporary = new TemporaryDirectory();
		var cancellationToken = TestContext.Current.CancellationToken;
		var project = temporary.CreateFolder("project");
		var store = new ProjectProfileStore(() => temporary.Path);
		var mark = Mark(FirstHash, 12);
		var add = PersistentSecretMarkDelta.Add(mark);
		var remove = PersistentSecretMarkDelta.Remove(new PersistentSecretMarkId(mark.H, mark.Length));

		Assert.True((await store.ApplyMarkDeltaAsync(project, add, cancellationToken)).Succeeded);
		Assert.True((await store.ApplyMarkDeltaAsync(project, remove, cancellationToken)).Succeeded);
		var afterRemove = await store.LoadMarksAsync(project, cancellationToken);
		Assert.True((await store.ApplyMarkDeltaAsync(project, add, cancellationToken)).Succeeded);
		var afterOldAddRetry = await store.LoadMarksAsync(project, cancellationToken);

		Assert.Empty(afterRemove.Snapshot!.Marks);
		Assert.Empty(afterOldAddRetry.Snapshot!.Marks);
		Assert.Equal(afterRemove.Snapshot.Revision, afterOldAddRetry.Snapshot.Revision);

		var newerAdd = PersistentSecretMarkDelta.Add(mark);
		Assert.True((await store.ApplyMarkDeltaAsync(project, newerAdd, cancellationToken)).Succeeded);
		Assert.True((await store.ApplyMarkDeltaAsync(project, remove, cancellationToken)).Succeeded);
		var afterOldRemoveRetry = await store.LoadMarksAsync(project, cancellationToken);
		Assert.Single(afterOldRemoveRetry.Snapshot!.Marks);
	}

	[Fact]
	public async Task SuccessfulAdd_IsDurableBeforeAcknowledgement()
	{
		using var temporary = new TemporaryDirectory();
		var cancellationToken = TestContext.Current.CancellationToken;
		var project = temporary.CreateFolder("project");
		var writer = new ProjectProfileStore(() => temporary.Path);

		var result = await writer.AddMarkAsync(project, Mark(FirstHash, 12), cancellationToken);
		var restarted = new ProjectProfileStore(() => temporary.Path);
		var loaded = await restarted.LoadMarksAsync(project, cancellationToken);

		Assert.True(result.Succeeded);
		Assert.True(loaded.Succeeded);
		Assert.Single(loaded.Snapshot!.Marks);
	}

	[Fact]
	public async Task UnavailableLock_ReturnsFailure_AndSameTypedDeltaRetriesAfterRelease()
	{
		using var temporary = new TemporaryDirectory();
		var cancellationToken = TestContext.Current.CancellationToken;
		var project = temporary.CreateFolder("project");
		var store = new ProjectProfileStore(() => temporary.Path);
		var storeDirectory = Path.Combine(temporary.Path, "DevProjex");
		Directory.CreateDirectory(storeDirectory);
		var lockPath = Path.Combine(storeDirectory, "project-secret-marks.json.lock");
		var delta = PersistentSecretMarkDelta.Add(Mark(FirstHash, 12));

		PersistentSecretMarkWriteResult unavailable;
		using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
		{
			unavailable = await store.ApplyMarkDeltaAsync(project, delta, cancellationToken);
		}
		var retried = await store.ApplyMarkDeltaAsync(project, delta, cancellationToken);
		var loaded = await store.LoadMarksAsync(project, cancellationToken);

		Assert.Equal(PersistentSecretMarkStoreStatus.TemporarilyUnavailable, unavailable.Status);
		Assert.True(retried.Succeeded);
		Assert.Single(loaded.Snapshot!.Marks);
		Assert.Equal(retried.Snapshot!.Revision, loaded.Snapshot.Revision);
	}

	[Fact]
	public async Task SelectionProfilePruning_DoesNotEvictPersistentMarks()
	{
		using var temporary = new TemporaryDirectory();
		var cancellationToken = TestContext.Current.CancellationToken;
		var store = new ProjectProfileStore(() => temporary.Path);
		var markedProject = temporary.CreateFolder("projects/project-000");
		Assert.True((await store.AddMarkAsync(markedProject, Mark(FirstHash, 12), cancellationToken)).Succeeded);

		for (var index = 0; index < 510; index++)
		{
			var project = temporary.CreateFolder($"projects/project-{index:D3}");
			Assert.True(store.TrySaveProfile(project, new ProjectSelectionProfile([], [".cs"], [])));
		}

		var loaded = await new ProjectProfileStore(() => temporary.Path).LoadMarksAsync(markedProject, cancellationToken);
		Assert.True(loaded.Succeeded);
		Assert.Single(loaded.Snapshot!.Marks);
	}

	[Fact]
	public async Task LegacySelectionMarks_AreMigratedOnceAndRemainReadable()
	{
		using var temporary = new TemporaryDirectory();
		var cancellationToken = TestContext.Current.CancellationToken;
		var project = temporary.CreateFolder("project");
		var storeDirectory = temporary.CreateFolder("DevProjex");
		var normalizedProject = PathUtility.Normalize(project).Replace("\\", "\\\\", StringComparison.Ordinal);
		var legacyJson = $$"""
			{
			  "schemaVersion": 3,
			  "profiles": {
			    "{{normalizedProject}}": {
			      "selectedRootFolders": [],
			      "selectedExtensions": [".cs"],
			      "selectedIgnoreOptions": [],
			      "rootFolderStates": {},
			      "extensionStates": { ".cs": true },
			      "ignoreOptionStates": {},
			      "selectedPaths": [],
			      "markedSecrets": [ { "h": "{{FirstHash}}", "key": "TOKEN", "length": 12 } ],
			      "updatedUtc": "2026-01-01T00:00:00+00:00"
			    }
			  }
			}
			""";
		File.WriteAllText(Path.Combine(storeDirectory, "project-profiles.json"), legacyJson);
		var store = new ProjectProfileStore(() => temporary.Path);

		Assert.True(store.TryLoadProfile(project, out var firstLoad));
		Assert.Single(firstLoad.MarkedSecrets!);
		Assert.True(store.TryLoadProfile(project, out var secondLoad));
		Assert.Single(secondLoad.MarkedSecrets!);
		var marks = await store.LoadMarksAsync(project, cancellationToken);
		Assert.Single(marks.Snapshot!.Marks);
		var rewrittenSelection = File.ReadAllText(Path.Combine(storeDirectory, "project-profiles.json"));
		Assert.DoesNotContain("\"markedSecrets\"", rewrittenSelection, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RepeatedLegacyMigration_DoesNotResurrectTombstonedMark()
	{
		using var temporary = new TemporaryDirectory();
		var project = temporary.CreateFolder("project");
		var mark = Mark(FirstHash, 12);
		var store = new PersistentSecretMarkStore(() => temporary.Path);

		var migrated = store.MergeLegacy(project, [mark], TimeSpan.FromSeconds(1));
		var removed = await store.RemoveAsync(
			project,
			new PersistentSecretMarkId(mark.H, mark.Length),
			TestContext.Current.CancellationToken);
		var repeated = store.MergeLegacy(project, [mark], TimeSpan.FromSeconds(1));

		Assert.True(migrated.Succeeded);
		Assert.True(removed.Succeeded);
		Assert.True(repeated.Succeeded);
		Assert.Empty(repeated.Snapshot!.Marks);
		Assert.Equal(removed.Snapshot!.Revision, repeated.Snapshot.Revision);
	}

	[Fact]
	public async Task TombstoneCapacity_RejectsNewIdentityWithoutDiscardingExistingState()
	{
		using var temporary = new TemporaryDirectory();
		var cancellationToken = TestContext.Current.CancellationToken;
		var project = temporary.CreateFolder("project");
		var directory = temporary.CreateFolder("DevProjex");
		var states = Enumerable.Range(0, ProjectProfileStorageLimits.MaximumPersistentMarkStatesPerProject)
			.Select(index => new PersistedSecretMarkState
			{
				Hash = index.ToString("x12", CultureInfo.InvariantCulture),
				Length = 8,
				Removed = true,
				IssuedUtcTicks = index + 1,
				OperationId = Guid.NewGuid()
			})
			.ToList();
		var database = new PersistentSecretMarkDb
		{
			SchemaVersion = 1,
			Projects = new Dictionary<string, PersistedProjectSecretMarks>(PathComparer.Default)
			{
				[PathUtility.Normalize(project)] = new PersistedProjectSecretMarks
				{
					Revision = 1,
					States = states
				}
			}
		};
		File.WriteAllText(
			Path.Combine(directory, "project-secret-marks.json"),
			JsonSerializer.Serialize(database, new JsonSerializerOptions
			{
				PropertyNamingPolicy = JsonNamingPolicy.CamelCase
			}));
		var store = new ProjectProfileStore(() => temporary.Path);

		var result = await store.AddMarkAsync(project, Mark("ffffffffffff", 12), cancellationToken);
		var loaded = await store.LoadMarksAsync(project, cancellationToken);

		Assert.Equal(PersistentSecretMarkStoreStatus.WriteFailed, result.Status);
		Assert.True(loaded.Succeeded);
		Assert.Empty(loaded.Snapshot!.Marks);
	}

	[Fact]
	public void OperationClock_RejectsOverflowInsteadOfWrappingOrdering()
	{
		Assert.Equal(43, PersistentSecretMarkDelta.CalculateNextIssuedUtcTicks(42, 10));
		Assert.Equal(100, PersistentSecretMarkDelta.CalculateNextIssuedUtcTicks(42, 100));
		Assert.Throws<InvalidOperationException>(() =>
			PersistentSecretMarkDelta.CalculateNextIssuedUtcTicks(long.MaxValue, long.MaxValue));
	}

	[Fact]
	public async Task LegacyReplacement_WithNewerV2State_TombstonesLegacyWithoutOverwritingV2()
	{
		using var temporary = new TemporaryDirectory();
		var project = temporary.CreateFolder("project");
		var store = new ProjectProfileStore(() => temporary.Path);
		var legacy = Mark(FirstHash, 12);
		var v2 = legacy with { H = "v2:" + new string('a', 64), Key = "NEWER" };
		Assert.True((await store.AddMarkAsync(
			project,
			legacy,
			TestContext.Current.CancellationToken)).Succeeded);
		var migration = PersistentSecretMarkDelta.Replace(
			new PersistentSecretMarkId(legacy.H, legacy.Length),
			v2);
		Assert.True((await store.AddMarkAsync(
			project,
			v2,
			TestContext.Current.CancellationToken)).Succeeded);

		var migrated = await store.ApplyMarkDeltaAsync(
			project,
			migration,
			TestContext.Current.CancellationToken);

		Assert.True(migrated.Succeeded);
		Assert.Equal(v2, Assert.Single(migrated.Snapshot!.Marks));
	}

	[Fact]
	public async Task MalformedTypedDeltas_AreNoOpsAndDoNotAdvanceRevision()
	{
		using var temporary = new TemporaryDirectory();
		var project = temporary.CreateFolder("project");
		var store = new ProjectProfileStore(() => temporary.Path);
		var mark = Mark(FirstHash, 12);
		var added = await store.AddMarkAsync(project, mark, TestContext.Current.CancellationToken);
		Assert.True(added.Succeeded);
		var sameIdentityReplacement = new PersistentSecretMarkDelta(
			Guid.NewGuid(),
			DateTime.UtcNow.Ticks + 1,
			PersistentSecretMarkDeltaKind.Replace,
			new PersistentSecretMarkId(mark.H, mark.Length),
			mark with { Key = "REPLACED" });
		var unknownKind = sameIdentityReplacement with
		{
			OperationId = Guid.NewGuid(),
			IssuedUtcTicks = sameIdentityReplacement.IssuedUtcTicks + 1,
			Kind = (PersistentSecretMarkDeltaKind)int.MaxValue
		};

		var afterReplacement = await store.ApplyMarkDeltaAsync(
			project,
			sameIdentityReplacement,
			TestContext.Current.CancellationToken);
		var afterUnknown = await store.ApplyMarkDeltaAsync(
			project,
			unknownKind,
			TestContext.Current.CancellationToken);

		Assert.True(afterReplacement.Succeeded);
		Assert.True(afterUnknown.Succeeded);
		Assert.Equal(added.Snapshot!.Revision, afterUnknown.Snapshot!.Revision);
		Assert.Equal(mark, Assert.Single(afterUnknown.Snapshot.Marks));
	}

	private static MarkedSecretProfileEntry Mark(string hash, int length) =>
		new(hash, "TOKEN", length);
}
