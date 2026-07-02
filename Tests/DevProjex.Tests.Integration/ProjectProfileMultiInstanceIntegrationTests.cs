namespace DevProjex.Tests.Integration;

public sealed class ProjectProfileMultiInstanceIntegrationTests
{
    [Fact]
    public async Task IndependentInstances_ConcurrentProfileSavesForDifferentProjects_PreserveBothSnapshots()
    {
        using var temp = new TemporaryDirectory();
        var storeA = new ProjectProfileStore(() => temp.Path);
        var storeB = new ProjectProfileStore(() => temp.Path);
        var firstProjectPath = temp.CreateDirectory("Workspace/RepoA");
        var secondProjectPath = temp.CreateDirectory("Workspace/RepoB");
        var firstProfile = CreateProfile(["src"], [".cs"], [IgnoreOptionId.DotFiles]);
        var secondProfile = CreateProfile(["tests"], [".json"], [IgnoreOptionId.UseGitIgnore]);
        using var startGate = new ManualResetEventSlim(false);

        var firstSaveTask = Task.Run(() =>
        {
            startGate.Wait();
            storeA.SaveProfile(firstProjectPath, firstProfile);
        }, cancellationToken: TestContext.Current.CancellationToken);

        var secondSaveTask = Task.Run(() =>
        {
            startGate.Wait();
            storeB.SaveProfile(secondProjectPath, secondProfile);
        }, cancellationToken: TestContext.Current.CancellationToken);

        startGate.Set();
        await Task.WhenAll(firstSaveTask, secondSaveTask);

        var reloaded = new ProjectProfileStore(() => temp.Path);

        Assert.True(reloaded.TryLoadProfile(firstProjectPath, out var loadedFirst));
        Assert.True(reloaded.TryLoadProfile(secondProjectPath, out var loadedSecond));
        Assert.Contains("src", loadedFirst.SelectedRootFolders);
        Assert.Contains(".cs", loadedFirst.SelectedExtensions);
        Assert.Contains(IgnoreOptionId.DotFiles, loadedFirst.SelectedIgnoreOptions);
        Assert.Contains("tests", loadedSecond.SelectedRootFolders);
        Assert.Contains(".json", loadedSecond.SelectedExtensions);
        Assert.Contains(IgnoreOptionId.UseGitIgnore, loadedSecond.SelectedIgnoreOptions);
    }

    [Fact]
    public void TrySaveProfile_OlderRetryTimestamp_DoesNotOverwriteNewerPersistedSnapshot()
    {
        using var temp = new TemporaryDirectory();
        var initialStore = new ProjectProfileStore(() => temp.Path);
        var retryStore = new ProjectProfileStore(() => temp.Path);
        var projectPath = temp.CreateDirectory("Workspace/RepoA");
        var olderProfile = CreateProfile(["src"], [".cs"], [IgnoreOptionId.DotFiles]);
        var newerProfile = CreateProfile(["docs"], [".md"], [IgnoreOptionId.UseGitIgnore]);
        var olderTimestamp = new DateTimeOffset(2026, 4, 2, 10, 0, 0, TimeSpan.Zero);
        var newerTimestamp = olderTimestamp.AddMinutes(5);

        Assert.True(initialStore.TrySaveProfile(projectPath, olderProfile, olderTimestamp));
        Assert.True(initialStore.TrySaveProfile(projectPath, newerProfile, newerTimestamp));
        Assert.True(retryStore.TrySaveProfile(projectPath, olderProfile, olderTimestamp));

        var reloaded = new ProjectProfileStore(() => temp.Path);
        Assert.True(reloaded.TryLoadProfile(projectPath, out var loaded));
        Assert.Contains("docs", loaded.SelectedRootFolders);
        Assert.DoesNotContain("src", loaded.SelectedRootFolders);
        Assert.Contains(".md", loaded.SelectedExtensions);
        Assert.DoesNotContain(".cs", loaded.SelectedExtensions);
        Assert.Contains(IgnoreOptionId.UseGitIgnore, loaded.SelectedIgnoreOptions);
        Assert.DoesNotContain(IgnoreOptionId.DotFiles, loaded.SelectedIgnoreOptions);
    }

    private static ProjectSelectionProfile CreateProfile(
        IEnumerable<string> selectedRootFolders,
        IEnumerable<string> selectedExtensions,
        IEnumerable<IgnoreOptionId> selectedIgnoreOptions)
    {
        return new ProjectSelectionProfile(
            SelectedRootFolders: [.. selectedRootFolders],
            SelectedExtensions: [.. selectedExtensions],
            SelectedIgnoreOptions: [.. selectedIgnoreOptions]);
    }
}
