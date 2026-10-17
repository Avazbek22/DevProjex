using DevProjex.Infrastructure.Persistence;
using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.DesktopControl;

public sealed class DesktopControlPaths(Func<string>? dataRootProvider = null)
{
	private readonly Func<string> _dataRootProvider = dataRootProvider ?? ResolveDataRoot;

	public string RootDirectory => Path.Combine(_dataRootProvider(), "DevProjex", "desktop-control");
	public string RegistryDirectory => Path.Combine(RootDirectory, "instances");
	public string SocketDirectory => Path.Combine(RootDirectory, "sockets");
	public string RequestDirectory => Path.Combine(RootDirectory, "requests");
	public string DiagnosticDirectory => Path.Combine(RootDirectory, "diagnostics");

	public string GetRegistrationPath(string instanceId) =>
		Path.Combine(RegistryDirectory, $"{instanceId}.json");

	public string GetSocketPath(string instanceId)
	{
		var fileName = $"dpx-{instanceId[..Math.Min(16, instanceId.Length)]}.sock";
		var candidate = Path.Combine(SocketDirectory, fileName);
		if (Encoding.UTF8.GetByteCount(candidate) <= 96)
			return candidate;

		if (FlatpakEnvironment.Current.IsActive)
		{
			var sharedDirectory = _dataRootProvider();
			Directory.CreateDirectory(sharedDirectory);
			var sharedPath = Path.Combine(sharedDirectory, fileName);
			if (Encoding.UTF8.GetByteCount(sharedPath) > 96)
				throw new IOException("The shared Flatpak socket path exceeds the Unix socket path limit.");
			return sharedPath;
		}
		return Path.Combine(Path.GetTempPath(), fileName);
	}

	private static string ResolveDataRoot()
	{
		var isolatedRoot = UserDataPathResolver.ResolveInternalDataRoot(
			Environment.GetEnvironmentVariable(InvocationEnvironment.InternalDataRootVariable));
		if (isolatedRoot is not null)
			return isolatedRoot;

		var xdgRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
		if (FlatpakEnvironment.Current.IsActive &&
			!string.IsNullOrWhiteSpace(xdgRuntime) && Path.IsPathFullyQualified(xdgRuntime))
			return Path.Combine(xdgRuntime, "app", FlatpakEnvironment.Current.AppId!);
		if (!OperatingSystem.IsWindows() &&
		    !string.IsNullOrWhiteSpace(xdgRuntime) &&
		    Path.IsPathFullyQualified(xdgRuntime))
			return xdgRuntime;

		return UserDataPathResolver.GetStateRoot();
	}
}
