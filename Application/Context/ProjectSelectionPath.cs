namespace DevProjex.Application.Context;

public static class ProjectSelectionPath
{
	public const string InvalidPathCode = "DPX-SELECTION-PATH-INVALID";

	public static string NormalizeRelative(string value)
	{
		if (string.IsNullOrEmpty(value) || value == ".")
			return string.Empty;

		if (Path.IsPathRooted(value))
		{
			throw new ProjectContextValidationException(
				InvalidPathCode,
				"Selected paths must be relative to the project root.");
		}

		var separators = OperatingSystem.IsWindows()
			? new[] { '\\', '/' }
			: ['/'];
		var segments = value.Split(separators, StringSplitOptions.RemoveEmptyEntries);
		if (segments.Any(static segment => segment == ".."))
		{
			throw new ProjectContextValidationException(
				InvalidPathCode,
				"Selected paths cannot contain parent traversal.");
		}

		return string.Join('/', segments.Where(static segment => segment != "."));
	}

}
