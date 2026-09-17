using DevProjex.Application.Services;
using DevProjex.Infrastructure.TerminalCommands;

namespace DevProjex.Tests.Unit;

public sealed class McpConnectionFragmentGeneratorTests
{
	[Theory]
	[InlineData((int)McpConnectionClient.ClaudeCode, (int)McpConnectionMode.Live)]
	[InlineData((int)McpConnectionClient.ClaudeCode, (int)McpConnectionMode.Standard)]
	[InlineData((int)McpConnectionClient.Codex, (int)McpConnectionMode.Live)]
	[InlineData((int)McpConnectionClient.Codex, (int)McpConnectionMode.Standard)]
	[InlineData((int)McpConnectionClient.Json, (int)McpConnectionMode.Live)]
	[InlineData((int)McpConnectionClient.Json, (int)McpConnectionMode.Standard)]
	public void Generate_PreservesAbsoluteUnicodePathsAndMode(int clientValue, int modeValue)
	{
		const string executable = @"C:\Program Files\DevProjex\DevProjex.exe";
		const string root = @"C:\Проекты\Мой проект";
		var client = (McpConnectionClient)clientValue;
		var mode = (McpConnectionMode)modeValue;

		var fragment = McpConnectionFragmentGenerator.Generate(client, mode, executable, root);

		Assert.Contains("DevProjex", fragment, StringComparison.Ordinal);
		Assert.Contains("Проекты", fragment, StringComparison.Ordinal);
		Assert.Equal(mode == McpConnectionMode.Live, fragment.Contains("--live", StringComparison.Ordinal));
		if (client == McpConnectionClient.Codex)
		{
			Assert.Contains("C:\\\\Program Files\\\\DevProjex", fragment, StringComparison.Ordinal);
		}
		else if (client == McpConnectionClient.Json)
		{
			using var document = JsonDocument.Parse(fragment);
			var server = document.RootElement.GetProperty("mcpServers").GetProperty("devprojex");
			Assert.Equal(executable, server.GetProperty("command").GetString());
			Assert.Contains(root, server.GetProperty("args").EnumerateArray().Select(static value => value.GetString()));
		}
	}

	[Fact]
	public void Generate_UsesThePublishedClientFormats()
	{
		const string executable = "/Applications/DevProjex.app/Contents/MacOS/DevProjex";
		const string root = "/Users/me/My Project";

		Assert.Equal(
			"claude mcp add devprojex -- \"/Applications/DevProjex.app/Contents/MacOS/DevProjex\" mcp --root \"/Users/me/My Project\" --live",
			McpConnectionFragmentGenerator.Generate(
				McpConnectionClient.ClaudeCode,
				McpConnectionMode.Live,
				executable,
				root));
		Assert.Equal(
			"[mcp_servers.devprojex]" + Environment.NewLine +
			"command = \"/Applications/DevProjex.app/Contents/MacOS/DevProjex\"" + Environment.NewLine +
			"args = [\"mcp\", \"--root\", \"/Users/me/My Project\"]",
			McpConnectionFragmentGenerator.Generate(
				McpConnectionClient.Codex,
				McpConnectionMode.Standard,
				executable,
				root));
	}

	[Theory]
	[InlineData("DevProjex.exe", "/project")]
	[InlineData("/opt/DevProjex", "project")]
	public void Generate_RejectsRelativePaths(string executable, string root)
	{
		Assert.Throws<ArgumentException>(() => McpConnectionFragmentGenerator.Generate(
			McpConnectionClient.Json,
			McpConnectionMode.Live,
			executable,
			root));
	}

	[Fact]
	public void Resolve_UsesTheWindowsStoreExecutionAlias()
	{
		var snapshot = Snapshot(
			TerminalCommandSetupState.ManagedByOperatingSystem,
			targetExecutablePath: null,
			CommandLineExecutableAliases.WindowsStoreAlias);

		var path = McpConnectionExecutablePathResolver.Resolve(
			snapshot,
			TerminalCommandHostPlatform.Windows,
			@"C:\Users\me\AppData\Local");

		Assert.Equal(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\devprojex.exe", path);
	}

	[Theory]
	[InlineData((int)TerminalCommandHostPlatform.Windows, @"D:\Tools\DevProjex.exe")]
	[InlineData((int)TerminalCommandHostPlatform.Linux, "/home/me/Apps/DevProjex.AppImage")]
	[InlineData((int)TerminalCommandHostPlatform.MacOS, "/Applications/DevProjex.app/Contents/MacOS/DevProjex")]
	public void Resolve_PreservesTheInstalledArtifactPath(int platformValue, string targetPath)
	{
		var snapshot = Snapshot(TerminalCommandSetupState.Installed, targetPath);

		Assert.Equal(
			targetPath,
			McpConnectionExecutablePathResolver.Resolve(
				snapshot,
				(TerminalCommandHostPlatform)platformValue));
	}

	private static TerminalCommandSetupSnapshot Snapshot(
		TerminalCommandSetupState state,
		string? targetExecutablePath,
		string commandName = "devprojex") =>
		new(
			commandName,
			state,
			CommandPath: null,
			targetExecutablePath,
			InstalledTargetExecutablePath: null,
			UserBinDirectory: null,
			UserBinDirectoryIsInPath: state == TerminalCommandSetupState.ManagedByOperatingSystem,
			CanInstall: false,
			CanRepair: false,
			ShellProfileHint: null);
}
