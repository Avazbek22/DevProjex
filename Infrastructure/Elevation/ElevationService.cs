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

			var psi = new ProcessStartInfo
			{
				FileName = exePath,
				UseShellExecute = true,
				Verb = "runas"
			};
			foreach (var argument in arguments)
				psi.ArgumentList.Add(argument);

			Process.Start(psi);
			return true;
		}
		catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
		{
			return false;
		}
#endif
	}
}
