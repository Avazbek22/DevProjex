namespace DevProjex.Tests.Terminal;

public sealed class TerminalPtyHarnessContractTests
{
	[Fact]
	public void UnixRestorationHandshakeReportsBothOpaqueTermiosSnapshotsOnMismatch()
	{
		var command = TerminalPtyHarness.BuildUnixShellCommand(
			"exec-devprojex",
			writeShellCompletionMarker: true);

		Assert.Contains(
			"dpx_stty_before=$(stty -g 2>/dev/null || true)",
			command,
			StringComparison.Ordinal);
		Assert.Contains(
			"dpx_stty_after=$(stty -g 2>/dev/null || true)",
			command,
			StringComparison.Ordinal);
		Assert.Contains(
			"printf '%s%s before=%s after=%s\\n'",
			command,
			StringComparison.Ordinal);
		Assert.Contains(
			"\"$dpx_stty_before\" \"$dpx_stty_after\"",
			command,
			StringComparison.Ordinal);
		Assert.Contains(
			"IFS= read -r dpx_sync",
			command,
			StringComparison.Ordinal);
	}

	[Fact]
	public void TermiosMismatchDiagnosticRetainsOpaqueBeforeAndAfterSnapshots()
	{
		var output =
			$"{TerminalPtyHarness.ShellTerminalStateMismatchMarker} " +
			"before=aaa:bbb after=ccc:ddd\r\n" +
			TerminalPtyHarness.ShellCompletionMarker;
		var markerIndex = output.IndexOf(
			TerminalPtyHarness.ShellCompletionMarker,
			StringComparison.Ordinal);

		var diagnostic = TerminalPtyStateAssertions.FindUnixTerminalStateMismatch(
			output,
			markerIndex);

		Assert.Equal(
			$"{TerminalPtyHarness.ShellTerminalStateMismatchMarker} " +
			"before=aaa:bbb after=ccc:ddd",
			diagnostic);
	}

	[Fact]
	public void TermiosMismatchAfterShellCompletionIsIgnored()
	{
		var output =
			TerminalPtyHarness.ShellTerminalStateRestoredMarker + "\n" +
			TerminalPtyHarness.ShellCompletionMarker + "\n" +
			TerminalPtyHarness.ShellTerminalStateMismatchMarker;
		var markerIndex = output.IndexOf(
			TerminalPtyHarness.ShellCompletionMarker,
			StringComparison.Ordinal);

		Assert.Null(
			TerminalPtyStateAssertions.FindUnixTerminalStateMismatch(
				output,
				markerIndex));
	}

	[Fact]
	public void DataRootKeepsGitObjectFilesBelowTheLegacyWindowsPathLimit()
	{
		const int legacyWindowsPathLimit = 260;
		const string hostedRunnerTemp =
			@"C:\Users\runneradmin\AppData\Local\Temp";
		var invocationId = new string('a', 32);
		var repositoryDirectory =
			new string('r', 32) + "_" + new string('A', 29);
		var repositoryPath = string.Join(
			'\\',
			hostedRunnerTemp,
			TerminalPtyHarness.DataRootDirectoryName,
			invocationId,
			"devprojex",
			"RepoCache",
			".staging",
			repositoryDirectory);
		var looseObjectPath = string.Join(
			'\\',
			repositoryPath,
			".git",
			"objects",
			"ab",
			new string('0', 38));
		var packFileName = "pack-" + new string('0', 40) + ".pack";
		var packPath = string.Join(
			'\\',
			repositoryPath,
			".git",
			"objects",
			"pack",
			packFileName);

		AssertBelowLegacyLimit(looseObjectPath);
		AssertBelowLegacyLimit(packPath);

		void AssertBelowLegacyLimit(string path)
		{
			Assert.True(
				path.Length < legacyWindowsPathLimit,
				$"The PTY fixture Git object path is {path.Length} characters: {path}");
		}
	}
}
