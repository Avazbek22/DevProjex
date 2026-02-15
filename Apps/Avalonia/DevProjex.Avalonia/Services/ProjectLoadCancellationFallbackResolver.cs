namespace DevProjex.Avalonia.Services;

public enum ProjectLoadCancellationFallback
{
    ResetToInitialState = 0,
    RestorePreviousProject = 1
}

public static class ProjectLoadCancellationFallbackResolver
{
    public static ProjectLoadCancellationFallback Resolve(bool hadLoadedProjectBefore)
    {
        return hadLoadedProjectBefore
            ? ProjectLoadCancellationFallback.RestorePreviousProject
            : ProjectLoadCancellationFallback.ResetToInitialState;
    }
}
