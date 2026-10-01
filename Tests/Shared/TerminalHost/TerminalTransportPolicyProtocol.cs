namespace DevProjex.Tests.Terminal.Host;

/// <summary>
/// Test-protocol contract between a test and the separate terminal test host executable.
/// </summary>
/// <remarks>
/// The shipped application reads none of these values. They exist so a child process started by a
/// test can build a synthetic local Git remote, which the shipped transport policy refuses.
/// </remarks>
internal static class TerminalTransportPolicyProtocol
{
	public const string AllowLocalFileTransportVariable =
		"DEVPROJEX_TEST_HOST_ALLOW_FILE_GIT";

	public const string Enabled = "1";

	/// <summary>
	/// Leading argument that asks the test host to run an ordinary terminal command line.
	/// </summary>
	public const string TerminalCommandArgument = "--terminal";

	public static bool IsLocalFileTransportRequested() => string.Equals(
		Environment.GetEnvironmentVariable(AllowLocalFileTransportVariable),
		Enabled,
		StringComparison.Ordinal);
}
