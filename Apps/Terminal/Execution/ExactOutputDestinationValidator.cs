namespace DevProjex.Terminal.Execution;

internal static class ExactOutputDestinationValidator
{
	public static string ValidateAnalysis(
		string sourceRoot,
		string destination,
		bool overwrite = false) =>
		ValidateFile(sourceRoot, destination, overwrite);

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
		try
		{
			return ExactFileOutputDestinationPolicy.Resolve(
				sourceRoot,
				destination,
				overwrite);
		}
		catch (AtomicFileOutputConflictException exception)
		{
			throw new OutputDestinationConflictException(exception.Path);
		}
	}

	public static string ValidateProject(
		string sourceRoot,
		string destination,
		ProjectCopyExportFormat format,
		bool overwrite)
	{
		var replacementAllowed = format == ProjectCopyExportFormat.Zip && overwrite;
		try
		{
			return ExactFileOutputDestinationPolicy.Resolve(
				sourceRoot,
				destination,
				replacementAllowed);
		}
		catch (AtomicFileOutputConflictException exception)
		{
			throw new OutputDestinationConflictException(exception.Path);
		}
	}
}
