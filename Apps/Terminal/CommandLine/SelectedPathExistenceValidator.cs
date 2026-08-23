namespace DevProjex.Terminal.CommandLine;

internal static class SelectedPathExistenceValidator
{
	public static void Validate(
		string projectRoot,
		IReadOnlyCollection<string>? selectedPaths)
	{
		if (selectedPaths is null || selectedPaths.Count == 0)
			return;

		var normalizedRoot = PathUtility.Normalize(projectRoot);
		foreach (var selectedPath in selectedPaths)
		{
			var relativePath = ProjectSelectionPath.NormalizeRelative(selectedPath);
			var fullPath = ResolveFullPath(normalizedRoot, relativePath, selectedPath);
			try
			{
				_ = File.GetAttributes(fullPath);
			}
			catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
			{
				throw Missing(selectedPath);
			}
		}
	}

	private static string ResolveFullPath(
		string projectRoot,
		string relativePath,
		string originalPath)
	{
		try
		{
			return relativePath.Length == 0
				? projectRoot
				: Path.GetFullPath(Path.Combine(
					projectRoot,
					relativePath.Replace('/', Path.DirectorySeparatorChar)));
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
			throw new ProjectContextValidationException(
				ProjectSelectionPath.InvalidPathCode,
				"Selected path is invalid.",
				originalPath);
		}
	}

	private static ProjectContextValidationException Missing(string path) =>
		new(
			"DPX-SELECTION-PATH-MISSING",
			"Selected path does not exist in the project.",
			path);
}
