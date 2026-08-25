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

	public static string NormalizePortableRelative(string value)
	{
		if (string.IsNullOrEmpty(value) || value == ".")
			return NormalizeRelative(value);

		if (IsRootedOnAnySupportedPlatform(value))
		{
			throw new ProjectContextValidationException(
				InvalidPathCode,
				"Selected paths must be relative on every supported platform.");
		}

		return NormalizeRelative(value);
	}

	private static bool IsRootedOnAnySupportedPlatform(string value) =>
		Path.IsPathRooted(value) ||
		value.StartsWith('/') ||
		value.StartsWith('\\') ||
		(value.Length >= 2 &&
		 char.IsAsciiLetter(value[0]) &&
		 value[1] == ':');
}
