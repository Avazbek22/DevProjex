using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using DevProjex.Infrastructure.TerminalCommands;

if (args[0] == "--shim")
{
	var home = Path.GetFullPath(args[1]);
	var service = new TerminalCommandSetupService(new()
	{
		HomeDirectoryProvider = () => home,
		ExecutablePathProvider = () => args[2],
		PathVariableProvider = () => Path.Combine(home, ".local", "bin") + ":" + Environment.GetEnvironmentVariable("PATH"),
		ProcessPathVariableWriter = _ => { },
		ShellPathProvider = () => "/bin/bash"
	});
	var result = service.InstallOrRepair();
	Console.WriteLine(JsonSerializer.Serialize(result));
	if (!result.Success) throw new InvalidOperationException("Managed terminal setup failed.");
	return;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
var root = Path.GetFullPath(args[0]);
var live = args.Contains("--live", StringComparer.Ordinal);
var selected = live ? "selected.cs" : "Main.kt";
var evidence = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;
var serverArguments = new List<string> { "run", "io.github.Avazbek22.DevProjex", "mcp", "--root", root };
if (live) serverArguments.Add("--live");
var command = "flatpak";
if (evidence is not null)
{
	Directory.CreateDirectory(evidence);
	serverArguments.Insert(0, "flatpak");
	serverArguments.Insert(0, evidence);
	serverArguments.Insert(0, Environment.GetEnvironmentVariable("DEVPROJEX_FLATPAK_TRANSPORT") ??
		Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "transport.py"));
	command = "python3";
}
var transport = new StdioClientTransport(new StdioClientTransportOptions
{
	Name = "DevProjex Flatpak",
	Command = command,
	Arguments = serverArguments,
	WorkingDirectory = root
});
await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
var tree = await CallAsync("get_tree", null);
if (!tree.Contains(selected, StringComparison.Ordinal) || live && tree.Contains("outside.cs", StringComparison.Ordinal))
	throw new InvalidOperationException("The installed Flatpak did not return the expected project selection.");
var search = await CallAsync("search_project", new() { ["pattern"] = "FlatpakSearchMarker" });
if (!search.Contains(selected, StringComparison.Ordinal) || live && search.Contains("OutsideMarker", StringComparison.Ordinal))
	throw new InvalidOperationException("The installed Flatpak search did not use the expected selection.");
var file = await CallAsync("get_file", new() { ["path"] = selected });
if (!file.Contains("FlatpakSearchMarker", StringComparison.Ordinal))
	throw new InvalidOperationException("The installed Flatpak did not return the expected file.");
Console.WriteLine("Installed Flatpak MCP client check passed: get_tree, search_project, get_file.");
if (args.Contains("--hold", StringComparer.Ordinal))
{
	if (evidence is null) throw new ArgumentException("--hold requires an evidence directory.");
	await File.WriteAllTextAsync(Path.Combine(evidence, "ready"), "ready", timeout.Token);
	while (!File.Exists(Path.Combine(evidence, "release")))
		await Task.Delay(100, timeout.Token);
}

async Task<string> CallAsync(string name, Dictionary<string, object?>? arguments)
{
	var result = await client.CallToolAsync(name, arguments, cancellationToken: timeout.Token);
	var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static item => item.Text));
	if (evidence is not null)
		await File.WriteAllTextAsync(Path.Combine(evidence, name + ".json"), JsonSerializer.Serialize(result), timeout.Token);
	if (result.IsError == true) throw new InvalidOperationException(name + " failed: " + text);
	return text;
}
