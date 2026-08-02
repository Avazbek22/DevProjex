namespace DevProjex.Tests.Terminal.Progress;

internal static class TerminalProgressCheckpointProtocol
{
	public const string CheckpointsVariable =
		"DEVPROJEX_TEST_TUI_PROGRESS_CHECKPOINTS";
	public const string PhasesVariable =
		"DEVPROJEX_TEST_TUI_PROGRESS_PHASES";
	public const string DirectoryName = "tui-progress-checkpoints";

	public static string GetReachedFileName(string checkpoint) =>
		$"reached-{checkpoint}";

	public static string GetReleaseFileName(string checkpoint) =>
		$"release-{checkpoint}";
}
