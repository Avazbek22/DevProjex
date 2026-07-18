namespace DevProjex.Kernel.Models;

public static class CommandLineExecutableAliases
{
	// These suffixes mirror Scripts/release-all.ps1; the release contract test prevents silent drift.
	private static readonly string[] PublishedPortableSuffixes =
	[
		".win-x64.exe",
		".win-arm64.exe",
		".linux-x64.portable",
		".linux-arm64.portable",
		".osx-x64",
		".osx-arm64"
	];

	public const string DisplayName = "DevProjex";
	public const string WindowsPortableExecutable = "DevProjex.exe";
	public const string WindowsPortableCommandFileName = "devprojex.cmd";
	public const string UnixCommand = "devprojex";
	public const string WindowsStoreAlias = "devprojex.exe";
	public const string WindowsStoreApplicationId = "App";
	public const string WindowsStoreUiPackageExecutable = "DevProjex.Avalonia\\DevProjex.exe";

	public static IReadOnlyList<string> DocumentedCommandNames { get; } =
	[
		DisplayName,
		WindowsPortableExecutable,
		UnixCommand,
		WindowsStoreAlias
	];

	public static IReadOnlyList<string> PublicAliases { get; } =
	[
		DisplayName,
		UnixCommand,
		WindowsStoreAlias
	];

	public static bool IsPublishedPortableFileName(string? fileName)
	{
		if (string.IsNullOrWhiteSpace(fileName))
			return false;

		if (fileName.Equals(DisplayName, StringComparison.OrdinalIgnoreCase) ||
		    fileName.Equals(WindowsPortableExecutable, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		const string versionPrefix = "DevProjex.v";
		if (!fileName.StartsWith(versionPrefix, StringComparison.OrdinalIgnoreCase))
			return false;

		foreach (var suffix in PublishedPortableSuffixes)
		{
			if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
				continue;

			var versionLength = fileName.Length - versionPrefix.Length - suffix.Length;
			return versionLength > 0 && IsValidReleaseVersion(fileName.AsSpan(versionPrefix.Length, versionLength));
		}

		return false;
	}

	private static bool IsValidReleaseVersion(ReadOnlySpan<char> version)
	{
		var hasDigit = false;
		foreach (var character in version)
		{
			if (char.IsDigit(character))
			{
				hasDigit = true;
				continue;
			}

			if (character is not ('.' or '-' or '+') && !char.IsAsciiLetter(character))
				return false;
		}

		return hasDigit;
	}
}
