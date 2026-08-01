using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Coordinators;

public sealed class ProjectLoadCancellationCoordinator
{
    private ProjectLoadCancellationSnapshot? _activeSnapshot;

    public void Capture(ProjectLoadCancellationSnapshot snapshot)
    {
        _activeSnapshot = snapshot;
    }

    public void Clear()
    {
        _activeSnapshot = null;
    }

    public bool TryApply(Action resetToInitialState, Action<ProjectLoadCancellationSnapshot> restorePreviousState)
    {
        var snapshot = _activeSnapshot;
        if (snapshot is null)
            return false;

        _activeSnapshot = null;
        var fallback = ProjectLoadCancellationFallbackResolver.Resolve(snapshot.HadLoadedProjectBefore);
        if (fallback == ProjectLoadCancellationFallback.ResetToInitialState)
        {
            resetToInitialState();
            return true;
        }

        restorePreviousState(snapshot);
        return true;
    }
}
