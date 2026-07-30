using System.Diagnostics;
using DevProjex.Infrastructure.FileSystem;
using DevProjex.Infrastructure.Git;
using DevProjex.Infrastructure.TerminalCommands;
using DevProjex.Terminal.DesktopControl;
using DevProjex.Terminal.Execution;
using DevProjex.Terminal.Tui;

namespace DevProjex.Tests.Unit;

public sealed class ChildProcessTerminalIsolationTests
{
	[Fact]
	public void GitCommandsUseNonInteractiveStandardTransports()
	{
		var startInfo = GitRepositoryService.CreateGitCommandStartInfo(
			workingDirectory: null,
			arguments: "--version");

		AssertNonInteractive(startInfo);
		Assert.Equal("0", startInfo.Environment["GIT_TERMINAL_PROMPT"]);
		Assert.Equal(
			GitRepositoryService.NonInteractiveSshCommand,
			startInfo.Environment["GIT_SSH_COMMAND"]);
		Assert.Equal("ssh", startInfo.Environment["GIT_SSH_VARIANT"]);
		Assert.Equal(string.Empty, startInfo.Environment["GIT_ASKPASS"]);
		Assert.Equal(string.Empty, startInfo.Environment["SSH_ASKPASS"]);
		Assert.Equal("never", startInfo.Environment["SSH_ASKPASS_REQUIRE"]);
		Assert.Equal("Never", startInfo.Environment["GCM_INTERACTIVE"]);
		Assert.Equal("false", startInfo.Environment["GCM_GUI_PROMPT"]);
	}

	[Fact]
	public void TrackedIndexCommandsDoNotAcquireTheParentTerminal()
	{
		var startInfo = GitTrackedPathIndexCache.CreateStartInfo(
			Path.GetTempPath());

		AssertNonInteractive(startInfo);
		Assert.Equal("0", startInfo.Environment["GIT_TERMINAL_PROMPT"]);
	}

	[Fact]
	public void TermInfoCapabilityProbeDoesNotAcquireTheParentTerminal()
	{
		var startInfo =
			MacOsTerminalModeCapabilityProvider.CreateTputStartInfo("rmkx");

		AssertNonInteractive(startInfo);
		Assert.Equal(["rmkx"], startInfo.ArgumentList);
	}

	[Fact]
	public void DoctorGitProbeDoesNotAcquireTheParentTerminal()
	{
		var startInfo = DoctorCommandHandler.CreateGitVersionStartInfo();

		AssertNonInteractive(startInfo);
		Assert.Equal("0", startInfo.Environment["GIT_TERMINAL_PROMPT"]);
	}

	[Fact]
	public void LauncherValidationDoesNotAcquireTheParentTerminal()
	{
		var startInfo =
			TerminalCommandSetupService.CreateLauncherValidationStartInfo(
				"dotnet");

		AssertNonInteractive(startInfo);
	}

	[Fact]
	public void UnixDesktopLaunchDoesNotAcquireTheParentTerminal()
	{
		if (OperatingSystem.IsWindows())
			return;

		var startInfo = DesktopProcessLauncher.CreateStartInfo(
			"desktop-request.json");

		AssertNonInteractive(startInfo);
	}

	private static void AssertNonInteractive(ProcessStartInfo startInfo)
	{
		Assert.False(startInfo.UseShellExecute);
		Assert.True(startInfo.RedirectStandardInput);
		Assert.True(startInfo.RedirectStandardOutput);
		Assert.True(startInfo.RedirectStandardError);
	}
}
