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
			if (startInfo.RedirectStandardInput)
				process.StandardInput.Close();
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

		return CreateStartInfo(
			requestPath,
			executable,
			ProcessEntryPointResolver.ResolveManagedAssemblyPath(),
			ProcessEntryPointResolver.ResolveCurrentAppHostPath(),
			OperatingSystem.IsWindows());
	}

	internal static ProcessStartInfo CreateStartInfo(
		string requestPath,
		string executable,
		string? managedAssemblyPath,
		string? appHostPath,
		bool isWindows) =>
		isWindows
			? CreateWindowsStartInfo(
				executable,
				managedAssemblyPath,
				appHostPath,
				requestPath)
			: CreateUnixStartInfo(
				executable,
				managedAssemblyPath,
				requestPath);

	private static ProcessStartInfo CreateWindowsStartInfo(
		string executable,
		string? managedAssemblyPath,
		string? appHostPath,
		string requestPath)
	{
		var desktopExecutable = !string.IsNullOrWhiteSpace(appHostPath)
			? appHostPath
			: ProcessEntryPointResolver.IsDotnetHost(executable)
				? null
				: executable;
		if (desktopExecutable is not null)
		{
			var appHostStartInfo = new ProcessStartInfo
			{
				FileName = desktopExecutable,
				UseShellExecute = true
			};
			AddDesktopRequestArguments(appHostStartInfo, requestPath);
			return appHostStartInfo;
		}

		if (string.IsNullOrWhiteSpace(managedAssemblyPath))
		{
			throw new DesktopControlException(
				"DPX-DESKTOP-LAUNCH-FAILED",
				"The managed DevProjex entry point is unavailable.");
		}

		// Framework-dependent terminal launches run under the console-subsystem
		// dotnet host. Start it directly without a console window; the internal
		// desktop request prevents Program from attaching it back to the terminal.
		var dotnetStartInfo = new ProcessStartInfo
		{
			FileName = executable,
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = Directory.GetCurrentDirectory()
		};
		dotnetStartInfo.ArgumentList.Add(managedAssemblyPath);
		AddDesktopRequestArguments(dotnetStartInfo, requestPath);
		dotnetStartInfo.Environment.Remove(
			InvocationEnvironment.TerminalHostVariable);
		return dotnetStartInfo;
	}

	private static ProcessStartInfo CreateUnixStartInfo(
		string executable,
		string? managedAssemblyPath,
		string requestPath)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = "/bin/sh",
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = Directory.GetCurrentDirectory()
		};
		startInfo.ArgumentList.Add("-c");
		startInfo.ArgumentList.Add("exec \"$@\" </dev/null >/dev/null 2>&1");
		startInfo.ArgumentList.Add("devprojex-desktop");
		startInfo.ArgumentList.Add(executable);
		AddManagedEntryPointArgument(
			startInfo,
			executable,
			managedAssemblyPath);
		AddDesktopRequestArguments(startInfo, requestPath);
		startInfo.Environment.Remove(InvocationEnvironment.TerminalHostVariable);
		return startInfo;
	}

	private static void AddManagedEntryPointArgument(
		ProcessStartInfo startInfo,
		string executable,
		string? managedAssemblyPath)
	{
		if (!ProcessEntryPointResolver.IsDotnetHost(executable))
			return;

		if (string.IsNullOrWhiteSpace(managedAssemblyPath))
		{
			throw new DesktopControlException(
				"DPX-DESKTOP-LAUNCH-FAILED",
				"The managed DevProjex entry point is unavailable.");
		}

		startInfo.ArgumentList.Add(managedAssemblyPath);
	}

	private static void AddDesktopRequestArguments(
		ProcessStartInfo startInfo,
		string requestPath)
	{
		startInfo.ArgumentList.Add(DesktopLaunchRequestStore.InternalRequestArgument);
		startInfo.ArgumentList.Add(requestPath);
	}
}
