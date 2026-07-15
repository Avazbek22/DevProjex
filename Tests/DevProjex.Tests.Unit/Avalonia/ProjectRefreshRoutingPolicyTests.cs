namespace DevProjex.Tests.Unit.Avalonia;

public sealed class ProjectRefreshRoutingPolicyTests
{
    [Theory]
    [InlineData(false, ProjectSourceType.LocalFolder, (int)ProjectRefreshRoute.None)]
    [InlineData(false, ProjectSourceType.GitClone, (int)ProjectRefreshRoute.None)]
    [InlineData(false, ProjectSourceType.ZipDownload, (int)ProjectRefreshRoute.None)]
    [InlineData(true, ProjectSourceType.LocalFolder, (int)ProjectRefreshRoute.ReloadFiles)]
    [InlineData(true, ProjectSourceType.GitClone, (int)ProjectRefreshRoute.PullGitUpdates)]
    [InlineData(true, ProjectSourceType.ZipDownload, (int)ProjectRefreshRoute.ReloadFiles)]
    public void Resolve_UsesProjectSourceAndLoadedState(
        bool isProjectLoaded,
        ProjectSourceType sourceType,
        int expectedRoute)
    {
        var route = ProjectRefreshRoutingPolicy.Resolve(isProjectLoaded, sourceType);

        Assert.Equal(expectedRoute, (int)route);
    }

    [Theory]
    [InlineData(false, ProjectSourceType.LocalFolder, 0, 0)]
    [InlineData(false, ProjectSourceType.GitClone, 0, 0)]
    [InlineData(true, ProjectSourceType.LocalFolder, 1, 0)]
    [InlineData(true, ProjectSourceType.ZipDownload, 1, 0)]
    [InlineData(true, ProjectSourceType.GitClone, 0, 1)]
    public async Task ExecuteAsync_InvokesOnlyTheResolvedSourceOperation(
        bool isProjectLoaded,
        ProjectSourceType sourceType,
        int expectedReloadCalls,
        int expectedPullCalls)
    {
        var reloadCalls = 0;
        var pullCalls = 0;

        await ProjectRefreshRoutingPolicy.ExecuteAsync(
            isProjectLoaded,
            sourceType,
            () =>
            {
                reloadCalls++;
                return Task.CompletedTask;
            },
            () =>
            {
                pullCalls++;
                return Task.CompletedTask;
            });

        Assert.Equal(expectedReloadCalls, reloadCalls);
        Assert.Equal(expectedPullCalls, pullCalls);
    }
}
