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

	public static bool IsDotnetHost(string? path) =>
		!string.IsNullOrWhiteSpace(path) &&
		Path.GetFileNameWithoutExtension(path)
			.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
}
