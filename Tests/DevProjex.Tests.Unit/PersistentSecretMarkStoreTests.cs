namespace DevProjex.Tests.Unit;

public sealed class PersistentSecretMarkStoreTests
{
	private const string FirstHash = "001122334455";
	private const string SecondHash = "aabbccddeeff";

	[Fact]
	public async Task FutureSchema_IsReadOnlyAcrossLoadMutationsMigrationAndClear()
	{
		using var temporary = new TemporaryDirectory();
		var project = temporary.CreateFolder("project");
		var directory = temporary.CreateFolder("DevProjex");
		var primaryPath = Path.Combine(directory, "project-secret-marks.json");
		var backupPath = primaryPath + ".bak";
		await File.WriteAllTextAsync(
			primaryPath,
			"{\"schemaVersion\":3,\"projects\":{\"future\":{}}}",
			TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(
			backupPath,
			"{\"schemaVersion\":1,\"projects\":{}}",
			TestContext.Current.CancellationToken);
		var primaryBefore = await File.ReadAllBytesAsync(primaryPath, TestContext.Current.CancellationToken);
		var backupBefore = await File.ReadAllBytesAsync(backupPath, TestContext.Current.CancellationToken);
		var store = new PersistentSecretMarkStore(() => temporary.Path);
		var mark = Mark(FirstHash, 12);

		var load = await store.LoadAsync(project, TestContext.Current.CancellationToken);
		var add = await store.AddAsync(project, mark, TestContext.Current.CancellationToken);
		var remove = await store.RemoveAsync(
			project,
			new PersistentSecretMarkId(mark.H, mark.Length),
			TestContext.Current.CancellationToken);
		var migration = store.MergeLegacy(project, [mark], TimeSpan.FromSeconds(1));
		store.ClearAll();

		Assert.Equal(PersistentSecretMarkStoreStatus.UnsupportedFutureSchema, load.Status);
		Assert.Equal(PersistentSecretMarkStoreStatus.UnsupportedFutureSchema, add.Status);
		Assert.Equal(PersistentSecretMarkStoreStatus.UnsupportedFutureSchema, remove.Status);
		Assert.Equal(PersistentSecretMarkStoreStatus.UnsupportedFutureSchema, migration.Status);
		Assert.Equal(primaryBefore, await File.ReadAllBytesAsync(primaryPath, TestContext.Current.CancellationToken));
		Assert.Equal(backupBefore, await File.ReadAllBytesAsync(backupPath, TestContext.Current.CancellationToken));
	}

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
		var add = PersistentSecretMarkDelta.Add(mark, observedRevision: 0);

		var added = await store.ApplyMarkDeltaAsync(project, add, cancellationToken);
		Assert.True(added.Succeeded);
		var remove = PersistentSecretMarkDelta.Remove(
			new PersistentSecretMarkId(mark.H, mark.Length),
			added.Snapshot!.Revision);
		Assert.True((await store.ApplyMarkDeltaAsync(project, remove, cancellationToken)).Succeeded);
		var afterRemove = await store.LoadMarksAsync(project, cancellationToken);
		Assert.True((await store.ApplyMarkDeltaAsync(project, add, cancellationToken)).Succeeded);
		var afterOldAddRetry = await store.LoadMarksAsync(project, cancellationToken);

		Assert.Empty(afterRemove.Snapshot!.Marks);
		Assert.Empty(afterOldAddRetry.Snapshot!.Marks);
		Assert.Equal(afterRemove.Snapshot.Revision, afterOldAddRetry.Snapshot.Revision);

		var newerAdd = PersistentSecretMarkDelta.Add(mark, afterRemove.Snapshot.Revision);
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
				OperationId = Guid.NewGuid(),
				AppliedRevision = index + 1
			})
			.ToList();
		var database = new PersistentSecretMarkDb
		{
			SchemaVersion = 2,
			Projects = new Dictionary<string, PersistedProjectSecretMarks>(PathComparer.Default)
			{
				[PathUtility.Normalize(project)] = new PersistedProjectSecretMarks
				{
					AppliedRevision = states.Count,
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
		Assert.True((await store.AddMarkAsync(
			project,
			v2,
			TestContext.Current.CancellationToken)).Succeeded);
		var observed = await store.LoadMarksAsync(project, TestContext.Current.CancellationToken);
		var migration = PersistentSecretMarkDelta.Replace(
			new PersistentSecretMarkId(legacy.H, legacy.Length),
			v2,
			observed.Snapshot!.Revision);

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
			added.Snapshot!.Revision,
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

	[Fact]
	public async Task CausalOrdering_IgnoresWallClockSkewAndRejectsOldAddRetry()
	{
		using var temporary = new TemporaryDirectory();
		var project = temporary.CreateFolder("project");
		var store = new ProjectProfileStore(() => temporary.Path);
		var mark = Mark(FirstHash, 12);
		var add = new PersistentSecretMarkDelta(
			Guid.NewGuid(),
			DateTime.UtcNow.AddHours(12).Ticks,
			0,
			PersistentSecretMarkDeltaKind.Add,
			new PersistentSecretMarkId(mark.H, mark.Length),
			mark);

		var added = await store.ApplyMarkDeltaAsync(project, add, TestContext.Current.CancellationToken);
		var remove = new PersistentSecretMarkDelta(
			Guid.NewGuid(),
			DateTime.UtcNow.AddHours(-12).Ticks,
			added.Snapshot!.Revision,
			PersistentSecretMarkDeltaKind.Remove,
			new PersistentSecretMarkId(mark.H, mark.Length),
			null);
		var removed = await store.ApplyMarkDeltaAsync(project, remove, TestContext.Current.CancellationToken);
		var retried = await store.ApplyMarkDeltaAsync(project, add, TestContext.Current.CancellationToken);

		Assert.True(removed.Succeeded);
		Assert.Empty(removed.Snapshot!.Marks);
		var identity = new PersistentSecretMarkId(mark.H, mark.Length);
		Assert.Equal(2, removed.Snapshot.StateAppliedRevisions![identity]);
		Assert.True(retried.Succeeded);
		Assert.Empty(retried.Snapshot!.Marks);
		Assert.Equal(2, retried.Snapshot.StateAppliedRevisions![identity]);
		Assert.Equal(removed.Snapshot.Revision, retried.Snapshot.Revision);
	}

	[Fact]
	public async Task ReplayedOperationId_IsAnIdempotentNoOp()
	{
		using var temporary = new TemporaryDirectory();
		var project = temporary.CreateFolder("project");
		var store = new ProjectProfileStore(() => temporary.Path);
		var delta = PersistentSecretMarkDelta.Add(Mark(FirstHash, 12), observedRevision: 0);

		var first = await store.ApplyMarkDeltaAsync(project, delta, TestContext.Current.CancellationToken);
		var replay = await store.ApplyMarkDeltaAsync(project, delta, TestContext.Current.CancellationToken);

		Assert.True(first.Succeeded);
		Assert.True(replay.Succeeded);
		Assert.Equal(first.Snapshot!.Revision, replay.Snapshot!.Revision);
		Assert.Equal(first.Snapshot.Marks, replay.Snapshot.Marks);
	}

	[Fact]
	public async Task IndependentConcurrentDeltas_ConvergeRegardlessOfArrivalOrder()
	{
		using var firstRoot = new TemporaryDirectory();
		using var secondRoot = new TemporaryDirectory();
		var firstProject = firstRoot.CreateFolder("project");
		var secondProject = secondRoot.CreateFolder("project");
		var firstDelta = PersistentSecretMarkDelta.Add(Mark(FirstHash, 12), observedRevision: 0);
		var secondDelta = PersistentSecretMarkDelta.Add(Mark(SecondHash, 16), observedRevision: 0);
		var firstStore = new ProjectProfileStore(() => firstRoot.Path);
		var secondStore = new ProjectProfileStore(() => secondRoot.Path);

		await firstStore.ApplyMarkDeltaAsync(firstProject, firstDelta, TestContext.Current.CancellationToken);
		await firstStore.ApplyMarkDeltaAsync(firstProject, secondDelta, TestContext.Current.CancellationToken);
		await secondStore.ApplyMarkDeltaAsync(secondProject, secondDelta, TestContext.Current.CancellationToken);
		await secondStore.ApplyMarkDeltaAsync(secondProject, firstDelta, TestContext.Current.CancellationToken);
		var first = await firstStore.LoadMarksAsync(firstProject, TestContext.Current.CancellationToken);
		var second = await secondStore.LoadMarksAsync(secondProject, TestContext.Current.CancellationToken);

		Assert.Equal(2, first.Snapshot!.Revision);
		Assert.Equal(first.Snapshot.Revision, second.Snapshot!.Revision);
		Assert.Equal(
			first.Snapshot.Marks.OrderBy(static mark => mark.H).ToArray(),
			second.Snapshot.Marks.OrderBy(static mark => mark.H).ToArray());
	}

	[Fact]
	public async Task SchemaOneOrdering_IsMigratedOnceToAppliedRevisions()
	{
		using var temporary = new TemporaryDirectory();
		var project = temporary.CreateFolder("project");
		var directory = temporary.CreateFolder("DevProjex");
		var normalizedProject = PathUtility.Normalize(project).Replace("\\", "\\\\", StringComparison.Ordinal);
		var olderOperation = Guid.NewGuid();
		var newerOperation = Guid.NewGuid();
		var json = $$"""
			{
			  "schemaVersion": 1,
			  "projects": {
			    "{{normalizedProject}}": {
			      "revision": 1,
			      "states": [
			        { "hash": "{{FirstHash}}", "length": 12, "key": "TOKEN", "removed": false, "issuedUtcTicks": 10, "operationId": "{{olderOperation}}" },
			        { "hash": "{{FirstHash}}", "length": 12, "key": null, "removed": true, "issuedUtcTicks": 20, "operationId": "{{newerOperation}}" }
			      ]
			    }
			  }
			}
			""";
		var path = Path.Combine(directory, "project-secret-marks.json");
		await File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken);
		var store = new ProjectProfileStore(() => temporary.Path);

		var migrated = await store.LoadMarksAsync(project, TestContext.Current.CancellationToken);
		var firstBytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
		var loadedAgain = await store.LoadMarksAsync(project, TestContext.Current.CancellationToken);
		var secondBytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
		var staleAdd = PersistentSecretMarkDelta.Add(Mark(FirstHash, 12), observedRevision: 0);
		var afterStale = await store.ApplyMarkDeltaAsync(project, staleAdd, TestContext.Current.CancellationToken);

		Assert.True(migrated.Succeeded);
		Assert.Empty(migrated.Snapshot!.Marks);
		Assert.Equal(migrated.Snapshot.Revision, loadedAgain.Snapshot!.Revision);
		Assert.Equal(migrated.Snapshot.Marks, loadedAgain.Snapshot.Marks);
		Assert.Equal(
			migrated.Snapshot.StateAppliedRevisions!.OrderBy(static pair => pair.Key.Hash),
			loadedAgain.Snapshot.StateAppliedRevisions!.OrderBy(static pair => pair.Key.Hash));
		Assert.Equal(firstBytes, secondBytes);
		Assert.Contains("\"schemaVersion\": 2", Encoding.UTF8.GetString(firstBytes), StringComparison.Ordinal);
		Assert.Contains("\"appliedRevision\"", Encoding.UTF8.GetString(firstBytes), StringComparison.Ordinal);
		Assert.Empty(afterStale.Snapshot!.Marks);
		Assert.Equal(migrated.Snapshot.Revision, afterStale.Snapshot.Revision);
	}

	private static MarkedSecretProfileEntry Mark(string hash, int length) =>
		new(hash, "TOKEN", length);
}
