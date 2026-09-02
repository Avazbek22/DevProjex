namespace DevProjex.Terminal.CommandLine;

internal static class SelectedPathExistenceValidator
{
	private const int WindowsInvalidName = 123;

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
			catch (PathTooLongException)
			{
				throw Invalid(selectedPath);
			}
			catch (IOException exception) when (IsInvalidWindowsPath(exception))
			{
				throw Invalid(selectedPath);
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
		catch (Exception exception) when (
			exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			throw Invalid(originalPath);
		}
	}

	private static bool IsInvalidWindowsPath(IOException exception) =>
		OperatingSystem.IsWindows() &&
		(exception.HResult & 0xFFFF) == WindowsInvalidName;

	private static ProjectContextValidationException Invalid(string path) =>
		new(
			ProjectSelectionPath.InvalidPathCode,
			"Selected path is invalid.",
			path);

	private static ProjectContextValidationException Missing(string path) =>
		new(
			"DPX-SELECTION-PATH-MISSING",
			"Selected path does not exist in the project.",
			path);
}
