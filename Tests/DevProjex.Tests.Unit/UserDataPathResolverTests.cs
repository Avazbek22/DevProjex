using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Tests.Unit;

public sealed class UserDataPathResolverTests
{
	[Fact]
	public void ExistingPlatformPathIsUsedWithoutDirectoryVerification()
	{
		var expected = Path.Combine(Path.GetTempPath(), "dpx-config");
		Environment.SpecialFolderOption? observedOption = null;

		var actual = UserDataPathResolver.Resolve(
			Environment.SpecialFolder.ApplicationData,
			isWindows: false,
			(folder, option) =>
			{
				observedOption = option;
				return folder == Environment.SpecialFolder.ApplicationData ? expected : string.Empty;
			},
			_ => null);

		Assert.Equal(Path.GetFullPath(expected), actual);
		Assert.Equal(Environment.SpecialFolderOption.DoNotVerify, observedOption);
	}

	[Fact]
	public void UnixConfigurationPrefersExplicitAbsoluteXdgPath()
	{
		var xdg = Path.Combine(Path.GetTempPath(), "xdg-config");
		var platformDefault = Path.Combine(Path.GetTempPath(), "platform-config");

		var actual = UserDataPathResolver.Resolve(
			Environment.SpecialFolder.ApplicationData,
			isWindows: false,
			(folder, _) => folder == Environment.SpecialFolder.ApplicationData
				? platformDefault
				: string.Empty,
			name => name == "XDG_CONFIG_HOME" ? xdg : null);

		Assert.Equal(Path.GetFullPath(xdg), actual);
	}

	[Theory]
	[InlineData(0, "XDG_CONFIG_HOME", ".config")]
	[InlineData(1, "XDG_DATA_HOME", ".local/share")]
	[InlineData(2, "XDG_STATE_HOME", ".local/state")]
	[InlineData(3, "XDG_CACHE_HOME", ".cache")]
	public void UnixDirectoryKindsHonorExplicitXdgRoots(
		int kindValue,
		string variable,
		string _)
	{
		var kind = (UserDataDirectoryKind)kindValue;
		var xdgRoot = Path.Combine(Path.GetTempPath(), variable.ToLowerInvariant());
		var platformDefault = Path.Combine(Path.GetTempPath(), "platform-default");

		var actual = UserDataPathResolver.Resolve(
			kind,
			isWindows: false,
			(_, _) => platformDefault,
			name => name == variable ? xdgRoot : null);

		Assert.Equal(Path.GetFullPath(xdgRoot), actual);
	}

	[Theory]
	[InlineData(0, ".config")]
	[InlineData(1, ".local/share")]
	[InlineData(2, ".local/state")]
	[InlineData(3, ".cache")]
	public void EmptyXdgVariableFallsBackToHome(
		int kindValue,
		string relativeFallback)
	{
		var kind = (UserDataDirectoryKind)kindValue;
		var home = Path.Combine(Path.GetTempPath(), "dpx-home");

		var actual = UserDataPathResolver.Resolve(
			kind,
			isWindows: false,
			(folder, _) => folder == Environment.SpecialFolder.UserProfile ? home : string.Empty,
			static _ => string.Empty);

		Assert.Equal(
			Path.GetFullPath(Path.Combine(home, relativeFallback.Replace('/', Path.DirectorySeparatorChar))),
			actual);
	}

	[Theory]
	[InlineData(2, ".local/state")]
	[InlineData(3, ".cache")]
	public void UnixStateAndCacheDoNotReuseLocalApplicationData(
		int kindValue,
		string relativeFallback)
	{
		var kind = (UserDataDirectoryKind)kindValue;
		var home = Path.Combine(Path.GetTempPath(), "dpx-home");
		var localData = Path.Combine(Path.GetTempPath(), "platform-local-data");

		var actual = UserDataPathResolver.Resolve(
			kind,
			isWindows: false,
			(folder, _) => folder switch
			{
				Environment.SpecialFolder.LocalApplicationData => localData,
				Environment.SpecialFolder.UserProfile => home,
				_ => string.Empty
			},
			static _ => null);

		Assert.Equal(
			Path.GetFullPath(Path.Combine(home, relativeFallback.Replace('/', Path.DirectorySeparatorChar))),
			actual);
	}

	[Fact]
	public void UnixRelativeXdgPathFallsBackToAbsoluteHome()
	{
		var home = Path.Combine(Path.GetTempPath(), "dpx-home");

		var actual = UserDataPathResolver.Resolve(
			Environment.SpecialFolder.ApplicationData,
			isWindows: false,
			(folder, _) => folder == Environment.SpecialFolder.UserProfile ? home : string.Empty,
			name => name == "XDG_CONFIG_HOME" ? "relative-config" : null);

		Assert.Equal(Path.GetFullPath(Path.Combine(home, ".config")), actual);
	}

	[Fact]
	public void UnixLocalDataFallsBackToHomeLocalShare()
	{
		var home = Path.Combine(Path.GetTempPath(), "dpx-home");

		var actual = UserDataPathResolver.Resolve(
			Environment.SpecialFolder.LocalApplicationData,
			isWindows: false,
			(folder, _) => folder == Environment.SpecialFolder.UserProfile ? home : string.Empty,
			_ => null);

		Assert.Equal(Path.GetFullPath(Path.Combine(home, ".local", "share")), actual);
	}

	[Fact]
	public void LegacyUnixLocalDataPreservesOriginalPlatformFirstResolution()
	{
		var platformData = Path.Combine(Path.GetTempPath(), "legacy-platform-data");
		var xdgData = Path.Combine(Path.GetTempPath(), "legacy-xdg-data");

		var actual = UserDataPathResolver.ResolveLegacyLocalData(
			isWindows: false,
			(folder, _) => folder == Environment.SpecialFolder.LocalApplicationData
				? platformData
				: string.Empty,
			name => name == "XDG_DATA_HOME" ? xdgData : null);

		Assert.Equal(Path.GetFullPath(platformData), actual);
	}

	[Fact]
	public void LegacyUnixLocalDataUsesXdgWhenPlatformFolderIsUnavailable()
	{
		var xdgData = Path.Combine(Path.GetTempPath(), "legacy-xdg-data");

		var actual = UserDataPathResolver.ResolveLegacyLocalData(
			isWindows: false,
			static (_, _) => string.Empty,
			name => name == "XDG_DATA_HOME" ? xdgData : null);

		Assert.Equal(Path.GetFullPath(xdgData), actual);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void LegacyUnixLocalDataTreatsUnsetAndEmptyXdgAsHomeFallback(
		string? xdgData)
	{
		var home = Path.Combine(Path.GetTempPath(), "legacy-home");

		var actual = UserDataPathResolver.ResolveLegacyLocalData(
			isWindows: false,
			(folder, _) => folder == Environment.SpecialFolder.UserProfile
				? home
				: string.Empty,
			name => name == "XDG_DATA_HOME" ? xdgData : null);

		Assert.Equal(
			Path.GetFullPath(Path.Combine(home, ".local", "share")),
			actual);
	}

	[Fact]
	public void LegacyLocalDataFailsWhenNoAbsoluteRootCanBeResolved()
	{
		Assert.Throws<InvalidOperationException>(() =>
			UserDataPathResolver.ResolveLegacyLocalData(
				isWindows: false,
				static (_, _) => string.Empty,
				static _ => null));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("relative-home")]
	public void CacheAndLegacyResolversRejectMissingOrUnsafeHome(
		string? home)
	{
		string? EnvironmentProvider(string name) =>
			name == "HOME" ? home : null;

		Assert.Throws<InvalidOperationException>(() =>
			UserDataPathResolver.Resolve(
				UserDataDirectoryKind.Cache,
				isWindows: false,
				static (_, _) => string.Empty,
				EnvironmentProvider));
		Assert.Throws<InvalidOperationException>(() =>
			UserDataPathResolver.ResolveLegacyLocalData(
				isWindows: false,
				static (_, _) => string.Empty,
				EnvironmentProvider));
	}

	[Theory]
	[InlineData(Environment.SpecialFolder.ApplicationData, "Roaming")]
	[InlineData(Environment.SpecialFolder.LocalApplicationData, "Local")]
	public void WindowsFallbackRemainsInsideUserProfile(
		Environment.SpecialFolder folder,
		string leaf)
	{
		var home = Path.Combine(Path.GetTempPath(), "dpx-user");

		var actual = UserDataPathResolver.Resolve(
			folder,
			isWindows: true,
			(candidate, _) => candidate == Environment.SpecialFolder.UserProfile ? home : string.Empty,
			_ => null);

		Assert.Equal(
			Path.GetFullPath(Path.Combine(home, "AppData", leaf)),
			actual);
	}

	[Fact]
	public void MissingUserDataRootsFailInsteadOfUsingCurrentDirectory()
	{
		Assert.Throws<InvalidOperationException>(() =>
			UserDataPathResolver.Resolve(
				Environment.SpecialFolder.ApplicationData,
				isWindows: false,
				static (_, _) => string.Empty,
				static _ => null));
	}
}
