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
		var windows = executable.StartsWith(@"C:\", StringComparison.Ordinal);
		Assert.Equal(matches ? windows ? "cmd" : "npx" : executable, launch.Command);
		Assert.Equal(
			matches ? windows ? new[] { "/c", "npx", "-y", "devprojex" } : ["-y", "devprojex"] : [],
			launch.PrefixArguments);
	}

	[Theory]
	[InlineData("/custom cache/Юникод", "linux-x64", "devprojex")]
	[InlineData(@"D:\Custom Cache\Юникод", "win-arm64", "devprojex.exe")]
	public void CustomNuGetCacheIsRecognizedByConfiguredRootOrRestoreMetadata(string cache, string rid, string binary)
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(false, null));
		var executable = cache + "/devprojex." + rid + "/5.2.2/tools/any/" + rid + "/" + binary;
		var windows = rid.StartsWith("win-", StringComparison.Ordinal);
		string[] expectedPrefix = windows ? ["/c", "dnx", "devprojex"] : ["devprojex"];
		var configured = McpServerLaunchResolver.Resolve(executable, null, cache, _ => false);
		Assert.Equal(windows ? "cmd" : "dnx", configured.Command);
		Assert.Equal(expectedPrefix, configured.PrefixArguments);
		var checkedPaths = new List<string>();
		var metadata = cache.Replace('\\', '/') + "/devprojex." + rid + "/5.2.2/.nupkg.metadata";
		var restored = McpServerLaunchResolver.Resolve(executable, null, null, path =>
		{
			checkedPaths.Add(path);
			return path == metadata;
		});
		Assert.Equal(windows ? "cmd" : "dnx", restored.Command);
		Assert.Equal(expectedPrefix, restored.PrefixArguments);
		Assert.Equal([metadata], checkedPaths);
	}

	[Fact]
	public void WindowsCaseAndSeparatorsAreRecognizedOnEveryOperatingSystem()
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(false, null));
		var upperCase = McpServerLaunchResolver.Resolve(
			@"C:\CACHE\_NPX\HASH\NODE_MODULES\@DEVPROJEX\CLI-WIN32-X64\BIN\DEVPROJEX.EXE", null, null, _ => false);
		var share = McpServerLaunchResolver.Resolve(
			@"\\server\cache\_npx\hash\node_modules\@devprojex\cli-win32-arm64\bin\devprojex.exe", null, null, _ => false);
		Assert.Equal("cmd", upperCase.Command);
		Assert.Equal(["/c", "npx", "-y", "devprojex"], upperCase.PrefixArguments);
		Assert.Equal("cmd", share.Command);
		Assert.Equal(["/c", "npx", "-y", "devprojex"], share.PrefixArguments);
	}

	[Theory]
	[InlineData(@"C:\Users\me\AppData\Local\npm-cache\_npx\a1\node_modules\@devprojex\cli-win32-x64\bin\devprojex.exe",
		new[] { "/c", "npx", "-y", "devprojex" })]
	[InlineData(@"C:\Users\me\.nuget\packages\devprojex.win-x64\5.2.2\tools\any\win-x64\devprojex.exe",
		new[] { "/c", "dnx", "devprojex" })]
	public void WindowsPackageLaunchersGoThroughCmdOnEveryOperatingSystem(string executable, string[] prefix)
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(false, null));
		var launch = McpServerLaunchResolver.Resolve(
			executable, null, null, _ => false, projectRoot: @"C:\Projects\Sample App (1)");

		Assert.Equal("cmd", launch.Command);
		Assert.Equal(prefix, launch.PrefixArguments);
		Assert.True(launch.IsPackageLauncher);
	}

	[Theory]
	[InlineData(@"C:\Projects\R&D")]
	[InlineData(@"C:\Projects\100%")]
	[InlineData(@"C:\Projects\%PATH%")]
	[InlineData(@"C:\Projects\a^b")]
	public void WindowsRootThatCmdWouldRewriteKeepsTheInstalledExecutable(string root)
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(false, null));
		const string executable =
			@"C:\Users\me\AppData\Local\npm-cache\_npx\a1\node_modules\@devprojex\cli-win32-x64\bin\devprojex.exe";
		var launch = McpServerLaunchResolver.Resolve(executable, null, null, _ => false, projectRoot: root);

		Assert.Equal(executable, launch.Command);
		Assert.Empty(launch.PrefixArguments);
		Assert.False(launch.IsPackageLauncher);
	}

	[Theory]
	[InlineData(McpConnectionClient.Json)]
	[InlineData(McpConnectionClient.Cursor)]
	public void WindowsNpxFragmentStartsThroughCmd(McpConnectionClient client)
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(false, null));
		const string executable =
			@"C:\Users\me\AppData\Local\npm-cache\_npx\a1\node_modules\@devprojex\cli-win32-x64\bin\devprojex.exe";
		const string root = @"C:\Projects\Sample App";

		using var fragment = System.Text.Json.JsonDocument.Parse(
			McpConnectionFragmentGenerator.Generate(client, McpConnectionMode.Live, executable, root));
		var server = fragment.RootElement.GetProperty("mcpServers").GetProperty("devprojex");

		Assert.Equal("cmd", server.GetProperty("command").GetString());
		Assert.Equal(
			["/c", "npx", "-y", "devprojex", "mcp", "--root", root, "--live"],
			server.GetProperty("args").EnumerateArray().Select(static item => item.GetString()!));
	}

	[Fact]
	public void WindowsClaudeCodeCommandStartsThroughCmd()
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(false, null));
		const string executable =
			@"C:\Users\me\AppData\Local\npm-cache\_npx\a1\node_modules\@devprojex\cli-win32-x64\bin\devprojex.exe";

		var command = McpConnectionFragmentGenerator.Generate(
			McpConnectionClient.ClaudeCode, McpConnectionMode.Standard, executable, @"C:\Projects\App");

		Assert.EndsWith(
			"claude mcp add --scope local devprojex -- cmd /c npx -y devprojex mcp --root \"C:\\Projects\\App\"",
			command,
			StringComparison.Ordinal);
	}

	[Fact]
	public void PosixPackageLaunchersStayDirectWhateverTheRootContains()
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(false, null));
		var launch = McpServerLaunchResolver.Resolve(
			"/home/me/.npm/_npx/a1/node_modules/@devprojex/cli-linux-x64/bin/devprojex", null, null, _ => false,
			projectRoot: "/home/me/R&D %PATH%");

		Assert.Equal("npx", launch.Command);
		Assert.Equal(["-y", "devprojex"], launch.PrefixArguments);
		Assert.True(launch.IsPackageLauncher);
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

	[Theory]
	[InlineData(false, true)]
	[InlineData(true, true)]
	[InlineData(false, false)]
	[InlineData(true, false)]
	public void PhysicalAliasesRequireMatchingFileSystemIdentity(bool nuget, bool identityMatches)
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(false, null));
		const string physicalCache = "/private/var/cache";
		const string aliasCache = "/var/cache";
		const string npmTail = "/node_modules/@devprojex/cli-darwin-arm64/bin/devprojex";
		const string nugetTail = "/devprojex.osx-arm64/5.2.2/tools/any/osx-arm64/devprojex";
		var executable = physicalCache + (nuget ? nugetTail : npmTail);
		var expectedLeft = nuget ? physicalCache : executable;
		var expectedRight = nuget ? aliasCache : aliasCache + npmTail;
		var comparisons = new List<(string, string)>();
		var launch = McpServerLaunchResolver.Resolve(executable, nuget ? null : expectedRight,
			nuget ? aliasCache : null, _ => false, (left, right) =>
			{
				comparisons.Add((left, right));
				return identityMatches;
			});
		Assert.Equal(identityMatches ? nuget ? "dnx" : "npx" : executable, launch.Command);
		Assert.Equal([(expectedLeft, expectedRight)], comparisons);
	}

	[Fact]
	public void LexicallyMatchingProvenanceDoesNotNeedFileSystemIdentity()
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(false, null));
		const string executable = "/var/cache/node_modules/@devprojex/cli-darwin-arm64/bin/devprojex";
		var launch = McpServerLaunchResolver.Resolve(executable, executable, null,
			_ => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException());
		Assert.Equal("npx", launch.Command);
	}
}
