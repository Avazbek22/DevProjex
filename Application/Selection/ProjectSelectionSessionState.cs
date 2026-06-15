namespace DevProjex.Application.Selection;

public sealed class ProjectSelectionSessionState
{
    public SelectionOptionStateCache RootFolders { get; } = new(PathComparer.Default);

    public SelectionOptionStateCache Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IgnoreSelectionState IgnoreOptions { get; } = new();

    public string? LastLoadedPath { get; set; }

    public string? PreparedPath { get; private set; }

    public PreparedSelectionMode PreparedMode { get; private set; }

    public bool IgnoreOptionStateCacheIsComplete { get; set; }

    public void ApplyProfile(string projectPath, ProjectSelectionProfile profile)
    {
        PreparedPath = projectPath;
        PreparedMode = PreparedSelectionMode.Profile;

        RootFolders.RestoreProfile(profile.SelectedRootFolders, profile.RootFolderStates);
        Extensions.RestoreProfile(profile.SelectedExtensions, profile.ExtensionStates);

        if (profile.IgnoreOptionStates is not null)
        {
            IgnoreOptions.ReplaceStateCache(profile.IgnoreOptionStates);
            IgnoreOptionStateCacheIsComplete = true;
            return;
        }

        IgnoreOptions.RestoreProfileSelection(profile.SelectedIgnoreOptions);
        IgnoreOptionStateCacheIsComplete = false;
    }

    public void ResetToDefaultsForProject(string projectPath)
    {
        PreparedPath = projectPath;
        PreparedMode = PreparedSelectionMode.Defaults;

        RootFolders.RestoreDefaults(trimExcess: true);
        Extensions.RestoreDefaults(trimExcess: true);
        IgnoreOptions.Reset(trimExcess: true);
        IgnoreOptionStateCacheIsComplete = false;
    }

    public void ClearProjectCaches(bool trimExcess)
    {
        RootFolders.RestoreDefaults(trimExcess);
        Extensions.RestoreDefaults(trimExcess);
        IgnoreOptions.Reset(trimExcess);
        IgnoreOptionStateCacheIsComplete = false;
    }

    public bool ShouldClearCachesForCurrentPath(string currentPath) =>
        SelectionRefreshPolicy.ShouldClearCachesForCurrentPath(
            LastLoadedPath,
            PreparedPath,
            currentPath);

    public bool HasPreparedSelectionForPath(string path)
    {
        return PreparedPath is not null &&
               PathComparer.Default.Equals(PreparedPath, path);
    }

    public bool ShouldSkipRefreshForPreparedPath(string currentPath) =>
        SelectionRefreshPolicy.ShouldSkipRefreshForPreparedPath(PreparedPath, currentPath);

    public bool IsPreparedProfile => PreparedMode == PreparedSelectionMode.Profile;

    public void ConsumePreparedSelectionForPath(string path)
    {
        if (!HasPreparedSelectionForPath(path))
            return;

        PreparedPath = null;
        PreparedMode = PreparedSelectionMode.None;
    }

    public void ClearPreparedSelection()
    {
        PreparedPath = null;
        PreparedMode = PreparedSelectionMode.None;
    }
}
