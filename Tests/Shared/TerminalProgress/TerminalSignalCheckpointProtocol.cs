namespace DevProjex.Tests.Terminal.Progress;

internal static class TerminalSignalCheckpointProtocol
{
	public const string EnabledVariable =
		"DEVPROJEX_TEST_SIGNAL_CHECKPOINT_PROTOCOL";
	public const string Ready = "READY";
	public const string CancellationObserved = "CANCELING";
}
