namespace DevProjex.Infrastructure.TerminalCommands;

public static class McpConnectionExecutablePathResolver
{
	public static string Resolve(
		TerminalCommandSetupSnapshot snapshot,
		string? localApplicationDataPath = null) =>
		Resolve(snapshot, DetectPlatform(), localApplicationDataPath);

	public static string Resolve(
		TerminalCommandSetupSnapshot snapshot,
		TerminalCommandHostPlatform platform,
		string? localApplicationDataPath = null)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		if (platform == TerminalCommandHostPlatform.Windows &&
			snapshot.State == TerminalCommandSetupState.ManagedByOperatingSystem)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataPath);
			return JoinWindowsPath(
				localApplicationDataPath,
				"Microsoft",
				"WindowsApps",
				snapshot.CommandName);
		}

		if (string.IsNullOrWhiteSpace(snapshot.TargetExecutablePath))
			throw new InvalidOperationException("The installed DevProjex executable path is unavailable.");
		if (ProcessEntryPointResolver.IsDotnetHost(snapshot.TargetExecutablePath) &&
			ProcessEntryPointResolver.ResolveCurrentAppHostPath() is { } appHostPath)
		{
			return appHostPath;
		}
		return snapshot.TargetExecutablePath;
	}

	private static string JoinWindowsPath(string root, params string[] segments)
	{
		var result = root.TrimEnd('\\', '/');
		foreach (var segment in segments)
			result += "\\" + segment.Trim('\\', '/');
		return result;
	}

	private static TerminalCommandHostPlatform DetectPlatform()
	{
		if (OperatingSystem.IsWindows())
			return TerminalCommandHostPlatform.Windows;
		if (OperatingSystem.IsLinux())
			return TerminalCommandHostPlatform.Linux;
		if (OperatingSystem.IsMacOS())
			return TerminalCommandHostPlatform.MacOS;
		return TerminalCommandHostPlatform.Other;
	}
}
