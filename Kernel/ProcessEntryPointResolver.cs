namespace DevProjex.Kernel;

public static class ProcessEntryPointResolver
{
	public static string? ResolveManagedAssemblyPath()
	{
		var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
		if (string.IsNullOrWhiteSpace(assemblyName))
			return null;

		var candidate = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
		return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
	}

	public static string? ResolveCurrentArtifactPath()
	{
		var processPath = Environment.ProcessPath;
		return IsDotnetHost(processPath)
			? ResolveManagedAssemblyPath()
			: processPath;
	}

	public static string? ResolveCurrentAppHostPath()
	{
		var processPath = Environment.ProcessPath;
		if (!IsDotnetHost(processPath))
			return processPath;

		var managedAssemblyPath = ResolveManagedAssemblyPath();
		if (string.IsNullOrWhiteSpace(managedAssemblyPath))
			return null;

		var directory = Path.GetDirectoryName(managedAssemblyPath);
		var assemblyName = Path.GetFileNameWithoutExtension(managedAssemblyPath);
		if (string.IsNullOrWhiteSpace(directory) ||
		    string.IsNullOrWhiteSpace(assemblyName))
		{
			return null;
		}

		var appHostFileName = OperatingSystem.IsWindows()
			? $"{assemblyName}.exe"
			: assemblyName;
		var candidate = Path.Combine(directory, appHostFileName);
		return File.Exists(candidate)
			? Path.GetFullPath(candidate)
			: null;
	}

	public static bool IsSingleFile() => ResolveManagedAssemblyPath() is null;

	public static bool IsDotnetHost(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return false;

		// Process paths can come from another target OS while packaging or testing.
		// Resolve both directory separators instead of applying host-OS path semantics.
		var separatorIndex = Math.Max(
			path.LastIndexOf('/'),
			path.LastIndexOf('\\'));
		var fileName = path[(separatorIndex + 1)..];
		return Path.GetFileNameWithoutExtension(fileName)
			.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
	}
}
