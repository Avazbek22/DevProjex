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
    public void Store_PersistsOnlyTheThirtyTwoMostRecentFoldersAcrossInstances()
    {
        using var temp = new TemporaryDirectory();
        var firstStore = new RecentProjectsStore(() => temp.Path);
        var db = firstStore.Load();

        var folderPaths = Enumerable.Range(0, 34)
            .Select(index => temp.CreateDirectory($"Folders/Folder{index}"))
            .ToArray();

        foreach (var folderPath in folderPaths)
            db = firstStore.AddFolder(db, folderPath);

        var secondStore = new RecentProjectsStore(() => temp.Path);
        var reloaded = secondStore.Load();

        Assert.Equal(32, reloaded.RecentFolders.Count);
        Assert.Equal(PathUtility.Normalize(folderPaths[33]), reloaded.RecentFolders[0].Path);
        Assert.Equal(PathUtility.Normalize(folderPaths[32]), reloaded.RecentFolders[1].Path);
        Assert.Equal(PathUtility.Normalize(folderPaths[2]), reloaded.RecentFolders[31].Path);
        Assert.DoesNotContain(reloaded.RecentFolders, entry => entry.Path == PathUtility.Normalize(folderPaths[0]));
        Assert.DoesNotContain(reloaded.RecentFolders, entry => entry.Path == PathUtility.Normalize(folderPaths[1]));
    }

    [Fact]
    public void Store_PersistsOnlyTheSixteenMostRecentRepositoriesAcrossInstances()
    {
        using var temp = new TemporaryDirectory();
        var firstStore = new RecentProjectsStore(() => temp.Path);
        var db = firstStore.Load();

        var repositoryUrls = Enumerable.Range(0, 18)
            .Select(index => $"https://example.com/user/repo{index}")
            .ToArray();

        foreach (var repositoryUrl in repositoryUrls)
            db = firstStore.AddRepository(db, repositoryUrl);

        var secondStore = new RecentProjectsStore(() => temp.Path);
        var reloaded = secondStore.Load();

        Assert.Equal(16, reloaded.RecentRepositories.Count);
        Assert.Equal(repositoryUrls[17], reloaded.RecentRepositories[0].Url);
        Assert.Equal(repositoryUrls[16], reloaded.RecentRepositories[1].Url);
        Assert.Equal(repositoryUrls[2], reloaded.RecentRepositories[15].Url);
        Assert.DoesNotContain(reloaded.RecentRepositories, entry => entry.Url == repositoryUrls[0]);
        Assert.DoesNotContain(reloaded.RecentRepositories, entry => entry.Url == repositoryUrls[1]);
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
		    { "path": "__VALID_FOLDER_TRAILING__", "openedUtc": "2026-03-29T10:00:00Z" },
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
				.Replace(
					"__VALID_FOLDER_TRAILING__",
					(validFolder + Path.DirectorySeparatorChar).Replace("\\", "\\\\"))
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
		Assert.Equal(3, loaded.SchemaVersion);
		Assert.Single(loaded.RecentFolders);
		Assert.Single(loaded.RecentRepositories);
		Assert.Equal(normalizedValidFolder, loaded.RecentFolders[0].Path);
		Assert.Equal("https://github.com/user/repo.git", loaded.RecentRepositories[0].Url);

		Assert.Equal(3, persisted!.SchemaVersion);
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
		Assert.Equal(3, loaded.SchemaVersion);
		Assert.Empty(loaded.RecentFolders);
		Assert.Empty(loaded.RecentRepositories);
		Assert.Equal(3, persisted!.SchemaVersion);
		Assert.Empty(persisted.RecentFolders);
		Assert.Empty(persisted.RecentRepositories);
	}

	[Fact]
	public void Store_RecoversFromCorruptedPrimaryFile_UsingPersistedBackupAcrossInstances()
	{
		using var temp = new TemporaryDirectory();
		var firstStore = new RecentProjectsStore(() => temp.Path);
		var folderPath = temp.CreateDirectory("Workspace/Recovered");
		var repositoryUrl = "https://github.com/example/recovered-repo";

		var db = firstStore.Load();
		db = firstStore.AddFolder(db, folderPath);
		db = firstStore.AddRepository(db, repositoryUrl);

		File.WriteAllText(firstStore.GetPath(), "{ invalid");

		var secondStore = new RecentProjectsStore(() => temp.Path);
		var reloaded = secondStore.Load();

		Assert.Single(reloaded.RecentFolders);
		Assert.Single(reloaded.RecentRepositories);
		Assert.Equal(PathUtility.Normalize(folderPath), reloaded.RecentFolders[0].Path);
		Assert.Equal(repositoryUrl, reloaded.RecentRepositories[0].Url);
	}

	[Fact]
	public void Store_DoesNotPersistApplicationStateDirectory_AcrossInstances()
	{
		using var temp = new TemporaryDirectory();
		var firstStore = new RecentProjectsStore(() => temp.Path);
		var validFolder = temp.CreateDirectory("Workspace/Valid");
		var applicationStateDirectory = Path.Combine(temp.Path, "DevProjex");
		Directory.CreateDirectory(applicationStateDirectory);

		var db = firstStore.Load();
		db = firstStore.AddFolder(db, applicationStateDirectory);
		db = firstStore.AddFolder(db, validFolder);

		var secondStore = new RecentProjectsStore(() => temp.Path);
		var reloaded = secondStore.Load();

		Assert.Single(reloaded.RecentFolders);
		Assert.Equal(PathUtility.Normalize(validFolder), reloaded.RecentFolders[0].Path);
		Assert.DoesNotContain(applicationStateDirectory, File.ReadAllText(firstStore.GetPath()), StringComparison.Ordinal);
	}
}
