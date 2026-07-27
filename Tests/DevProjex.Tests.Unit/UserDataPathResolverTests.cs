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
	public void UnixConfigurationUsesAbsoluteXdgPathWhenSpecialFolderIsUnavailable()
	{
		var xdg = Path.Combine(Path.GetTempPath(), "xdg-config");

		var actual = UserDataPathResolver.Resolve(
			Environment.SpecialFolder.ApplicationData,
			isWindows: false,
			static (_, _) => string.Empty,
			name => name == "XDG_CONFIG_HOME" ? xdg : null);

		Assert.Equal(Path.GetFullPath(xdg), actual);
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
