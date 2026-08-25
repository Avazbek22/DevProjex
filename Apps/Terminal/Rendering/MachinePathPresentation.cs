namespace DevProjex.Terminal.Rendering;

internal static class MachinePathPresentation
{
	public static string Normalize(string path)
	{
		ArgumentNullException.ThrowIfNull(path);
		return OperatingSystem.IsWindows() || IsWindowsAbsolutePath(path)
			? path.Replace('\\', '/')
			: path;
	}

	private static bool IsWindowsAbsolutePath(string path) =>
		path.StartsWith("\\\\", StringComparison.Ordinal) ||
		(path.Length >= 3 &&
		 IsAsciiLetter(path[0]) &&
		 path[1] == ':' &&
		 path[2] is '\\' or '/');

	private static bool IsAsciiLetter(char value) =>
		value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
