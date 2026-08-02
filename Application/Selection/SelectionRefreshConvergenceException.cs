namespace DevProjex.Application.Selection;

public sealed class SelectionRefreshConvergenceException(
    SelectionRefreshConvergenceFailure failure,
    int completedPasses)
    : InvalidOperationException(CreateMessage(failure, completedPasses))
{
    public SelectionRefreshConvergenceFailure Failure { get; } = failure;

    public int CompletedPasses { get; } = completedPasses;

    private static string CreateMessage(
        SelectionRefreshConvergenceFailure failure,
        int completedPasses) =>
        failure switch
        {
            SelectionRefreshConvergenceFailure.CycleDetected =>
                $"The settings selection refresh entered a cycle after {completedPasses} passes.",
            SelectionRefreshConvergenceFailure.PassLimitExceeded =>
                $"The settings selection refresh did not converge after {completedPasses} passes.",
            _ => "The settings selection refresh did not converge."
        };
}

public enum SelectionRefreshConvergenceFailure
{
    CycleDetected,
    PassLimitExceeded
}
