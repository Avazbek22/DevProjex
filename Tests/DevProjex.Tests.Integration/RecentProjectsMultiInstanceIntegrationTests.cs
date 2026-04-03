using DevProjex.Infrastructure.RecentProjects;

namespace DevProjex.Tests.Integration;

public sealed class RecentProjectsMultiInstanceIntegrationTests
{
    [Fact]
    public async Task IndependentInstances_ConcurrentRecentUpdates_AreMergedWithoutHistoryLoss()
    {
        using var temp = new Helpers.TemporaryDirectory();
        var storeA = new RecentProjectsStore(() => temp.Path);
        var storeB = new RecentProjectsStore(() => temp.Path);
        var snapshotA = storeA.Load();
        var snapshotB = storeB.Load();
        var folderPath = temp.CreateDirectory("Workspace/Feature");
        var repositoryUrl = "https://github.com/example/recent-project";
        using var startGate = new ManualResetEventSlim(false);

        var folderTask = Task.Run(() =>
        {
            startGate.Wait();
            storeA.AddFolder(snapshotA, folderPath);
        });

        var repositoryTask = Task.Run(() =>
        {
            startGate.Wait();
            storeB.AddRepository(snapshotB, repositoryUrl);
        });

        startGate.Set();
        await Task.WhenAll(folderTask, repositoryTask);

        var reloaded = new RecentProjectsStore(() => temp.Path).Load();

        Assert.Contains(reloaded.RecentFolders, entry => entry.Path == PathUtility.Normalize(folderPath));
        Assert.Contains(reloaded.RecentRepositories, entry => entry.Url == repositoryUrl);
    }

    [Fact]
    public void TryPersist_StaleDetachedSnapshot_MergesWithNewerPersistedFolders()
    {
        using var temp = new Helpers.TemporaryDirectory();
        var writerStore = new RecentProjectsStore(() => temp.Path);
        var retryStore = new RecentProjectsStore(() => temp.Path);
        var newerFolder = temp.CreateDirectory("Workspace/Newer");
        var delayedFolder = temp.CreateDirectory("Workspace/Delayed");

        var initialSnapshot = writerStore.Load();
        writerStore.AddFolder(initialSnapshot, newerFolder);

        var staleSnapshot = new RecentProjectsDb
        {
            SchemaVersion = 1,
            RecentFolders =
            [
                new RecentFolderEntry
                {
                    Path = delayedFolder,
                    OpenedUtc = DateTimeOffset.UtcNow
                }
            ],
            RecentRepositories = []
        };

        Assert.True(retryStore.TryPersist(staleSnapshot));

        var reloaded = new RecentProjectsStore(() => temp.Path).Load();

        Assert.Equal(2, reloaded.RecentFolders.Count);
        Assert.Contains(reloaded.RecentFolders, entry => entry.Path == PathUtility.Normalize(newerFolder));
        Assert.Contains(reloaded.RecentFolders, entry => entry.Path == PathUtility.Normalize(delayedFolder));
    }
}
