namespace DevProjex.Kernel.Models;

public static class CommandLineExecutableAliases
{
	public const string DisplayName = "DevProjex";
	public const string WindowsPortableExecutable = "DevProjex.exe";
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
}
