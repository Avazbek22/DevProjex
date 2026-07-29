namespace DevProjex.Terminal.Execution;

internal static class ExactOutputDestinationValidator
{
	public static string ValidateAnalysis(
		string sourceRoot,
		string destination) =>
		ValidateFile(sourceRoot, destination, overwrite: false);

	public static string ValidateContext(
		string sourceRoot,
		string destination,
		bool overwrite) =>
		ValidateFile(sourceRoot, destination, overwrite);

	private static string ValidateFile(
		string sourceRoot,
		string destination,
		bool overwrite)
	{
		var fullPath = Path.GetFullPath(destination);
		ProjectCopyExportService.EnsureDestinationOutsideProject(sourceRoot, fullPath);
		if (Directory.Exists(fullPath) || (!overwrite && File.Exists(fullPath)))
			throw new OutputDestinationConflictException(fullPath);

		return fullPath;
	}

	public static string ValidateProject(
		string sourceRoot,
		string destination,
		ProjectCopyExportFormat format,
		bool overwrite)
	{
		var fullPath = Path.GetFullPath(destination);
		ProjectCopyExportService.EnsureDestinationOutsideProject(sourceRoot, fullPath);
		if (Directory.Exists(fullPath) ||
		    (File.Exists(fullPath) &&
		     !(format == ProjectCopyExportFormat.Zip && overwrite)))
		{
			throw new OutputDestinationConflictException(fullPath);
		}

		return fullPath;
	}
}
