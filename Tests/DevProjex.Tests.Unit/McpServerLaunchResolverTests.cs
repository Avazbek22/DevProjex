namespace DevProjex.Tests.Unit;

public sealed class McpServerLaunchResolverTests
{
	[Theory]
	[InlineData("/opt/devprojex")]
	[InlineData(@"C:\Program Files\DevProjex\DevProjex.exe")]
	[InlineData(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\devprojex.exe")]
	[InlineData("/Applications/DevProjex.app/Contents/MacOS/DevProjex")]
	[InlineData("/home/me/DevProjex.AppImage")]
	[InlineData("/cache/_npx/hash/node_modules/@other/cli-linux-x64/bin/devprojex")]
	[InlineData("/cache/_npx/hash/node_modules/@devprojex/cli-linux-musl-x64/bin/devprojex")]
	[InlineData("/cache/_npx/hash/node_modules/@devprojex/cli-linux-x64/bin/not-devprojex")]
	[InlineData("/cache/_npx/hash/node_modules/@devprojex/cli-linux-x64/bin/devprojex.exe")]
	[InlineData("/cache/_npx/hash/node_modules/@devprojex/cli-linux-x64/bin/../bin/devprojex")]
	[InlineData("/cache/_npx/hash/node_modules/@devprojex/cli-linux-x64/bin/devprojex/extra")]
	[InlineData("/cache/node_modules/@devprojex/cli-linux-x64/bin/devprojex")]
	[InlineData("/cache/_NPX/hash/node_modules/@devprojex/cli-linux-x64/bin/devprojex")]
	[InlineData("/home/me/.nuget/packages/devprojex.linux-x64/5.2.0/tools/any/osx-x64/devprojex")]
	[InlineData("/home/me/.nuget/packages/devprojex.linux-x64/not-a-version/tools/any/linux-x64/devprojex")]
	[InlineData("/home/me/.nuget/packages/devprojex.linux-x64/5.2.0\n/tools/any/linux-x64/devprojex")]
	[InlineData("/home/me/.nuget/packages/devprojex.linux-x64/5.2.0/tools/net10.0/linux-x64/devprojex")]
	[InlineData("/home/me/.nuget/packages/other.linux-x64/5.2.0/tools/any/linux-x64/devprojex")]
	[InlineData("/home/me/.nuget/packages/devprojex.linux-x64/5.2.0/tools/any/linux-x64/devprojex.exe")]
	[InlineData("/copied-package/devprojex.linux-x64/5.2.0/tools/any/linux-x64/devprojex")]
	[InlineData("/home/me/.dotnet/tools/.store/devprojex/5.2.0/devprojex.linux-x64/5.2.0/tools/any/linux-x64/devprojex")]
	public void NativeArtifactsAndSimilarPathsKeepTheirExactCommand(string executable)
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(false, null));
		var launch = McpServerLaunchResolver.Resolve(executable, "unrelated inherited marker", null, _ => false);
		Assert.Equal(executable, launch.Command);
		Assert.Empty(launch.PrefixArguments);
	}

	[Theory]
	[InlineData("/global/node_modules/@devprojex/cli-linux-x64/bin/devprojex", true)]
	[InlineData("/global/node_modules/@devprojex/cli-linux-x64/bin/devprojex", false)]
	[InlineData(@"C:\Global Tools\node_modules\@devprojex\cli-win32-x64\bin\devprojex.exe", true)]
	public void NpmMarkerMustNameThisExactPackageExecutable(string executable, bool matches)
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(false, null));
		var launch = McpServerLaunchResolver.Resolve(executable, matches ? executable : executable + "-old", null, _ => false);
		Assert.Equal(matches ? "npx" : executable, launch.Command);
		Assert.Equal(matches ? new[] { "-y", "devprojex" } : [], launch.PrefixArguments);
	}

	[Theory]
	[InlineData("/custom cache/Юникод", "linux-x64", "devprojex")]
	[InlineData(@"D:\Custom Cache\Юникод", "win-arm64", "devprojex.exe")]
	public void CustomNuGetCacheIsRecognizedByConfiguredRootOrRestoreMetadata(string cache, string rid, string binary)
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(false, null));
		var executable = cache + "/devprojex." + rid + "/5.2.2/tools/any/" + rid + "/" + binary;
		var configured = McpServerLaunchResolver.Resolve(executable, null, cache, _ => false);
		Assert.Equal("dnx", configured.Command);
		var checkedPaths = new List<string>();
		var metadata = cache.Replace('\\', '/') + "/devprojex." + rid + "/5.2.2/.nupkg.metadata";
		var restored = McpServerLaunchResolver.Resolve(executable, null, null, path =>
		{
			checkedPaths.Add(path);
			return path == metadata;
		});
		Assert.Equal("dnx", restored.Command);
		Assert.Equal(["devprojex"], restored.PrefixArguments);
		Assert.Equal([metadata], checkedPaths);
	}

	[Fact]
	public void WindowsCaseAndSeparatorsAreRecognizedOnEveryOperatingSystem()
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(false, null));
		Assert.Equal("npx", McpServerLaunchResolver.Resolve(
			@"C:\CACHE\_NPX\HASH\NODE_MODULES\@DEVPROJEX\CLI-WIN32-X64\BIN\DEVPROJEX.EXE", null, null, _ => false).Command);
		Assert.Equal("npx", McpServerLaunchResolver.Resolve(
			@"\\server\cache\_npx\hash\node_modules\@devprojex\cli-win32-arm64\bin\devprojex.exe", null, null, _ => false).Command);
	}

	[Fact]
	public void FlatpakTakesPrecedenceOverPackageMarkersAndCachePaths()
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, FlatpakEnvironment.DevProjexAppId));
		const string executable = "/cache/_npx/hash/node_modules/@devprojex/cli-linux-x64/bin/devprojex";
		var launch = McpServerLaunchResolver.Resolve(executable, executable, null, _ => throw new InvalidOperationException());
		Assert.Equal("flatpak", launch.Command);
		Assert.Equal(["run", FlatpakEnvironment.DevProjexAppId], launch.PrefixArguments);
	}

	[Fact]
	public void GlobalToolStoreRemainsNativeEvenWithAnInheritedMarkerAndRestoreMetadata()
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(false, null));
		const string executable = "/home/me/.dotnet/tools/.store/devprojex/5.2.0/devprojex.linux-x64/5.2.0/tools/any/linux-x64/devprojex";
		var launch = McpServerLaunchResolver.Resolve(executable, executable, null, _ => true);
		Assert.Equal(executable, launch.Command);
		Assert.Empty(launch.PrefixArguments);
	}
}
