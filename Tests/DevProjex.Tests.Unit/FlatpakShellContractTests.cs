using System.Diagnostics;
using DevProjex.Infrastructure.TerminalCommands;
using Tomlyn;
using Tomlyn.Model;

namespace DevProjex.Tests.Unit;

public sealed class FlatpakShellContractTests
{
	private const string AppId = "io.github.Avazbek22.DevProjex";
	private static readonly Lazy<Task<bool>> WindowsShellAvailable = new(ProbeWindowsShellAsync);

	public static IEnumerable<object[]> Paths() => new[]
	{
		"plain", "with space", "O'Brien", "double\"quote", "$HOME", "`uname`", "; echo injected",
		"a&b", "a|b", "line\nnext", "*", "-leading", "проект/日本語", new string('x', 4096)
	}.Select(static value => new object[] { value });

	[Theory]
	[MemberData(nameof(Paths))]
	public async Task PosixShellPreservesOverrideAndHostCommandArguments(string path)
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, AppId));
		Assert.Equal(new[] { path }, Assert.Single(await RunShellAsync("capture " + FlatpakEnvironment.QuoteArgument(path))));
		Assert.Equal(new[] { "flatpak", "override", "--user", "--filesystem=" + path, AppId },
			Assert.Single(await RunShellAsync(FlatpakEnvironment.OverrideCommand(path))));
		var root = "/home/person/" + path;
		foreach (var client in new[] { McpConnectionClient.ClaudeCode, McpConnectionClient.Codex })
		{
			var command = McpConnectionFragmentGenerator.GenerateHostClientCommand(new(client,
				McpConnectionMode.Live, "/app/lib/devprojex/DevProjex", root));
			var calls = await RunShellAsync(command);
			Assert.Equal(new[] { "cd", root }, calls[0]);
			var expected = client == McpConnectionClient.ClaudeCode
				? new[] { "claude", "mcp", "add", "--scope", "local", "devprojex", "--", "flatpak", "run", AppId, "mcp", "--root", root, "--live" }
				: new[] { "codex", "mcp", "add", "devprojex", "--", "flatpak", "run", AppId, "mcp", "--root", root, "--live" };
			Assert.Equal(expected, calls[1]);
			Assert.Equal(2, calls.Count);
			if (client == McpConnectionClient.ClaudeCode)
			{
				calls = await RunShellAsync(McpConnectionFragmentGenerator.Generate(client,
					McpConnectionMode.Live, "/app/DevProjex", root));
				Assert.Equal(new[] { "cd", root }, calls[0]);
				Assert.Equal(expected, calls[1]);
				Assert.Equal(2, calls.Count);
			}
		}
	}

	[Theory]
	[MemberData(nameof(Paths))]
	public void JsonAndTomlRoundTripEveryRootCharacter(string path)
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, AppId));
		var root = "/home/person/" + path;
		var expected = new[] { "run", AppId, "mcp", "--root", root, "--live" };
		using var json = JsonDocument.Parse(McpConnectionFragmentGenerator.Generate(McpConnectionClient.Json,
			McpConnectionMode.Live, "/app/DevProjex", root));
		Assert.Equal(expected, json.RootElement.GetProperty("mcpServers").GetProperty("devprojex")
			.GetProperty("args").EnumerateArray().Select(static value => value.GetString()));
		var toml = McpConnectionFragmentGenerator.Generate(McpConnectionClient.Codex, McpConnectionMode.Live, "/app/DevProjex", root);
		var model = Assert.IsType<TomlTable>(TomlSerializer.Deserialize<TomlTable>(toml));
		var server = Assert.IsType<TomlTable>(Assert.IsType<TomlTable>(model["mcp_servers"])["devprojex"]);
		Assert.Equal(expected, Assert.IsType<TomlArray>(server["args"]).Select(static value => Assert.IsType<string>(value)));
	}

	[Theory]
	[MemberData(nameof(Paths))]
	public async Task ShimForwardsExactlyTheOriginalArguments(string argument)
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, AppId));
		var script = TerminalCommandSetupService.BuildWrapperContent("/app/" + argument);
		var calls = await RunShellAsync("set -- " + FlatpakEnvironment.QuoteArgument(argument) + "\n" +
			script.Replace("exec flatpak ", "flatpak ", StringComparison.Ordinal));
		Assert.Equal(new[] { "flatpak", "run", AppId, argument }, Assert.Single(calls));
	}

	[Theory]
	[MemberData(nameof(Paths))]
	public async Task ClipboardCommandsUsePosixSyntaxEvenWithASubstitutedEnvironment(string path)
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, AppId));
		var root = OperatingSystem.IsWindows() ? "C:\\project\\" + path : "/home/person/" + path;
		var service = new McpClientLaunchService(new(new JsonLocalizationCatalog(), AppLanguage.En));
		foreach (var client in new[] { McpConnectionClient.ClaudeCode, McpConnectionClient.Codex, McpConnectionClient.VsCode })
		{
			var result = await service.OpenAsync(new(client, root), TestContext.Current.CancellationToken);
			Assert.Equal(McpClientLaunchStatus.CopyCommand, result.Status);
			var calls = await RunShellAsync(result.ManualCommand!);
			if (client == McpConnectionClient.VsCode)
				Assert.Equal(new[] { "code", root }, Assert.Single(calls));
			else
			{
				Assert.Equal(new[] { "cd", root }, calls[0]);
				Assert.Equal(new[] { client == McpConnectionClient.ClaudeCode ? "claude" : "codex" }, calls[1]);
				Assert.Equal(2, calls.Count);
			}
		}
	}

	private static async Task<IReadOnlyList<string[]>> RunShellAsync(string command)
	{
		if (OperatingSystem.IsWindows() && !await WindowsShellAvailable.Value)
			Assert.Skip("WSL Ubuntu with sh and python3 is unavailable.");
		var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "wsl.exe" : "sh")
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			StandardInputEncoding = new UTF8Encoding(false)
		};
		if (OperatingSystem.IsWindows())
		{
			start.ArgumentList.Add("-d");
			start.ArgumentList.Add("Ubuntu");
			start.ArgumentList.Add("--");
			start.ArgumentList.Add("sh");
		}
		using var process = Process.Start(start);
		Assert.NotNull(process);
		var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		var script = "capture() { python3 -c 'import sys,json; print(json.dumps(sys.argv[1:]))' \"$@\"; }\n" +
			"cd() { capture cd \"$@\"; }\nflatpak() { capture flatpak \"$@\"; }\n" +
			"claude() { capture claude \"$@\"; }\ncodex() { capture codex \"$@\"; }\ncode() { capture code \"$@\"; }\n" + command + "\n";
		await process.StandardInput.WriteAsync(script.AsMemory(), TestContext.Current.CancellationToken);
		process.StandardInput.Close();
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(20));
		try
		{
			await process.WaitForExitAsync(timeout.Token);
		}
		catch (OperationCanceledException)
		{
			process.Kill(entireProcessTree: true);
			throw;
		}
		var error = await stderr;
		if (OperatingSystem.IsWindows() && process.ExitCode != 0 && error.Contains("WSL", StringComparison.OrdinalIgnoreCase))
			Assert.Skip("WSL Ubuntu with sh and python3 is unavailable: " + error);
		Assert.Equal(0, process.ExitCode);
		Assert.Equal(string.Empty, error);
		return (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries)
			.Select(static line => JsonSerializer.Deserialize<string[]>(line)!).ToArray();
	}

	private static async Task<bool> ProbeWindowsShellAsync()
	{
		try
		{
			var start = new ProcessStartInfo("wsl.exe")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			foreach (var argument in new[] { "-d", "Ubuntu", "--", "sh", "-c", "command -v python3" })
				start.ArgumentList.Add(argument);
			using var process = Process.Start(start)!;
			var output = process.StandardOutput.ReadToEndAsync();
			var error = process.StandardError.ReadToEndAsync();
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
			try { await process.WaitForExitAsync(timeout.Token); }
			catch (OperationCanceledException) { process.Kill(entireProcessTree: true); return false; }
			await Task.WhenAll(output, error);
			return process.ExitCode == 0;
		}
		catch (System.ComponentModel.Win32Exception) { return false; }
	}
}
