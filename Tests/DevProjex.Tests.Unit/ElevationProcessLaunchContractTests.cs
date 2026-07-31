using DevProjex.Infrastructure.Elevation;

namespace DevProjex.Tests.Unit;

public sealed class ElevationProcessLaunchContractTests
{
	[Fact]
	public void WindowsRelaunchPrefersGuiAppHostOverDotnetConsoleHost()
	{
		var startInfo = ElevationService.CreateRelaunchStartInfo(
			[@"C:\Projects\Sample"],
			@"C:\Program Files\dotnet\dotnet.exe",
			@"C:\DevProjex\DevProjex.dll",
			@"C:\DevProjex\DevProjex.exe");

		Assert.Equal(@"C:\DevProjex\DevProjex.exe", startInfo.FileName);
		Assert.True(startInfo.UseShellExecute);
		Assert.Equal("runas", startInfo.Verb);
		Assert.Equal(
			[@"C:\Projects\Sample"],
			startInfo.ArgumentList);
	}

	[Fact]
	public void DotnetElevationFallbackReplaysManagedEntryPoint()
	{
		var startInfo = ElevationService.CreateRelaunchStartInfo(
			["--language", "ru"],
			@"C:\Program Files\dotnet\dotnet.exe",
			@"C:\DevProjex\DevProjex.dll",
			appHostPath: null);

		Assert.Equal(@"C:\Program Files\dotnet\dotnet.exe", startInfo.FileName);
		Assert.Equal(
			[@"C:\DevProjex\DevProjex.dll", "--language", "ru"],
			startInfo.ArgumentList);
	}
}
