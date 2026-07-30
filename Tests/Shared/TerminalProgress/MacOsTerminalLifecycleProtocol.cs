namespace DevProjex.Tests.Terminal.Progress;

internal static class MacOsTerminalLifecycleProtocol
{
	public const string EnabledVariable =
		"DEVPROJEX_TEST_MACOS_TERMINAL_LIFECYCLE";
	public const string SessionCountVariable =
		"DEVPROJEX_TEST_MACOS_TERMINAL_SESSION_COUNT";
	public const string VerifyContinueVariable =
		"DEVPROJEX_TEST_MACOS_TERMINAL_VERIFY_CONTINUE";
	public const string DelayContinueIntoSecondSessionVariable =
		"DEVPROJEX_TEST_MACOS_TERMINAL_DELAY_CONTINUE";
	public const string VerifyActiveContinueVariable =
		"DEVPROJEX_TEST_MACOS_TERMINAL_VERIFY_ACTIVE_CONTINUE";
	public const string GatePendingInputVariable =
		"DEVPROJEX_TEST_MACOS_TERMINAL_GATE_PENDING_INPUT";
	public const string VerifyDiscardSurvivorVariable =
		"DEVPROJEX_TEST_MACOS_TERMINAL_VERIFY_DISCARD";
	public const string SpawnIsolatedChildVariable =
		"DEVPROJEX_TEST_MACOS_TERMINAL_ISOLATED_CHILD";
	public const string ReadyPrefix =
		"__DEVPROJEX_MACOS_TERMINAL_READY_";
	public const string PendingInputReady =
		"__DEVPROJEX_MACOS_PENDING_INPUT_READY__";
	public const string PendingInputReleaseFileName =
		"release-macos-pending-input";
	public const string DiscardSurvivor =
		"__DEVPROJEX_MACOS_DISCARD_SURVIVOR__";
	public const string NormalKeypadModeSequence =
		"\u001b>";
	public const string TermInfoApplicationKeypadModeSequence =
		"\u001b=";
	public const string ContinueVerified =
		"__DEVPROJEX_MACOS_SIGCONT_VERIFIED__";
	public const string ActiveContinueVerified =
		"__DEVPROJEX_MACOS_ACTIVE_SIGCONT_VERIFIED__";
	public const string IsolatedChildReady =
		"__DEVPROJEX_MACOS_ISOLATED_CHILD_READY__";
}
