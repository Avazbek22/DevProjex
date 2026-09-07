namespace DevProjex.Application.Dependencies;

public static class DependencyPlatformCatalog
{
	public static bool IsDotNetExternal(
		DependencyResolverConfiguration configuration,
		string reference) =>
		DotNetAliases.Contains(reference) ||
		configuration.DotNetExternalSymbols.Contains(reference) ||
		configuration.DotNetExternalSymbols.Any(symbol =>
			symbol.EndsWith('.' + reference, StringComparison.Ordinal));

	public static bool IsDotNetAlias(string reference) => DotNetAliases.Contains(reference);

	private static readonly IReadOnlySet<string> DotNetAliases = new HashSet<string>(
		["bool", "byte", "sbyte", "char", "decimal", "double", "float", "int", "uint", "long", "ulong", "short", "ushort", "object", "string", "nint", "nuint"],
		StringComparer.Ordinal);

	public static bool IsPythonExternal(
		DependencyResolverConfiguration configuration,
		string scopeId,
		string module) =>
		IsPythonStandardLibraryModule(
			configuration,
			configuration.FindScope(scopeId)?.PythonVersion,
			module.Split('.')[0]);

	private static bool IsPythonStandardLibraryModule(
		DependencyResolverConfiguration configuration,
		string? version,
		string module)
	{
		if (version is not null && configuration.PythonStandardLibraryModules.TryGetValue(version, out var modules))
			return modules.Contains(module);
		return configuration.PythonStandardLibraryModules.Count > 0 &&
		       configuration.PythonStandardLibraryModules.Values.All(catalog => catalog.Contains(module));
	}

	public static bool IsNodeExternal(
		DependencyResolverConfiguration configuration,
		string module)
	{
		var normalized = module.StartsWith("node:", StringComparison.Ordinal)
			? module[5..]
			: module;
		return configuration.NodeBuiltInModules.Contains(normalized.Split('/')[0]);
	}
}
