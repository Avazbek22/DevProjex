namespace DevProjex.Terminal.CommandLine;

internal static class McpRootSourceResolver
{
	public static IReadOnlyList<string> Resolve(
		IReadOnlyList<string> explicitRoots,
		IReadOnlyDictionary<string, string?> variables,
		string currentDirectory)
	{
		ArgumentNullException.ThrowIfNull(explicitRoots);
		ArgumentNullException.ThrowIfNull(variables);
		ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
		if (explicitRoots.Count > 0)
			return explicitRoots;
		if (variables.TryGetValue("CLAUDE_PROJECT_DIR", out var projectDirectory) &&
		    !string.IsNullOrWhiteSpace(projectDirectory))
		{
			return [projectDirectory];
		}
		return [currentDirectory];
	}
}
