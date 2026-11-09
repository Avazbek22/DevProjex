namespace DevProjex.Terminal.CommandLine;

internal enum ExactOutputPathProblem
{
	None,
	Empty,
	TrailingSeparator,
	InvalidSyntax,
	DeviceName
}

/// <summary>
/// Checks the raw <c>-o PATH</c> text of commands that write exactly one file, before any
/// normalization can turn a folder spelling or a device name into a different destination.
/// </summary>
internal static class ExactOutputPathSyntax
{
	private const string StandardOutput = "-";

	private static readonly HashSet<string> WindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"CON", "PRN", "AUX", "NUL",
		"COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
		"LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
	};

	private static readonly char[] WindowsInvalidNameCharacters =
	[
		'"', '<', '>', '|', ':', '*', '?',
		.. Enumerable.Range(0, 32).Select(static value => (char)value)
	];

	public static ExactOutputPathProblem Check(string? path) =>
		Check(path, OperatingSystem.IsWindows());

	internal static ExactOutputPathProblem Check(string? path, bool isWindows)
	{
		if (path is null || path == StandardOutput)
			return ExactOutputPathProblem.None;
		if (string.IsNullOrWhiteSpace(path))
			return ExactOutputPathProblem.Empty;
		if (IsSeparator(path[^1], isWindows))
			return ExactOutputPathProblem.TrailingSeparator;
		if (path.Contains('\0'))
			return ExactOutputPathProblem.InvalidSyntax;
		if (isWindows)
		{
			var windowsProblem = CheckWindowsPath(path);
			if (windowsProblem != ExactOutputPathProblem.None)
				return windowsProblem;
		}

		try
		{
			_ = Path.GetFullPath(path);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
			return ExactOutputPathProblem.InvalidSyntax;
		}

		return ExactOutputPathProblem.None;
	}

	private static bool IsSeparator(char character, bool isWindows) =>
		character == '/' || (isWindows && character == '\\');

	private static ExactOutputPathProblem CheckWindowsPath(string path)
	{
		var rootLength = GetWindowsRootLength(path, out var isDeviceNamespace);
		if (isDeviceNamespace)
			return ExactOutputPathProblem.DeviceName;

		var segments = path[rootLength..].Split(['/', '\\']);
		if (segments.Any(static segment => segment.IndexOfAny(WindowsInvalidNameCharacters) >= 0))
			return ExactOutputPathProblem.InvalidSyntax;

		return IsWindowsDeviceName(segments[^1])
			? ExactOutputPathProblem.DeviceName
			: ExactOutputPathProblem.None;
	}

	// Windows resolves a reserved name to its device regardless of extension or trailing spaces.
	private static bool IsWindowsDeviceName(string fileName)
	{
		var extensionIndex = fileName.IndexOf('.');
		var baseName = (extensionIndex >= 0 ? fileName[..extensionIndex] : fileName).TrimEnd(' ');
		return WindowsDeviceNames.Contains(baseName);
	}

	private static int GetWindowsRootLength(string path, out bool isDeviceNamespace)
	{
		isDeviceNamespace = false;
		if (path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
		    path.StartsWith("//./", StringComparison.Ordinal))
		{
			isDeviceNamespace = true;
			return path.Length;
		}

		var offset = 0;
		if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
		{
			offset = 4;
			if (path.AsSpan(offset).StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
				return GetUncRootLength(path, offset + 4);
		}
		else if (path.Length >= 2 && IsSeparator(path[0], true) && IsSeparator(path[1], true))
		{
			return GetUncRootLength(path, 2);
		}

		if (path.Length >= offset + 2 && char.IsAsciiLetter(path[offset]) && path[offset + 1] == ':')
			return offset + 2;
		return offset;
	}

	// \\server\share\ forms the root of a UNC path; everything after it is ordinary segments.
	private static int GetUncRootLength(string path, int start)
	{
		var separatorsToSkip = 2;
		for (var index = start; index < path.Length; index++)
		{
			if (!IsSeparator(path[index], true))
				continue;
			separatorsToSkip--;
			if (separatorsToSkip == 0)
				return index;
		}

		return path.Length;
	}
}
