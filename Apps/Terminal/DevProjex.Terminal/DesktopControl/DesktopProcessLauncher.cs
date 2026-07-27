using System.Diagnostics;
using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.DesktopControl;

public sealed record DesktopLaunchResult(int ProcessId, string RequestPath);

public sealed class DesktopProcessLauncher
{
	public async Task<DesktopLaunchResult> LaunchAsync(
		DesktopOpenRequest request,
		CancellationToken cancellationToken = default)
	{
		var requestPath = await DesktopLaunchRequestStore
			.CreateAsync(request, cancellationToken)
			.ConfigureAwait(false);
		try
		{
			var startInfo = CreateStartInfo(requestPath);
			var process = Process.Start(startInfo) ??
			              throw new DesktopControlException(
				              "DPX-DESKTOP-LAUNCH-FAILED",
				              "DevProjex Desktop could not be started.");
			var processId = process.Id;
			process.Dispose();
			return new DesktopLaunchResult(processId, requestPath);
		}
		catch
		{
			DesktopInstanceRegistry.TryDelete(requestPath);
			throw;
		}
	}

	internal static ProcessStartInfo CreateStartInfo(string requestPath)
	{
		var executable = Environment.ProcessPath;
		if (string.IsNullOrWhiteSpace(executable))
		{
			throw new DesktopControlException(
				"DPX-DESKTOP-LAUNCH-FAILED",
				"The current DevProjex executable path is unavailable.");
		}

		return OperatingSystem.IsWindows()
			? CreateWindowsStartInfo(executable, requestPath)
			: CreateUnixStartInfo(executable, requestPath);
	}

	private static ProcessStartInfo CreateWindowsStartInfo(
		string executable,
		string requestPath)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = executable,
			UseShellExecute = true
		};
		AddDesktopArguments(startInfo, executable, requestPath);
		return startInfo;
	}

	private static ProcessStartInfo CreateUnixStartInfo(
		string executable,
		string requestPath)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = "/bin/sh",
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = Directory.GetCurrentDirectory()
		};
		startInfo.ArgumentList.Add("-c");
		startInfo.ArgumentList.Add("exec \"$@\" </dev/null >/dev/null 2>&1");
		startInfo.ArgumentList.Add("devprojex-desktop");
		startInfo.ArgumentList.Add(executable);
		AddDesktopArguments(startInfo, executable, requestPath);
		startInfo.Environment.Remove(InvocationEnvironment.TerminalHostVariable);
		return startInfo;
	}

	private static void AddDesktopArguments(
		ProcessStartInfo startInfo,
		string executable,
		string requestPath)
	{
		var entryPath = ProcessEntryPointResolver.ResolveManagedAssemblyPath();
		if (!string.IsNullOrWhiteSpace(entryPath) &&
		    ProcessEntryPointResolver.IsDotnetHost(executable))
		{
			startInfo.ArgumentList.Add(entryPath);
		}

		startInfo.ArgumentList.Add(DesktopLaunchRequestStore.InternalRequestArgument);
		startInfo.ArgumentList.Add(requestPath);
	}
}
