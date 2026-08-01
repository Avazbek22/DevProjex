namespace DevProjex.Infrastructure.Git;

internal static class GitProcessEnvironmentSanitizer
{
	private static readonly string[] RepositorySelectionVariables =
	[
		"GIT_DIR",
		"GIT_WORK_TREE",
		"GIT_INDEX_FILE",
		"GIT_COMMON_DIR",
		"GIT_OBJECT_DIRECTORY",
		"GIT_ALTERNATE_OBJECT_DIRECTORIES",
		"GIT_NAMESPACE",
		"GIT_PREFIX",
		"GIT_CEILING_DIRECTORIES",
		"GIT_DISCOVERY_ACROSS_FILESYSTEM",
		"GIT_CONFIG",
		"GIT_CONFIG_PARAMETERS",
		"GIT_CONFIG_COUNT"
	];

	public static void RemoveRepositoryOverrides(ProcessStartInfo startInfo)
	{
		// IDEs and CI jobs may override repository discovery for their own commands.
		// Product scans must always resolve the repository selected by the user.
		foreach (var variable in RepositorySelectionVariables)
			startInfo.Environment.Remove(variable);

		List<string>? injectedConfigVariables = null;
		foreach (var variable in startInfo.Environment.Keys)
		{
			if (!variable.StartsWith("GIT_CONFIG_KEY_", StringComparison.OrdinalIgnoreCase) &&
			    !variable.StartsWith("GIT_CONFIG_VALUE_", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			injectedConfigVariables ??= [];
			injectedConfigVariables.Add(variable);
		}

		if (injectedConfigVariables is null)
			return;

		foreach (var variable in injectedConfigVariables)
			startInfo.Environment.Remove(variable);
	}
}
