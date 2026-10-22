namespace DevProjex.Tests.Unit;

public sealed class FlatpakEnvironmentTests
{
	[Theory]
	[InlineData(true, "io.github.Avazbek22.DevProjex", true, true)]
	[InlineData(true, "io.github.Avazbek22.DevProjex", false, false)]
	[InlineData(true, null, true, false)]
	[InlineData(true, null, false, false)]
	[InlineData(true, "", true, false)]
	[InlineData(true, " ", true, false)]
	[InlineData(false, "io.github.Avazbek22.DevProjex", true, false)]
	[InlineData(false, "io.github.Avazbek22.DevProjex", false, false)]
	[InlineData(false, null, true, false)]
	[InlineData(false, null, false, false)]
	public void RequiresLinuxAndBothMarkers(bool linux, string? id, bool marker, bool expected)
	{
		var result = FlatpakEnvironment.Detect(linux, name => name == "FLATPAK_ID" ? id : null,
			path => path == "/.flatpak-info" && marker);
		Assert.Equal(expected, result.IsActive);
		Assert.Equal(expected ? id : null, result.AppId);
		Assert.Equal(expected ? "flatpak run io.github.Avazbek22.DevProjex" : null, result.ExternalLaunchCommand);
	}

	[Fact]
	public void NonLinuxDoesNotProbeEnvironmentOrFilesystem()
	{
		Assert.False(FlatpakEnvironment.Detect(false, _ => throw new InvalidOperationException(),
			_ => throw new InvalidOperationException()).IsActive);
	}

	[Theory]
	[InlineData("io.github.Avazbek22.DevProjex", "/app/lib/devprojex/DevProjex", true)]
	[InlineData("io.github.Avazbek22.DevProjex.Devel", "/app/lib/devprojex/DevProjex", true)]
	// An editor installed from Flathub runs DevProjex from npm or a release archive inside its own sandbox.
	[InlineData("com.visualstudio.code", "/home/person/.npm/_npx/abc/node_modules/@devprojex/cli-linux-x64/DevProjex", false)]
	[InlineData("com.visualstudio.code", "/home/person/Downloads/DevProjex/DevProjex", false)]
	[InlineData("com.visualstudio.code", "/tmp/.mount_DevProjexAbc/usr/bin/DevProjex", false)]
	[InlineData("io.github.Avazbek22.DevProjex", "/usr/lib/dotnet/dotnet", false)]
	[InlineData("io.github.Avazbek22.DevProjex", "/application/DevProjex", false)]
	[InlineData("io.github.Avazbek22.DevProjex", "/app", false)]
	[InlineData("io.github.Avazbek22.DevProjex", "/app/../home/person/DevProjex", false)]
	[InlineData("io.github.Avazbek22.DevProjex", "/App/lib/devprojex/DevProjex", false)]
	[InlineData("io.github.Avazbek22.DevProjex", null, false)]
	public void OnlyABinaryInsideTheApplicationPrefixIsTheDevProjexFlatpak(string id, string? processPath, bool expected)
	{
		var result = FlatpakEnvironment.Detect(true, name => name == "FLATPAK_ID" ? id : null,
			path => path == "/.flatpak-info", processPath);

		Assert.Equal(expected, result.IsActive);
		Assert.Equal(expected ? id : null, result.AppId);
		Assert.Equal(expected ? "flatpak run " + id : null, result.ExternalLaunchCommand);
	}

	[Theory]
	[InlineData(false, "io.github.Avazbek22.DevProjex", true)]
	[InlineData(true, null, true)]
	[InlineData(true, "io.github.Avazbek22.DevProjex", false)]
	public void TheApplicationPrefixAloneIsNotASandbox(bool linux, string? id, bool marker)
	{
		var result = FlatpakEnvironment.Detect(linux, name => name == "FLATPAK_ID" ? id : null,
			path => path == "/.flatpak-info" && marker, "/app/lib/devprojex/DevProjex");

		Assert.False(result.IsActive);
		Assert.Null(result.ExternalLaunchCommand);
	}

	[Fact]
	public void TheTestHostIsNeverDetectedAsTheDevProjexFlatpak()
	{
		Assert.False(FlatpakEnvironment.Current.IsActive);
		Assert.Null(FlatpakEnvironment.Current.ExternalLaunchCommand);
	}

	[Fact]
	public void ShellArgumentsCannotInjectACommand()
	{
		Assert.Equal("'/mnt/O'\"'\"'Brien; x'", FlatpakEnvironment.QuoteArgument("/mnt/O'Brien; x"));
	}
}
