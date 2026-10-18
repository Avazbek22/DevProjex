using System.Diagnostics;
using DevProjex.Infrastructure.TerminalCommands;
using Tomlyn;
using Tomlyn.Model;

namespace DevProjex.Tests.Unit;

public sealed class FlatpakGeneratedCommandTests
{
	private const int Seed = 4812026;
	private const string AppId = "io.github.Avazbek22.DevProjex";

	[Fact]
	public void ThousandsOfRootsRoundTripThroughJsonAndToml()
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, AppId));
		foreach (var path in Paths())
		{
			var root = "/home/person/" + path;
			var expected = new[] { "run", AppId, "mcp", "--root", root, "--live" };
			foreach (var client in new[] { McpConnectionClient.Json, McpConnectionClient.Cursor, McpConnectionClient.VsCode })
			{
				using var json = JsonDocument.Parse(McpConnectionFragmentGenerator.Generate(client, McpConnectionMode.Live, "/app/DevProjex", root));
				var server = json.RootElement.GetProperty(client == McpConnectionClient.VsCode ? "servers" : "mcpServers").GetProperty("devprojex");
				Assert.Equal("flatpak", server.GetProperty("command").GetString());
				Assert.True(expected.SequenceEqual(server.GetProperty("args").EnumerateArray().Select(value => value.GetString())), $"JSON seed={Seed}; root={JsonSerializer.Serialize(root)}");
			}
			var text = McpConnectionFragmentGenerator.Generate(McpConnectionClient.Codex, McpConnectionMode.Live, "/app/DevProjex", root);
			TomlTable? parsed = null;
			var exception = Record.Exception(() => parsed = TomlSerializer.Deserialize<TomlTable>(text));
			Assert.True(exception is null, $"TOML seed={Seed}; root={JsonSerializer.Serialize(root)}; {exception}");
			var entry = Assert.IsType<TomlTable>(Assert.IsType<TomlTable>(parsed!["mcp_servers"])["devprojex"]);
			Assert.Equal("flatpak", entry["command"]);
			Assert.True(expected.SequenceEqual(Assert.IsType<TomlArray>(entry["args"]).Select(value => Assert.IsType<string>(value))), $"TOML seed={Seed}; root={JsonSerializer.Serialize(root)}");
		}
	}

	[Fact]
	public async Task ThousandsOfCommandsPreserveArgumentsInARealPosixShell()
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, AppId));
		var script = new StringBuilder("capture() { printf '%s\\000' \"$#\" \"$@\"; }\ncd() { capture cd \"$@\"; }\nflatpak() { capture flatpak \"$@\"; }\nclaude() { capture claude \"$@\"; }\ncodex() { capture codex \"$@\"; }\ncode() { capture code \"$@\"; }\ncursor() { capture cursor \"$@\"; }\n");
		var expected = new List<string[]>();
		var launcher = new McpClientLaunchService(new(new JsonLocalizationCatalog(), AppLanguage.En));
		foreach (var path in Paths())
		{
			var root = "/home/person/" + path;
			Add("capture " + FlatpakEnvironment.QuoteArgument(path), [path]);
			Add(FlatpakEnvironment.OverrideCommand(root), ["flatpak", "override", "--user", "--filesystem=" + root, AppId]);
			foreach (var client in new[] { McpConnectionClient.ClaudeCode, McpConnectionClient.Codex })
			{
				var command = McpConnectionFragmentGenerator.GenerateHostClientCommand(new(client, McpConnectionMode.Live, "/app/DevProjex", root));
				script.Append(command).Append('\n');
				expected.Add(["cd", root]);
				expected.Add(client == McpConnectionClient.ClaudeCode
					? ["claude", "mcp", "add", "--scope", "local", "devprojex", "--", "flatpak", "run", AppId, "mcp", "--root", root, "--live"]
					: ["codex", "mcp", "add", "devprojex", "--", "flatpak", "run", AppId, "mcp", "--root", root, "--live"]);
			}
			Add("set -- " + FlatpakEnvironment.QuoteArgument(path) + "\n" + TerminalCommandSetupService.BuildWrapperContent("/app/" + path).Replace("exec flatpak ", "flatpak ", StringComparison.Ordinal), ["flatpak", "run", AppId, path]);
			var nativeRoot = OperatingSystem.IsWindows() ? "C:\\home\\" + path : root;
			foreach (var client in new[] { McpConnectionClient.ClaudeCode, McpConnectionClient.Codex, McpConnectionClient.Cursor, McpConnectionClient.VsCode })
			{
				var result = await launcher.OpenAsync(new(client, nativeRoot), TestContext.Current.CancellationToken);
				Assert.Equal(McpClientLaunchStatus.CopyCommand, result.Status);
				script.Append(result.ManualCommand).Append('\n');
				if (client is McpConnectionClient.ClaudeCode or McpConnectionClient.Codex)
				{
					expected.Add(["cd", nativeRoot]);
					expected.Add([client == McpConnectionClient.ClaudeCode ? "claude" : "codex"]);
				}
				else expected.Add([client == McpConnectionClient.Cursor ? "cursor" : "code", nativeRoot]);
			}
		}
		var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "wsl.exe" : "/bin/sh")
		{
			RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
			UseShellExecute = false, CreateNoWindow = true, StandardInputEncoding = new UTF8Encoding(false),
			StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
		};
		if (OperatingSystem.IsWindows())
		{
			foreach (var argument in new[] { "-d", "Ubuntu", "--", "sh" }) start.ArgumentList.Add(argument);
			var probe = new ProcessStartInfo("wsl.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
			foreach (var argument in new[] { "-d", "Ubuntu", "--", "sh", "-c", "exit 0" }) probe.ArgumentList.Add(argument);
			using var check = Process.Start(probe)!;
			var output = check.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
			var error = check.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
			await check.WaitForExitAsync(TestContext.Current.CancellationToken);
			await output;
			var reason = await error;
			if (check.ExitCode != 0) Assert.Skip("WSL Ubuntu POSIX sh unavailable: " + reason);
		}
		start.WorkingDirectory = Path.GetTempPath();
		using var process = Process.Start(start)!;
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		deadline.CancelAfter(TimeSpan.FromSeconds(60));
		var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		try
		{
			await process.StandardInput.WriteAsync(script.ToString().AsMemory(), deadline.Token);
			process.StandardInput.Close();
			await process.WaitForExitAsync(deadline.Token);
		}
		finally
		{
			if (!process.HasExited) process.Kill(entireProcessTree: true);
		}
		Assert.Equal(string.Empty, await stderr);
		Assert.Equal(0, process.ExitCode);
		var fields = (await stdout).Split('\0');
		var offset = 0;
		foreach (var arguments in expected)
		{
			Assert.True(offset < fields.Length && int.TryParse(fields[offset++], out var count) && count == arguments.Length, $"Argument count seed={Seed}; offset={offset}");
			Assert.True(arguments.SequenceEqual(fields.Skip(offset).Take(arguments.Length)), $"argv seed={Seed}; offset={offset}; expected={JsonSerializer.Serialize(arguments)}");
			offset += arguments.Length;
		}
		Assert.Equal(fields.Length - 1, offset);
		Assert.Equal(string.Empty, fields[^1]);
		return;

		void Add(string command, string[] arguments) { script.Append(command).Append('\n'); expected.Add(arguments); }
	}

	[Fact]
	public void NullBytesAreRejectedInsteadOfBeingSilentlyTruncated()
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, AppId));
		foreach (var client in Enum.GetValues<McpConnectionClient>())
			Assert.Throws<ArgumentException>(() => McpConnectionFragmentGenerator.Generate(client, McpConnectionMode.Standard, "/app/DevProjex", "/home/a\0b"));
	}

	private static IEnumerable<string> Paths()
	{
		var atoms = new[] { " ", "'", "\"", "$HOME", "`id`", "\\", ";", "&", "|", "<>", "*?[]", "~", "#", "!", "%", "\n", "\t", "\r", "\u0001", "\u001b", "\u007f", "-leading", "日本語", "проект", "🚀" }
			.Concat(Enumerable.Range(1, 31).Select(value => ((char)value).ToString())).ToArray();
		foreach (var atom in atoms) yield return atom;
		var random = new Random(Seed);
		for (var index = 0; index < 2600; index++)
			yield return string.Concat(Enumerable.Range(0, random.Next(1, 16)).Select(_ => atoms[random.Next(atoms.Length)])) + index;
		yield return new string('x', 8192) + "'🚀";
	}
}
