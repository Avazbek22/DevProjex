namespace DevProjex.Infrastructure.Persistence;

internal enum UserDataDirectoryKind
{
	Configuration,
	Data,
	State,
	Cache
}

public static class UserDataPathResolver
{
	public static string GetConfigurationRoot() =>
		Resolve(
			UserDataDirectoryKind.Configuration,
			OperatingSystem.IsWindows(),
			Environment.GetFolderPath,
			Environment.GetEnvironmentVariable);

	public static string GetLocalDataRoot() =>
		Resolve(
			UserDataDirectoryKind.Data,
			OperatingSystem.IsWindows(),
			Environment.GetFolderPath,
			Environment.GetEnvironmentVariable);

	public static string GetStateRoot() =>
		Resolve(
			UserDataDirectoryKind.State,
			OperatingSystem.IsWindows(),
			Environment.GetFolderPath,
			Environment.GetEnvironmentVariable);

	public static string GetCacheRoot() =>
		Resolve(
			UserDataDirectoryKind.Cache,
			OperatingSystem.IsWindows(),
			Environment.GetFolderPath,
			Environment.GetEnvironmentVariable);

	internal static string GetLegacyLocalDataRoot() =>
		ResolveLegacyLocalData(
			OperatingSystem.IsWindows(),
			Environment.GetFolderPath,
			Environment.GetEnvironmentVariable);

	internal static string Resolve(
		Environment.SpecialFolder folder,
		bool isWindows,
		Func<Environment.SpecialFolder, Environment.SpecialFolderOption, string> specialFolderProvider,
		Func<string, string?> environmentProvider)
		=> Resolve(
			folder == Environment.SpecialFolder.ApplicationData
				? UserDataDirectoryKind.Configuration
				: UserDataDirectoryKind.Data,
			isWindows,
			specialFolderProvider,
			environmentProvider);

	internal static string Resolve(
		UserDataDirectoryKind kind,
		bool isWindows,
		Func<Environment.SpecialFolder, Environment.SpecialFolderOption, string> specialFolderProvider,
		Func<string, string?> environmentProvider)
	{
		ArgumentNullException.ThrowIfNull(specialFolderProvider);
		ArgumentNullException.ThrowIfNull(environmentProvider);

		if (!isWindows)
		{
			var xdgPath = environmentProvider(GetXdgVariable(kind));
			if (IsUsableAbsolutePath(xdgPath))
				return Path.GetFullPath(xdgPath!);
		}

		if (isWindows ||
		    kind is UserDataDirectoryKind.Configuration or UserDataDirectoryKind.Data)
		{
			var platformFolder = kind == UserDataDirectoryKind.Configuration
				? Environment.SpecialFolder.ApplicationData
				: Environment.SpecialFolder.LocalApplicationData;
			var platformPath = specialFolderProvider(
				platformFolder,
				Environment.SpecialFolderOption.DoNotVerify);
			if (IsUsableAbsolutePath(platformPath))
				return Path.GetFullPath(platformPath);
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

		var relativePath = (isWindows, kind) switch
		{
			(true, UserDataDirectoryKind.Configuration) => Path.Combine("AppData", "Roaming"),
			(true, _) => Path.Combine("AppData", "Local"),
			(false, UserDataDirectoryKind.Configuration) => ".config",
			(false, UserDataDirectoryKind.Data) => Path.Combine(".local", "share"),
			(false, UserDataDirectoryKind.State) => Path.Combine(".local", "state"),
			(false, UserDataDirectoryKind.Cache) => ".cache",
			_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
		};
		return Path.GetFullPath(Path.Combine(home!, relativePath));
	}

	internal static string ResolveLegacyLocalData(
		bool isWindows,
		Func<Environment.SpecialFolder, Environment.SpecialFolderOption, string> specialFolderProvider,
		Func<string, string?> environmentProvider)
	{
		ArgumentNullException.ThrowIfNull(specialFolderProvider);
		ArgumentNullException.ThrowIfNull(environmentProvider);

		// Keep the pre-XDG-cache lookup order frozen so existing installations
		// remain discoverable after the cache moved out of the data directory.
		var platformPath = specialFolderProvider(
			Environment.SpecialFolder.LocalApplicationData,
			Environment.SpecialFolderOption.DoNotVerify);
		if (IsUsableAbsolutePath(platformPath))
			return Path.GetFullPath(platformPath);

		if (!isWindows)
		{
			var xdgDataPath = environmentProvider("XDG_DATA_HOME");
			if (IsUsableAbsolutePath(xdgDataPath))
				return Path.GetFullPath(xdgDataPath!);
		}

		var home = specialFolderProvider(
			Environment.SpecialFolder.UserProfile,
			Environment.SpecialFolderOption.DoNotVerify);
		if (!IsUsableAbsolutePath(home))
			home = environmentProvider(isWindows ? "USERPROFILE" : "HOME");

		if (!IsUsableAbsolutePath(home))
			throw new InvalidOperationException("A safe absolute legacy data directory could not be resolved.");

		var relativePath = isWindows
			? Path.Combine("AppData", "Local")
			: Path.Combine(".local", "share");
		return Path.GetFullPath(Path.Combine(home!, relativePath));
	}

	private static string GetXdgVariable(UserDataDirectoryKind kind) =>
		kind switch
		{
			UserDataDirectoryKind.Configuration => "XDG_CONFIG_HOME",
			UserDataDirectoryKind.Data => "XDG_DATA_HOME",
			UserDataDirectoryKind.State => "XDG_STATE_HOME",
			UserDataDirectoryKind.Cache => "XDG_CACHE_HOME",
			_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
		};

	private static bool IsUsableAbsolutePath(string? path) =>
		!string.IsNullOrWhiteSpace(path) &&
		Path.IsPathFullyQualified(path);
}
