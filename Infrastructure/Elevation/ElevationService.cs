using System.ComponentModel;
using System.Security.Principal;

namespace DevProjex.Infrastructure.Elevation;

public sealed class ElevationService : IElevationService
{
	public bool IsAdministrator
	{
		get
		{
			if (!OperatingSystem.IsWindows()) return false;

			using var identity = WindowsIdentity.GetCurrent();
			var principal = new WindowsPrincipal(identity);
			return principal.IsInRole(WindowsBuiltInRole.Administrator);
		}
	}

	public bool TryRelaunchAsAdministrator(IReadOnlyList<string> arguments)
	{
		// Store builds must never trigger UAC or relaunch with elevation.
#if DEVPROJEX_STORE
		return false;
#else
		if (!OperatingSystem.IsWindows()) return false;

		try
		{
			var exePath = Environment.ProcessPath;
			if (string.IsNullOrWhiteSpace(exePath)) return false;

			var psi = CreateRelaunchStartInfo(
				arguments,
				exePath,
				ProcessEntryPointResolver.ResolveManagedAssemblyPath(),
				ProcessEntryPointResolver.ResolveCurrentAppHostPath());
			Process.Start(psi);
			return true;
		}
		catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
		{
			return false;
		}
#endif
	}

	internal static ProcessStartInfo CreateRelaunchStartInfo(
		IReadOnlyList<string> arguments,
		string processPath,
		string? managedAssemblyPath,
		string? appHostPath)
	{
		var fileName = !string.IsNullOrWhiteSpace(appHostPath)
			? appHostPath
			: processPath;
		var startInfo = new ProcessStartInfo
		{
			FileName = fileName,
			UseShellExecute = true,
			Verb = "runas"
		};

		if (ProcessEntryPointResolver.IsDotnetHost(fileName))
		{
			if (string.IsNullOrWhiteSpace(managedAssemblyPath))
			{
				throw new InvalidOperationException(
					"The managed DevProjex entry point is unavailable.");
			}

			startInfo.ArgumentList.Add(managedAssemblyPath);
		}

		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);

		return startInfo;
	}
}
