namespace DevProjex.Application.Selection;

public sealed class ProjectSelectionSessionState
{
    private long _revision;

    public SelectionOptionStateCache Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IgnoreSelectionState IgnoreOptions { get; } = new();

    public string? LastLoadedPath { get; set; }

    public string? PreparedPath { get; private set; }

    public PreparedSelectionMode PreparedMode { get; private set; }

    public bool IgnoreOptionStateCacheIsComplete { get; set; }

    // Explicit Desktop/CLI extension collections are closed sets. This prevents later
    // discovery from treating a newly visible extension as implicitly selected.
    public bool ExtensionSelectionIsExplicit { get; set; }

    // The revision is the consistency boundary between tree-shaping settings and consumers such
    // as tree construction. Content-only transformations use their own cancellation callback and
    // must not invalidate the filesystem selection snapshot.
    public long Revision => Interlocked.Read(ref _revision);

    public long AdvanceRevision() => Interlocked.Increment(ref _revision);

    public void ApplyProfile(string projectPath, ProjectSelectionProfile profile)
    {
        // Extensions and exclusions form one logical parameters snapshot. Content-processing
        // options share the persisted state map but do not alter the filesystem selection.
        PreparedPath = projectPath;
        PreparedMode = PreparedSelectionMode.Profile;
        ExtensionSelectionIsExplicit = false;

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
        ExtensionSelectionIsExplicit = false;

        Extensions.RestoreDefaults(trimExcess: true);
        IgnoreOptions.Reset(trimExcess: true);
        IgnoreOptionStateCacheIsComplete = false;
    }

    public void ClearProjectCaches(bool trimExcess)
    {
        Extensions.RestoreDefaults(trimExcess);
        IgnoreOptions.Reset(trimExcess);
        IgnoreOptionStateCacheIsComplete = false;
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
            ExtensionSelectionIsExplicit,
            Extensions.CaptureSnapshot(),
            IgnoreOptions.CaptureSnapshot());

    public void RestoreSnapshot(ProjectSelectionSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        LastLoadedPath = snapshot.LastLoadedPath;
        PreparedPath = snapshot.PreparedPath;
        PreparedMode = snapshot.PreparedMode;
        IgnoreOptionStateCacheIsComplete = snapshot.IgnoreOptionStateCacheIsComplete;
        ExtensionSelectionIsExplicit = snapshot.ExtensionSelectionIsExplicit;
        Extensions.RestoreSnapshot(snapshot.Extensions);
        IgnoreOptions.RestoreSnapshot(snapshot.IgnoreOptions);
    }
}

public sealed record ProjectSelectionSessionSnapshot(
    string? LastLoadedPath,
    string? PreparedPath,
    PreparedSelectionMode PreparedMode,
    bool IgnoreOptionStateCacheIsComplete,
    bool ExtensionSelectionIsExplicit,
    SelectionOptionStateCacheSnapshot Extensions,
    IgnoreSelectionStateSnapshot IgnoreOptions);
