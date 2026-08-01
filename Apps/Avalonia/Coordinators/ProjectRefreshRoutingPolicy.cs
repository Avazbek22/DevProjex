namespace DevProjex.Avalonia.Coordinators;

internal enum ProjectRefreshRoute
{
    None,
    ReloadFiles,
    PullGitUpdates
}

internal static class ProjectRefreshRoutingPolicy
{
    public static ProjectRefreshRoute Resolve(bool isProjectLoaded, ProjectSourceType sourceType)
    {
        if (!isProjectLoaded)
            return ProjectRefreshRoute.None;

        return sourceType == ProjectSourceType.GitClone
            ? ProjectRefreshRoute.PullGitUpdates
            : ProjectRefreshRoute.ReloadFiles;
    }

    public static Task ExecuteAsync(
        bool isProjectLoaded,
        ProjectSourceType sourceType,
        Func<Task> reloadFiles,
        Func<Task> pullGitUpdates)
    {
        ArgumentNullException.ThrowIfNull(reloadFiles);
        ArgumentNullException.ThrowIfNull(pullGitUpdates);

        return Resolve(isProjectLoaded, sourceType) switch
        {
            ProjectRefreshRoute.ReloadFiles => reloadFiles(),
            ProjectRefreshRoute.PullGitUpdates => pullGitUpdates(),
            _ => Task.CompletedTask
        };
    }
}
