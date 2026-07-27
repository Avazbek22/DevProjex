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

	public static bool IsSingleFile() => ResolveManagedAssemblyPath() is null;

	public static bool IsDotnetHost(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return false;

		// Process paths can come from another target OS while packaging or testing.
		// Resolve both directory separators instead of applying host-OS path semantics.
		var trimmedPath = path.Trim();
		var separatorIndex = Math.Max(
			trimmedPath.LastIndexOf('/'),
			trimmedPath.LastIndexOf('\\'));
		var fileName = trimmedPath[(separatorIndex + 1)..];
		return Path.GetFileNameWithoutExtension(fileName)
			.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
	}
}
