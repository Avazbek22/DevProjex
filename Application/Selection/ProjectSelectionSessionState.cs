namespace DevProjex.Application.Selection;

public sealed class ProjectSelectionSessionState
{
    private long _revision;

    public SelectionOptionStateCache RootFolders { get; } = new(PathComparer.Default);

    public SelectionOptionStateCache Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IgnoreSelectionState IgnoreOptions { get; } = new();

    public string? LastLoadedPath { get; set; }

    public string? PreparedPath { get; private set; }

    public PreparedSelectionMode PreparedMode { get; private set; }

    public bool IgnoreOptionStateCacheIsComplete { get; set; }

    // Explicit Desktop/CLI collections are closed sets. These flags prevent later topology
    // discovery from treating a newly visible root or extension as implicitly selected.
    public bool RootSelectionIsExplicit { get; set; }

    public bool ExtensionSelectionIsExplicit { get; set; }

    // The revision is the consistency boundary between the three settings sections and
    // consumers such as tree construction. Any effective selection mutation advances it,
    // allowing long-running consumers to reject results built from an obsolete snapshot.
    public long Revision => Interlocked.Read(ref _revision);

    public long AdvanceRevision() => Interlocked.Increment(ref _revision);

    public void ApplyProfile(string projectPath, ProjectSelectionProfile profile)
    {
		// Root folders, extensions, and exclusions form one logical parameters snapshot.
		// Restoring all maps together prevents a refresh in one island from interpreting
		// stale selected-only data from another island as a new user decision.
        PreparedPath = projectPath;
        PreparedMode = PreparedSelectionMode.Profile;
        RootSelectionIsExplicit = false;
        ExtensionSelectionIsExplicit = false;

        RootFolders.RestoreProfile(profile.SelectedRootFolders, profile.RootFolderStates);
        Extensions.RestoreProfile(profile.SelectedExtensions, profile.ExtensionStates);

        if (profile.IgnoreOptionStates is not null)
        {
			// Even an empty map is meaningful: it is a complete modern snapshot whose
			// future, newly available checkboxes receive their descriptor defaults.
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
        RootSelectionIsExplicit = false;
        ExtensionSelectionIsExplicit = false;

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
        RootSelectionIsExplicit = false;
        ExtensionSelectionIsExplicit = false;
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

    public ProjectSelectionSessionSnapshot CaptureSnapshot() =>
        new(
            LastLoadedPath,
            PreparedPath,
            PreparedMode,
            IgnoreOptionStateCacheIsComplete,
            RootSelectionIsExplicit,
            ExtensionSelectionIsExplicit,
            RootFolders.CaptureSnapshot(),
            Extensions.CaptureSnapshot(),
            IgnoreOptions.CaptureSnapshot());

    public void RestoreSnapshot(ProjectSelectionSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        LastLoadedPath = snapshot.LastLoadedPath;
        PreparedPath = snapshot.PreparedPath;
        PreparedMode = snapshot.PreparedMode;
        IgnoreOptionStateCacheIsComplete = snapshot.IgnoreOptionStateCacheIsComplete;
        RootSelectionIsExplicit = snapshot.RootSelectionIsExplicit;
        ExtensionSelectionIsExplicit = snapshot.ExtensionSelectionIsExplicit;
        RootFolders.RestoreSnapshot(snapshot.RootFolders);
        Extensions.RestoreSnapshot(snapshot.Extensions);
        IgnoreOptions.RestoreSnapshot(snapshot.IgnoreOptions);
    }
}

public sealed record ProjectSelectionSessionSnapshot(
    string? LastLoadedPath,
    string? PreparedPath,
    PreparedSelectionMode PreparedMode,
    bool IgnoreOptionStateCacheIsComplete,
    bool RootSelectionIsExplicit,
    bool ExtensionSelectionIsExplicit,
    SelectionOptionStateCacheSnapshot RootFolders,
    SelectionOptionStateCacheSnapshot Extensions,
    IgnoreSelectionStateSnapshot IgnoreOptions);
