namespace DevProjex.Terminal.Tui;

internal enum TerminalOperationPhase
{
	CloneConnecting,
	ProjectLoading,
	Preparing,
	WritingContext
}

internal interface ITerminalOperationObserver
{
	ValueTask ObservePhaseAsync(
		TerminalOperationPhase phase,
		CancellationToken cancellationToken);

	void ObserveProgress(
		ProjectCopyExportProgress progress,
		CancellationToken cancellationToken);
}

internal sealed class NullTerminalOperationObserver : ITerminalOperationObserver
{
	public static NullTerminalOperationObserver Instance { get; } = new();

	private NullTerminalOperationObserver()
	{
	}

	public ValueTask ObservePhaseAsync(
		TerminalOperationPhase phase,
		CancellationToken cancellationToken) =>
		ValueTask.CompletedTask;

	public void ObserveProgress(
		ProjectCopyExportProgress progress,
		CancellationToken cancellationToken)
	{
	}
}
