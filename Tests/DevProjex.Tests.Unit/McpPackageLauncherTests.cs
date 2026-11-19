using System.Diagnostics;
using Tomlyn;
using Tomlyn.Model;

namespace DevProjex.Tests.Unit;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class McpPackageLauncherTests
{
	public static TheoryData<string, string, string[]> PackageCases => new()
	{
		{ @"C:\Users\Person Name\npm-cache\_npx\abc123\node_modules\@devprojex\cli-win32-x64\bin\devprojex.exe", "cmd", ["/c", "npx", "-y", "devprojex"] },
		{ @"C:\Cache Юникод\_npx\def456\node_modules\@devprojex\cli-win32-arm64\bin\devprojex.exe", "cmd", ["/c", "npx", "-y", "devprojex"] },
		{ "/home/person/.npm/_npx/abc123/node_modules/@devprojex/cli-linux-x64/bin/devprojex", "npx", ["-y", "devprojex"] },
		{ "/home/person/cache with spaces/_npx/abc123/node_modules/@devprojex/cli-linux-arm64/bin/devprojex", "npx", ["-y", "devprojex"] },
		{ "/Users/person/.npm/_npx/abc123/node_modules/@devprojex/cli-darwin-x64/bin/devprojex", "npx", ["-y", "devprojex"] },
		{ "/Users/person/.npm/_npx/abc123/node_modules/@devprojex/cli-darwin-arm64/bin/devprojex", "npx", ["-y", "devprojex"] },
		{ @"C:\Users\Person Name\.nuget\packages\devprojex.win-x64\5.2.0\tools\any\win-x64\devprojex.exe", "cmd", ["/c", "dnx", "devprojex"] },
		{ @"C:\Users\person\.nuget\packages\devprojex.win-arm64\5.2.2-rc.1\tools\any\win-arm64\devprojex.exe", "cmd", ["/c", "dnx", "devprojex"] },
		{ "/home/person/.nuget/packages/devprojex.linux-x64/5.2.0/tools/any/linux-x64/devprojex", "dnx", ["devprojex"] },
		{ "/home/person/.nuget/packages/devprojex.linux-arm64/5.2.0/tools/any/linux-arm64/devprojex", "dnx", ["devprojex"] },
		{ "/Users/person/.nuget/packages/devprojex.osx-x64/5.2.0/tools/any/osx-x64/devprojex", "dnx", ["devprojex"] },
		{ "/Users/person/.nuget/packages/devprojex.osx-arm64/5.2.0/tools/any/osx-arm64/devprojex", "dnx", ["devprojex"] }
	};

	[Theory]
	[MemberData(nameof(PackageCases))]
	public void PackageFragmentsUseDurableLaunchersForEveryClientAndMode(
		string executable, string command, string[] prefix)
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(false, null));
		foreach (var mode in Enum.GetValues<McpConnectionMode>())
		{
			const string root = "/project with spaces/Юникод $value `text` 'quoted'";
			string[] expected = [.. prefix, "mcp", "--root", root, .. mode == McpConnectionMode.Live ? new[] { "--live" } : []];
			foreach (var client in new[] { McpConnectionClient.Json, McpConnectionClient.Cursor, McpConnectionClient.VsCode })
			{
				using var json = JsonDocument.Parse(McpConnectionFragmentGenerator.Generate(client, mode, executable, root));
				var server = json.RootElement.GetProperty(client == McpConnectionClient.VsCode ? "servers" : "mcpServers").GetProperty("devprojex");
				Assert.Equal(command, server.GetProperty("command").GetString());
				Assert.Equal(expected, server.GetProperty("args").EnumerateArray().Select(x => x.GetString()));
			}
			var toml = TomlSerializer.Deserialize<TomlTable>(McpConnectionFragmentGenerator.Generate(McpConnectionClient.Codex, mode, executable, root))!;
			var entry = (TomlTable)((TomlTable)toml["mcp_servers"])["devprojex"];
			Assert.Equal(command, entry["command"]);
			Assert.Equal(expected, ((TomlArray)entry["args"]).Cast<string>());
			var claude = McpConnectionFragmentGenerator.Generate(McpConnectionClient.ClaudeCode, mode, executable, root);
			Assert.Contains(" -- " + command + " " + string.Join(' ', prefix) + " mcp --root ", claude, StringComparison.Ordinal);
			Assert.DoesNotContain(executable, claude, StringComparison.Ordinal);
			foreach (var client in new[] { McpConnectionClient.Cursor, McpConnectionClient.VsCode })
			{
				using var json = JsonDocument.Parse(McpConnectionFragmentGenerator.GenerateProjectFile(client, mode, executable));
				var server = json.RootElement.GetProperty(client == McpConnectionClient.VsCode ? "servers" : "mcpServers").GetProperty("devprojex");
				Assert.Equal(command, server.GetProperty("command").GetString());
				Assert.Equal(expected.Select(x => x == root ? "${workspaceFolder}" : x), server.GetProperty("args").EnumerateArray().Select(x => x.GetString()));
			}
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ClaudePackageCommandsRoundTripThroughTheNativeShell(bool nuget)
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(false, null));
		var windows = OperatingSystem.IsWindows();
		var launcher = nuget ? "dnx" : "npx";
		var executable = PackageCases.First(row => (row.Data.Item2 == launcher || row.Data.Item3.Contains(launcher)) &&
			row.Data.Item1.StartsWith(windows ? "C:" : "/", StringComparison.Ordinal)).Data.Item1;
		var root = windows ? "C:\\Project with spaces\\Юникод ! $HOME `text` 'quote'" : "/project with spaces/Юникод ! $HOME `text` 'quote' \"double\" \\slash";
		var fragment = McpConnectionFragmentGenerator.Generate(McpConnectionClient.ClaudeCode, McpConnectionMode.Live, executable, root);
		var start = new ProcessStartInfo(windows ? "pwsh" : "/bin/sh")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = new UTF8Encoding(false),
			StandardErrorEncoding = new UTF8Encoding(false)
		};
		string script;
		if (windows)
		{
			script = "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); " +
				"Remove-Item Alias:cd -ErrorAction SilentlyContinue; function cd { param($Path) }; " +
				"function claude { [Console]::Out.Write((ConvertTo-Json -Compress -InputObject @($args))) }; " +
				"Invoke-Expression $env:DPX_PACKAGE_FRAGMENT";
			foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script })
				start.ArgumentList.Add(argument);
		}
		else
		{
			script = "cd() { :; }; claude() { printf '%s\\0' \"$@\"; }; eval \"$DPX_PACKAGE_FRAGMENT\"";
			start.ArgumentList.Add("-c");
			start.ArgumentList.Add(script);
		}
		start.Environment["DPX_PACKAGE_FRAGMENT"] = fragment;
		using var process = Process.Start(start) ?? throw new InvalidOperationException("Shell did not start.");
		var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		deadline.CancelAfter(TimeSpan.FromSeconds(20));
		try
		{
			await process.WaitForExitAsync(deadline.Token);
		}
		finally
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
		}
		Assert.True(process.ExitCode == 0, await error);
		var actual = windows ? JsonSerializer.Deserialize<string[]>(await output)! : (await output).Split('\0', StringSplitOptions.RemoveEmptyEntries);
		string[] expected = ["mcp", "add", "--scope", "local", "devprojex", .. windows ? Array.Empty<string>() : ["--"],
			.. windows ? new[] { "cmd", "/c" } : [],
			launcher, .. nuget ? new[] { "devprojex" } : ["-y", "devprojex"], "mcp", "--root", root, "--live"];
		Assert.Equal(expected, actual);
	}
}
