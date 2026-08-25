using DevProjex.Infrastructure.RecentProjects;
using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Tests.Unit;

public sealed class RecentProjectsStoreTests
{
	[Fact]
	public void GetPath_IncludesExpectedSegments()
	{
		var store = new RecentProjectsStore();
		var path = store.GetPath();

		Assert.EndsWith(Path.Combine("DevProjex", "recent-projects.json"), path);
	}

	[Fact]
	public void StartupMigratesLegacyConfigurationStateWithoutDeletingLegacyData()
	{
		using var temp = new TemporaryDirectory();
		var stateRoot = temp.CreateFolder("state");
		var legacyConfigurationRoot = temp.CreateFolder("config");
		var workspace = temp.CreateFolder("workspace");
		var legacyStore = new RecentProjectsStore(() => legacyConfigurationRoot);
		legacyStore.AddFolder(null, workspace);
		var legacyPath = legacyStore.GetPath();
		var store = new RecentProjectsStore(
			() => stateRoot,
			() => legacyConfigurationRoot);

		var beforeStartup = store.Load();

		Assert.Equal(PathUtility.Normalize(workspace), Assert.Single(beforeStartup.RecentFolders).Path);
		Assert.False(File.Exists(store.GetPath()));

		var migrated = store.LoadForStartup(TimeSpan.Zero);

		Assert.Equal(PathUtility.Normalize(workspace), Assert.Single(migrated.RecentFolders).Path);
		Assert.True(File.Exists(store.GetPath()));
		Assert.True(File.Exists(store.GetPath() + ".bak"));
		Assert.True(File.Exists(legacyPath));
	}

	[Fact]
	public void Load_PropagatesUnexpectedLegacyPathProviderFailure()
	{
		using var temp = new TemporaryDirectory();
		var expected = new ApplicationException("unexpected provider failure");
		var store = new RecentProjectsStore(
			() => temp.CreateFolder("state"),
			() => throw expected);

		var actual = Assert.Throws<ApplicationException>(() => store.Load());

		Assert.Same(expected, actual);
	}

	[Fact]
	public void AddFolder_MovesDuplicateToFront_AndPersists()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var db = store.Load();
		var folderA = Path.Combine(temp.Path, "FolderA");
		var folderB = Path.Combine(temp.Path, "FolderB");

		db = store.AddFolder(db, folderA);
		db = store.AddFolder(db, folderB);
		db = store.AddFolder(db, folderA);

		Assert.Equal(2, db.RecentFolders.Count);
		Assert.Equal(Path.GetFullPath(folderA).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), db.RecentFolders[0].Path);
		Assert.Equal(Path.GetFullPath(folderB).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), db.RecentFolders[1].Path);
		Assert.True(File.Exists(store.GetPath()));
	}

	[Fact]
	public void AddFolder_CreatesBackupSnapshotAlongsidePrimaryFile()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var db = store.Load();

		store.AddFolder(db, Path.Combine(temp.Path, "FolderA"));

		Assert.True(File.Exists(store.GetPath()));
		Assert.True(File.Exists(store.GetPath() + ".bak"));
	}

	[Fact]
	public void EnsureStorageExists_CreatesPrimaryAndBackup_WhenFilesAreMissing()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);

		Assert.True(store.EnsureStorageExists());
		Assert.True(File.Exists(store.GetPath()));
		Assert.True(File.Exists(store.GetPath() + ".bak"));

		var loaded = store.Load();
		Assert.Empty(loaded.RecentFolders);
		Assert.Empty(loaded.RecentRepositories);
	}

	[Fact]
	public void EnsureStorageExists_RecreatesMissingBackup_FromPrimarySnapshot()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var db = store.Load();

		store.AddFolder(db, Path.Combine(temp.Path, "FolderA"));
		File.Delete(store.GetPath() + ".bak");

		Assert.True(store.EnsureStorageExists());
		Assert.True(File.Exists(store.GetPath() + ".bak"));

		var reloaded = store.Load();
		Assert.Single(reloaded.RecentFolders);
	}

	[Fact]
	public void LoadForStartup_WhenStoreLockIsHeld_ReturnsDefaultWithinBoundedTime()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var lockPath = store.GetPath() + ".lock";
		Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
		using var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();

		var loaded = store.LoadForStartup(TimeSpan.FromMilliseconds(25));

		stopwatch.Stop();
		Assert.Empty(loaded.RecentFolders);
		Assert.Empty(loaded.RecentRepositories);
		Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Startup load took {stopwatch.Elapsed}.");
	}

	[Fact]
	public void LoadForStartup_WhenStoreIsAvailable_ReturnsPersistedHistory()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var folderPath = Path.Combine(temp.Path, "Workspace");
		store.AddFolder(store.Load(), folderPath);

		var loaded = store.LoadForStartup(TimeSpan.FromMilliseconds(25));

		var folder = Assert.Single(loaded.RecentFolders);
		Assert.Equal(Path.GetFullPath(folderPath), folder.Path);
	}

	[Fact]
	public void TryPersist_DetachedSnapshot_WritesPrimaryAndBackup()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var snapshot = new RecentProjectsDb
		{
			SchemaVersion = 1,
			RecentFolders =
			[
				new RecentFolderEntry
				{
					Path = Path.Combine(temp.Path, "Workspace"),
					OpenedUtc = DateTimeOffset.UtcNow
				}
			],
			RecentRepositories =
			[
				new RecentRepositoryEntry
				{
					Url = "https://github.com/example/repo",
					OpenedUtc = DateTimeOffset.UtcNow
				}
			]
		};

		Assert.True(store.TryPersist(snapshot));
		Assert.True(File.Exists(store.GetPath()));
		Assert.True(File.Exists(store.GetPath() + ".bak"));

		var reloaded = store.Load();
		Assert.Single(reloaded.RecentFolders);
		Assert.Single(reloaded.RecentRepositories);
	}

	[Fact]
	public void TryPersist_InvalidAppDataPath_ReturnsFalse()
	{
		var invalidRoot = string.Concat("broken", '\0', "root");
		var store = new RecentProjectsStore(() => invalidRoot);
		var snapshot = new RecentProjectsDb
		{
			SchemaVersion = 1,
			RecentFolders =
			[
				new RecentFolderEntry
				{
					Path = Path.GetTempPath(),
					OpenedUtc = DateTimeOffset.UtcNow
				}
			],
			RecentRepositories = []
		};

		Assert.False(store.TryPersist(snapshot));
	}

	[Fact]
	public void AddFolder_ClampsToThirtyTwoItems()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var db = store.Load();

		for (var i = 0; i < 34; i++)
			db = store.AddFolder(db, Path.Combine(temp.Path, $"Folder{i}"));

		Assert.Equal(32, db.RecentFolders.Count);
		Assert.Contains(db.RecentFolders, entry => entry.Path.EndsWith("Folder33", StringComparison.Ordinal));
		Assert.DoesNotContain(db.RecentFolders, entry => entry.Path.EndsWith("Folder0", StringComparison.Ordinal));
	}

	[Fact]
	public void AddRepository_NormalizesUrl_AndDeduplicates()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var db = store.Load();

		db = store.AddRepository(db, "https://github.com/user/repo/");
		db = store.AddRepository(db, "https://github.com/user/repo?ref=main");

		Assert.Single(db.RecentRepositories);
		Assert.Equal("https://github.com/user/repo", db.RecentRepositories[0].Url);
	}

	[Fact]
	public void AddRepository_ClampsToSixteenItems()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var db = store.Load();

		for (var i = 0; i < 18; i++)
			db = store.AddRepository(db, $"https://example.com/user/repo{i}");

		Assert.Equal(16, db.RecentRepositories.Count);
		Assert.Contains(db.RecentRepositories, entry => entry.Url.EndsWith("repo17", StringComparison.Ordinal));
		Assert.DoesNotContain(db.RecentRepositories, entry => entry.Url.EndsWith("repo0", StringComparison.Ordinal));
	}

	[Fact]
	public void AddRepository_DeduplicatesGitSuffixVariants_AndKeepsLatestValue()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var db = store.Load();

		db = store.AddRepository(db, "https://github.com/user/repo.git");
		db = store.AddRepository(db, "https://github.com/user/repo");

		Assert.Single(db.RecentRepositories);
		Assert.Equal("https://github.com/user/repo", db.RecentRepositories[0].Url);
	}

	[Fact]
	public void AddFolder_DoesNotPolluteRecentRepositories()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var db = store.Load();

		db = store.AddFolder(db, Path.Combine(temp.Path, "FolderA"));

		Assert.Single(db.RecentFolders);
		Assert.Empty(db.RecentRepositories);
	}

	[Fact]
	public void AddFolder_IgnoresRepoCachePath()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var db = store.Load();
		var repoCachePath = Path.Combine(Path.GetTempPath(), "DevProjex", "RepoCache", "repo_123");

		db = store.AddFolder(db, repoCachePath);

		Assert.Empty(db.RecentFolders);
		Assert.False(File.Exists(store.GetPath()));
	}

	[Fact]
	public void AddFolder_IgnoresApplicationStateDirectory()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var db = store.Load();
		var applicationStateDirectory = Path.Combine(temp.Path, "DevProjex");
		Directory.CreateDirectory(applicationStateDirectory);

		db = store.AddFolder(db, applicationStateDirectory);

		Assert.Empty(db.RecentFolders);
		Assert.False(File.Exists(store.GetPath()));
	}

	[Fact]
	public void AddFolder_DeduplicatesLegacyTrailingSeparatorVariants_AndKeepsLatestValue()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var db = store.Load();
		var folder = temp.CreateFolder("Workspace");

		db = store.AddFolder(db, folder);
		db = store.AddFolder(db, folder + Path.DirectorySeparatorChar);

		Assert.Single(db.RecentFolders);
		Assert.Equal(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), db.RecentFolders[0].Path);
	}

	[Fact]
	public void AddRepository_DoesNotPolluteRecentFolders()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var db = store.Load();

		db = store.AddRepository(db, "https://github.com/user/repo");

		Assert.Empty(db.RecentFolders);
		Assert.Single(db.RecentRepositories);
	}

	[Fact]
	public void Load_RemovesRepoCacheFolders_FromLegacyData()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);

		var regularFolder = temp.CreateFolder("RegularFolder");
		var db = store.Load();
		db = store.AddFolder(db, regularFolder);

		var path = store.GetPath();
		var json = File.ReadAllText(path);
		var legacyRepoCachePath = Path.Combine(Path.GetTempPath(), "DevProjex", "RepoCache", "repo_legacy");
		json = json.Replace(regularFolder.Replace("\\", "\\\\"), legacyRepoCachePath.Replace("\\", "\\\\"));
		File.WriteAllText(path, json);

		var loaded = store.Load();

		Assert.Empty(loaded.RecentFolders);
	}

	[Fact]
	public void Load_InvalidJson_ReturnsDefaultWithoutDestroyingOriginalFile()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var filePath = store.GetPath();
		Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
		const string invalidJson = "{ definitely-not-json";
		File.WriteAllText(filePath, invalidJson);

		var loaded = store.Load();

		Assert.Equal(3, loaded.SchemaVersion);
		Assert.Empty(loaded.RecentFolders);
		Assert.Empty(loaded.RecentRepositories);
		Assert.Equal(invalidJson, File.ReadAllText(filePath));
	}

	[Fact]
	public void Load_OversizedPrimary_ReturnsDefaultWithoutMaterializingOrDestroyingFile()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var filePath = store.GetPath();
		Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
		using (var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write))
			stream.SetLength(JsonStorePersistence.SmallDocumentMaximumBytes + 1);

		var loaded = store.Load();

		Assert.Equal(3, loaded.SchemaVersion);
		Assert.Empty(loaded.RecentFolders);
		Assert.Empty(loaded.RecentRepositories);
		Assert.Equal(JsonStorePersistence.SmallDocumentMaximumBytes + 1, new FileInfo(filePath).Length);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void FutureSchemaInPrimaryOrBackup_IsPreservedAndBlocksPersistence(bool useBackup)
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var primaryPath = store.GetPath();
		var futurePath = useBackup ? primaryPath + ".bak" : primaryPath;
		const string futureJson = """
		{
		  "schemaVersion": 999,
		  "recentFolders": [],
		  "recentRepositories": [],
		  "futureHistory": { "keep": true }
		}
		""";
		Directory.CreateDirectory(Path.GetDirectoryName(primaryPath)!);
		File.WriteAllText(futurePath, futureJson);

		var loaded = store.Load();
		var updated = store.AddRepository(loaded, "https://github.com/example/new-repository");

		Assert.Empty(loaded.RecentRepositories);
		Assert.Single(updated.RecentRepositories);
		Assert.False(store.TryPersist(updated));
		Assert.True(store.EnsureStorageExists());
		Assert.Equal(futureJson, File.ReadAllText(futurePath));
		Assert.Equal(!useBackup, File.Exists(primaryPath));
	}

	[Fact]
	public void Load_RemovesApplicationStateDirectory_FromLegacyData()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var validFolder = temp.CreateFolder("Workspace");
		var applicationStateDirectory = Path.Combine(temp.Path, "DevProjex");
		Directory.CreateDirectory(applicationStateDirectory);
		var filePath = store.GetPath();
		Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
		File.WriteAllText(filePath, $$"""
		{
		  "schemaVersion": 1,
		  "recentFolders": [
		    { "path": "{{applicationStateDirectory.Replace("\\", "\\\\")}}", "openedUtc": "2026-04-02T13:11:48.4602914+00:00" },
		    { "path": "{{validFolder.Replace("\\", "\\\\")}}", "openedUtc": "2026-04-01T13:11:48.4602914+00:00" }
		  ],
		  "recentRepositories": []
		}
		""");

		var loaded = store.Load();

		Assert.Single(loaded.RecentFolders);
		Assert.Equal(PathUtility.Normalize(validFolder), loaded.RecentFolders[0].Path);
		Assert.DoesNotContain(applicationStateDirectory, File.ReadAllText(filePath), StringComparison.Ordinal);
	}

	[Fact]
	public void Load_InvalidPrimaryFile_RecoversFromBackupAndRestoresPrimary()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var db = store.Load();
		db = store.AddFolder(db, Path.Combine(temp.Path, "FolderA"));
		db = store.AddRepository(db, "https://github.com/example/repo");

		var filePath = store.GetPath();
		File.WriteAllText(filePath, "{ invalid");

		var loaded = store.Load();
		var persisted = JsonSerializer.Deserialize<RecentProjectsDb>(File.ReadAllText(filePath), new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			PropertyNameCaseInsensitive = true
		});

		Assert.NotNull(persisted);
		Assert.Single(loaded.RecentFolders);
		Assert.Single(loaded.RecentRepositories);
		Assert.Single(persisted!.RecentFolders);
		Assert.Single(persisted.RecentRepositories);
	}

	[Fact]
	public void Load_NullCollections_RewritesEmptyListsAndCurrentSchema()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var filePath = store.GetPath();
		Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
		File.WriteAllText(filePath, """
		{
		  "schemaVersion": 0,
		  "recentFolders": null,
		  "recentFolderRemovals": null,
		  "recentRepositories": null
		}
		""");

		var loaded = store.Load();
		var persisted = JsonSerializer.Deserialize<RecentProjectsDb>(File.ReadAllText(filePath), new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			PropertyNameCaseInsensitive = true
		});

		Assert.NotNull(persisted);
		Assert.Equal(3, loaded.SchemaVersion);
		Assert.Empty(loaded.RecentFolders);
		Assert.Empty(loaded.RecentRepositories);
		Assert.Equal(3, persisted!.SchemaVersion);
		Assert.Empty(persisted.RecentFolders);
		Assert.Empty(persisted.RecentFolderRemovals);
		Assert.Empty(persisted.RecentRepositories);
		Assert.Empty(persisted.RecentRepositoryRemovals);
	}

	[Fact]
	public void RemoveFolder_RemovesOnlyRequestedFolderAndPersistsTombstone()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var removedPath = temp.CreateFolder("Removed");
		var retainedPath = temp.CreateFolder("Retained");
		var state = store.AddFolder(store.Load(), removedPath);
		state = store.AddFolder(state, retainedPath);
		state = store.AddRepository(state, "https://github.com/example/repo");

		state = store.RemoveFolder(state, removedPath);
		var reloaded = store.Load();

		Assert.DoesNotContain(state.RecentFolders, entry => PathComparer.Default.Equals(entry.Path, removedPath));
		Assert.Single(reloaded.RecentFolders);
		Assert.Equal(PathUtility.Normalize(retainedPath), reloaded.RecentFolders[0].Path);
		Assert.Single(reloaded.RecentRepositories);
		var removal = Assert.Single(reloaded.RecentFolderRemovals);
		Assert.Equal(PathUtility.Normalize(removedPath), removal.Path);
	}

	[Fact]
	public void TryPersist_StaleSnapshot_DoesNotResurrectExplicitlyRemovedFolder()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var folderPath = temp.CreateFolder("Workspace");
		var current = store.AddFolder(store.Load(), folderPath);
		var stale = new RecentProjectsDb
		{
			SchemaVersion = current.SchemaVersion,
			RecentFolders = current.RecentFolders.Select(static entry => entry with { }).ToList()
		};

		store.RemoveFolder(current, folderPath);
		Assert.True(store.TryPersist(stale));

		var reloaded = store.Load();
		Assert.Empty(reloaded.RecentFolders);
		Assert.Single(reloaded.RecentFolderRemovals);
	}

	[Fact]
	public void AddFolder_AfterExplicitRemoval_RestoresItAsNewerHistory()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var folderPath = temp.CreateFolder("Workspace");
		var state = store.AddFolder(store.Load(), folderPath);
		state = store.RemoveFolder(state, folderPath);

		state = store.AddFolder(state, folderPath);
		var reloaded = store.Load();

		Assert.Single(state.RecentFolders);
		Assert.Single(reloaded.RecentFolders);
		Assert.Equal(PathUtility.Normalize(folderPath), reloaded.RecentFolders[0].Path);
	}

	[Fact]
	public void RemoveRepository_RemovesEquivalentUrlAndPersistsTombstone()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var state = store.AddRepository(
			store.Load(),
			"https://github.com/example/repository.git");
		state = store.AddRepository(
			state,
			"https://github.com/example/retained");

		state = store.RemoveRepository(
			state,
			"git@github.com:example/repository.git");
		var reloaded = store.Load();

		Assert.DoesNotContain(
			state.RecentRepositories,
			entry => RepositoryUrlUtility.AreEquivalent(
				entry.Url,
				"https://github.com/example/repository"));
		Assert.Single(reloaded.RecentRepositories);
		Assert.Equal(
			"https://github.com/example/retained",
			reloaded.RecentRepositories[0].Url);
		var removal = Assert.Single(reloaded.RecentRepositoryRemovals);
		Assert.True(RepositoryUrlUtility.AreEquivalent(
			removal.Url,
			"https://github.com/example/repository"));
	}

	[Fact]
	public void TryPersist_StaleSnapshot_DoesNotResurrectExplicitlyRemovedRepository()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var repositoryUrl = "https://github.com/example/repository";
		var current = store.AddRepository(store.Load(), repositoryUrl);
		var stale = new RecentProjectsDb
		{
			SchemaVersion = current.SchemaVersion,
			RecentRepositories = current.RecentRepositories
				.Select(static entry => entry with { })
				.ToList()
		};

		store.RemoveRepository(current, repositoryUrl);
		Assert.True(store.TryPersist(stale));

		var reloaded = store.Load();
		Assert.Empty(reloaded.RecentRepositories);
		Assert.Single(reloaded.RecentRepositoryRemovals);
	}

	[Fact]
	public void AddRepository_AfterExplicitRemoval_RestoresItAsNewerHistory()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var repositoryUrl = "https://github.com/example/repository";
		var state = store.AddRepository(store.Load(), repositoryUrl);
		state = store.RemoveRepository(state, repositoryUrl);

		state = store.AddRepository(
			state,
			"git@github.com:example/repository.git");
		var reloaded = store.Load();

		Assert.Single(state.RecentRepositories);
		Assert.Single(reloaded.RecentRepositories);
		Assert.Equal(
			"git@github.com:example/repository.git",
			reloaded.RecentRepositories[0].Url);
	}

	[Fact]
	public void ExtremePersistedTimestamps_DoNotBreakRemovalOrReopening()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var folderPath = temp.CreateFolder("Workspace");
		const string repositoryUrl = "https://github.com/example/repository";
		var state = new RecentProjectsDb
		{
			RecentFolders =
			[
				new RecentFolderEntry
				{
					Path = folderPath,
					OpenedUtc = DateTimeOffset.MaxValue
				}
			],
			RecentRepositories =
			[
				new RecentRepositoryEntry
				{
					Url = repositoryUrl,
					OpenedUtc = DateTimeOffset.MaxValue
				}
			]
		};

		state = store.RemoveFolder(state, folderPath);
		state = store.RemoveRepository(state, repositoryUrl);
		state = store.AddFolder(state, folderPath);
		state = store.AddRepository(state, repositoryUrl);
		var reloaded = store.Load();

		Assert.Equal(PathUtility.Normalize(folderPath), Assert.Single(reloaded.RecentFolders).Path);
		Assert.Equal(repositoryUrl, Assert.Single(reloaded.RecentRepositories).Url);
	}

	[Fact]
	public void TryPersist_ExcessRemovalHistory_IsBoundedToNewestSixtyFourEntries()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var snapshot = new RecentProjectsDb
		{
			RecentFolderRemovals = Enumerable.Range(0, 70)
				.Select(index => new RecentFolderRemovalEntry
				{
					Path = Path.Combine(temp.Path, $"Removed{index}"),
					RemovedUtc = DateTimeOffset.UtcNow.AddMinutes(index)
				})
				.ToList()
		};

		Assert.True(store.TryPersist(snapshot));
		var reloaded = store.Load();

		Assert.Equal(64, reloaded.RecentFolderRemovals.Count);
		Assert.Contains(reloaded.RecentFolderRemovals, entry => entry.Path.EndsWith("Removed69", StringComparison.Ordinal));
		Assert.DoesNotContain(reloaded.RecentFolderRemovals, entry => entry.Path.EndsWith("Removed0", StringComparison.Ordinal));
	}

	[Fact]
	public void LoadForStartupWithStatus_CorruptPrimaryRecoversFromBackup()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var folder = temp.CreateFolder("Recovered");
		store.AddFolder(null, folder);
		File.WriteAllText(store.GetPath(), "{ invalid");

		var result = store.LoadForStartupWithStatus(TimeSpan.Zero);

		Assert.Equal(RecentProjectsLoadStatus.Success, result.Status);
		Assert.Equal(PathUtility.Normalize(folder), Assert.Single(result.Database.RecentFolders).Path);
	}

	[Fact]
	public void LoadForStartupWithStatus_CorruptPrimaryAndBackupReportsInvalidStorage()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		Assert.True(store.EnsureStorageExists());
		File.WriteAllText(store.GetPath(), "{ invalid-primary");
		File.WriteAllText(store.GetPath() + ".bak", "{ invalid-backup");

		var result = store.LoadForStartupWithStatus(TimeSpan.Zero);

		Assert.Equal(RecentProjectsLoadStatus.InvalidStorage, result.Status);
		Assert.Empty(result.Database.RecentFolders);
	}

	[Fact]
	public void LoadForStartupWithStatus_HeldStoreLockReportsTemporaryUnavailability()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		Assert.True(store.EnsureStorageExists());
		using var heldLock = new FileStream(
			store.GetPath() + ".lock",
			FileMode.OpenOrCreate,
			FileAccess.ReadWrite,
			FileShare.None);

		var result = store.LoadForStartupWithStatus(TimeSpan.Zero);

		Assert.Equal(RecentProjectsLoadStatus.TemporarilyUnavailable, result.Status);
		Assert.Empty(result.Database.RecentFolders);
	}
}
