using DevProjex.Infrastructure.RecentProjects;

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
	public void AddFolder_ClampsToTenItems()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var db = store.Load();

		for (var i = 0; i < 12; i++)
			db = store.AddFolder(db, Path.Combine(temp.Path, $"Folder{i}"));

		Assert.Equal(10, db.RecentFolders.Count);
		Assert.Contains(db.RecentFolders, entry => entry.Path.EndsWith("Folder11", StringComparison.Ordinal));
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
	public void AddRepository_ClampsToSevenItems()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var db = store.Load();

		for (var i = 0; i < 9; i++)
			db = store.AddRepository(db, $"https://example.com/user/repo{i}");

		Assert.Equal(7, db.RecentRepositories.Count);
		Assert.Contains(db.RecentRepositories, entry => entry.Url.EndsWith("repo8", StringComparison.Ordinal));
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
	public void AddFolder_DeduplicatesLegacyTrailingSeparatorVariants_AndKeepsLatestValue()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var db = store.Load();
		var folder = temp.CreateFolder("Workspace");

		db = store.AddFolder(db, folder);
		db = store.AddFolder(db, folder + '\\');

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

		Assert.Equal(1, loaded.SchemaVersion);
		Assert.Empty(loaded.RecentFolders);
		Assert.Empty(loaded.RecentRepositories);
		Assert.Equal(invalidJson, File.ReadAllText(filePath));
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
		Assert.Equal(1, loaded.SchemaVersion);
		Assert.Empty(loaded.RecentFolders);
		Assert.Empty(loaded.RecentRepositories);
		Assert.Equal(1, persisted!.SchemaVersion);
		Assert.Empty(persisted.RecentFolders);
		Assert.Empty(persisted.RecentRepositories);
	}
}
