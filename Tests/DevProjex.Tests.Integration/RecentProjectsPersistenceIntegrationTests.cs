using DevProjex.Infrastructure.RecentProjects;

namespace DevProjex.Tests.Integration;

public sealed class RecentProjectsPersistenceIntegrationTests
{
	[Fact]
	public void Store_PersistsRecentFoldersAndRepositoriesAcrossInstances()
	{
		using var temp = new TemporaryDirectory();
		var firstStore = new RecentProjectsStore(() => temp.Path);
		var folderPath = temp.CreateDirectory("Workspace/Feature");
		var repositoryUrl = "https://github.com/example/project.git";

		var db = firstStore.Load();
		db = firstStore.AddFolder(db, folderPath);
		db = firstStore.AddRepository(db, repositoryUrl);

		var secondStore = new RecentProjectsStore(() => temp.Path);
		var reloaded = secondStore.Load();

		Assert.Single(reloaded.RecentFolders);
		Assert.Single(reloaded.RecentRepositories);
		Assert.Equal(Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), reloaded.RecentFolders[0].Path);
		Assert.Equal(repositoryUrl, reloaded.RecentRepositories[0].Url);
	}

	[Fact]
	public void Store_PersistsLatestRepositoryRepresentationAfterComparisonDeduplication()
	{
		using var temp = new TemporaryDirectory();
		var firstStore = new RecentProjectsStore(() => temp.Path);
		var db = firstStore.Load();

		db = firstStore.AddRepository(db, "https://github.com/example/project.git");
		db = firstStore.AddRepository(db, "https://github.com/example/project");

		var secondStore = new RecentProjectsStore(() => temp.Path);
		var reloaded = secondStore.Load();

		Assert.Single(reloaded.RecentRepositories);
		Assert.Equal("https://github.com/example/project", reloaded.RecentRepositories[0].Url);
	}

	[Fact]
	public void Load_NormalizesLegacyPayload_AndRewritesFile()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => temp.Path);
		var validFolder = temp.CreateDirectory("Workspace/Feature");
		var filePath = store.GetPath();
		Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

		var legacyPayload = """
		{
		  "schemaVersion": 0,
		  "recentFolders": [
		    { "path": "__VALID_FOLDER__", "openedUtc": "2026-03-30T10:00:00Z" },
		    { "path": "__VALID_FOLDER__\\", "openedUtc": "2026-03-29T10:00:00Z" },
		    { "path": "__REPO_CACHE__", "openedUtc": "2026-03-28T10:00:00Z" }
		  ],
		  "recentRepositories": [
		    { "url": "https://github.com/user/repo.git", "openedUtc": "2026-03-30T10:00:00Z" },
		    { "url": "https://github.com/user/repo?ref=main", "openedUtc": "2026-03-29T10:00:00Z" }
		  ]
		}
		""";

		var repoCachePath = Path.Combine(Path.GetTempPath(), "DevProjex", "RepoCache", "repo_legacy");
		File.WriteAllText(
			filePath,
			legacyPayload
				.Replace("__VALID_FOLDER__", validFolder.Replace("\\", "\\\\"))
				.Replace("__REPO_CACHE__", repoCachePath.Replace("\\", "\\\\")));

		var loaded = store.Load();
		var persisted = JsonSerializer.Deserialize<RecentProjectsDb>(File.ReadAllText(filePath), new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			PropertyNameCaseInsensitive = true
		});
		var normalizedValidFolder = PathUtility.Normalize(validFolder);

		Assert.NotNull(persisted);
		Assert.Equal(1, loaded.SchemaVersion);
		Assert.Single(loaded.RecentFolders);
		Assert.Single(loaded.RecentRepositories);
		Assert.Equal(normalizedValidFolder, loaded.RecentFolders[0].Path);
		Assert.Equal("https://github.com/user/repo.git", loaded.RecentRepositories[0].Url);

		Assert.Equal(1, persisted!.SchemaVersion);
		Assert.Single(persisted.RecentFolders);
		Assert.Single(persisted.RecentRepositories);
		Assert.Equal(normalizedValidFolder, persisted.RecentFolders[0].Path);
		Assert.Equal("https://github.com/user/repo.git", persisted.RecentRepositories[0].Url);
	}

	[Fact]
	public void Load_NullCollections_RewritesEmptyLists()
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
