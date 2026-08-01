using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Terminal.DesktopControl;

public sealed class DesktopControlPaths(Func<string>? dataRootProvider = null)
{
	private readonly Func<string> _dataRootProvider = dataRootProvider ?? ResolveDataRoot;

	public string RootDirectory => Path.Combine(_dataRootProvider(), "DevProjex", "desktop-control");
	public string RegistryDirectory => Path.Combine(RootDirectory, "instances");
	public string SocketDirectory => Path.Combine(RootDirectory, "sockets");

	public string GetRegistrationPath(string instanceId) =>
		Path.Combine(RegistryDirectory, $"{instanceId}.json");

	public string GetSocketPath(string instanceId)
	{
		var fileName = $"dpx-{instanceId[..Math.Min(16, instanceId.Length)]}.sock";
		var candidate = Path.Combine(SocketDirectory, fileName);
		if (Encoding.UTF8.GetByteCount(candidate) <= 96)
			return candidate;

		return Path.Combine(Path.GetTempPath(), fileName);
	}

	private static string ResolveDataRoot()
	{
		var xdgRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
		if (!OperatingSystem.IsWindows() &&
		    !string.IsNullOrWhiteSpace(xdgRuntime) &&
		    Path.IsPathFullyQualified(xdgRuntime))
			return xdgRuntime;

		return UserDataPathResolver.GetStateRoot();
	}
}
