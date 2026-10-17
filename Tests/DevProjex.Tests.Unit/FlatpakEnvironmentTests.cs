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

	[Fact]
	public void ShellArgumentsCannotInjectACommand()
	{
		Assert.Equal("'/mnt/O'\"'\"'Brien; x'", FlatpakEnvironment.QuoteArgument("/mnt/O'Brien; x"));
	}
}
