namespace DevProjex.Infrastructure.Persistence;

public static class UserDataPathResolver
{
	public static string GetConfigurationRoot() =>
		Resolve(
			Environment.SpecialFolder.ApplicationData,
			OperatingSystem.IsWindows(),
			Environment.GetFolderPath,
			Environment.GetEnvironmentVariable);

	public static string GetLocalDataRoot() =>
		Resolve(
			Environment.SpecialFolder.LocalApplicationData,
			OperatingSystem.IsWindows(),
			Environment.GetFolderPath,
			Environment.GetEnvironmentVariable);

	internal static string Resolve(
		Environment.SpecialFolder folder,
		bool isWindows,
		Func<Environment.SpecialFolder, Environment.SpecialFolderOption, string> specialFolderProvider,
		Func<string, string?> environmentProvider)
	{
		ArgumentNullException.ThrowIfNull(specialFolderProvider);
		ArgumentNullException.ThrowIfNull(environmentProvider);

		var platformPath = specialFolderProvider(
			folder,
			Environment.SpecialFolderOption.DoNotVerify);
		if (IsUsableAbsolutePath(platformPath))
			return Path.GetFullPath(platformPath);

		if (!isWindows)
		{
			var xdgVariable = folder == Environment.SpecialFolder.ApplicationData
				? "XDG_CONFIG_HOME"
				: "XDG_DATA_HOME";
			var xdgPath = environmentProvider(xdgVariable);
			if (IsUsableAbsolutePath(xdgPath))
				return Path.GetFullPath(xdgPath!);
		}

		var home = specialFolderProvider(
			Environment.SpecialFolder.UserProfile,
			Environment.SpecialFolderOption.DoNotVerify);
		if (!IsUsableAbsolutePath(home))
		{
			home = environmentProvider(isWindows ? "USERPROFILE" : "HOME");
		}

		if (!IsUsableAbsolutePath(home))
			throw new InvalidOperationException("A safe absolute user data directory could not be resolved.");

		var relativePath = (isWindows, folder) switch
		{
			(true, Environment.SpecialFolder.ApplicationData) => Path.Combine("AppData", "Roaming"),
			(true, _) => Path.Combine("AppData", "Local"),
			(false, Environment.SpecialFolder.ApplicationData) => ".config",
			(false, _) => Path.Combine(".local", "share")
		};
		return Path.GetFullPath(Path.Combine(home!, relativePath));
	}

	private static bool IsUsableAbsolutePath(string? path) =>
		!string.IsNullOrWhiteSpace(path) &&
		Path.IsPathFullyQualified(path);
}
