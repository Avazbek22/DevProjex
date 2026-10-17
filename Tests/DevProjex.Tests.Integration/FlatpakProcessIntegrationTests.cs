using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Integration;

public sealed class FlatpakProcessIntegrationTests
{
	[Fact]
	public async Task CliPrintsAHostExecutableThroughTheRealCommandConsumer()
	{
		using var fixture = new TemporaryDirectory();
		var marker = fixture.CreateFile("flatpak-info", "[Application]\n");
		var start = Start(marker, fixture.Path,
			["mcp", "connect", fixture.Path, "--client", "cursor", "--print"]);
		using var process = Process.Start(start)!;
		var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		await process.WaitForExitAsync(TestContext.Current.CancellationToken);
		Assert.Equal(0, process.ExitCode);
		using var json = JsonDocument.Parse(await stdout);
		var server = json.RootElement.GetProperty("mcpServers").GetProperty("devprojex");
		Assert.Equal("flatpak", server.GetProperty("command").GetString());
		Assert.Equal("run", server.GetProperty("args")[0].GetString());
		Assert.Equal(string.Empty, await stderr);
	}

	[Fact]
	public async Task CliMissingRootExplainsBothAbsenceAndSandboxAccess()
	{
		using var fixture = new TemporaryDirectory();
		var marker = fixture.CreateFile("flatpak-info", "[Application]\n");
		var missing = Path.Combine(fixture.Path, "not-accessible");
		using var process = Process.Start(Start(marker, fixture.Path, ["analyze", missing]))!;
		var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		await process.WaitForExitAsync(TestContext.Current.CancellationToken);
		Assert.NotEqual(0, process.ExitCode);
		Assert.Contains("flatpak override --user", await stderr);
		Assert.Equal(string.Empty, await stdout);
	}

	[Fact]
	public async Task McpClientInitializesAndReadsTreeFromASeparateSandboxProcess()
	{
		using var fixture = new TemporaryDirectory();
		var marker = fixture.CreateFile("flatpak-info", "[Application]\n");
		fixture.CreateFile("main.cs", "public class Sample {}\n");
		var start = Start(marker, fixture.Path, ["mcp", "--root", fixture.Path]);
		var options = new StdioClientTransportOptions
		{
			Name = "Flatpak process",
			Command = start.FileName,
			Arguments = start.ArgumentList.ToArray(),
			WorkingDirectory = fixture.Path,
			EnvironmentVariables = new Dictionary<string, string?>
			{
				["FLATPAK_ID"] = "io.github.Avazbek22.DevProjex",
				["DEVPROJEX_INTERNAL_DATA_ROOT"] = fixture.Path,
				["DEVPROJEX_LANG"] = "en"
			}
		};
		await using var client = await McpClient.CreateAsync(new StdioClientTransport(options),
			cancellationToken: TestContext.Current.CancellationToken);
		var result = await client.CallToolAsync("get_tree", cancellationToken: TestContext.Current.CancellationToken);
		Assert.NotEqual(true, result.IsError);
		Assert.Contains("main.cs", string.Join('\n', result.Content.OfType<TextContentBlock>().Select(static b => b.Text)));
		var missing = await client.CallToolAsync("get_file", new Dictionary<string, object?> { ["path"] = "missing.txt" },
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.True(missing.IsError);
		var error = string.Join('\n', missing.Content.OfType<TextContentBlock>().Select(static b => b.Text));
		Assert.Contains("flatpak override", error);
		Assert.Contains("DPX-MCP-PATH-NOT-FOUND: request failed.", error);
		Assert.DoesNotContain("missing.txt", error.Split('\n')[0]);
		var state = Path.Combine(fixture.Path, "DevProjex");
		var livePath = Assert.Single(Directory.GetFiles(Path.Combine(state, "live-sessions"), "*.json"));
		var sandboxId = Path.GetFileNameWithoutExtension(livePath);
		Assert.True(Guid.TryParseExact(sandboxId, "N", out _));
		var journalPath = Assert.Single(Directory.GetFiles(Path.Combine(state, "agent-journal"), "*.jsonl"));
		using var header = JsonDocument.Parse(File.ReadLines(journalPath).First());
		Assert.Equal(sandboxId, header.RootElement.GetProperty("session").GetProperty("sandboxSessionId").GetString());
		Assert.EndsWith("-" + sandboxId + ".jsonl", journalPath);
	}

	private static ProcessStartInfo Start(string marker, string root, string[] arguments)
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DevProjex.sln")))
			directory = directory.Parent;
		var host = Path.Combine(directory!.FullName, "Tests", "DevProjex.Tests.FlatpakHost", "bin",
			"Release", "net10.0", "DevProjex.Tests.FlatpakHost.dll");
		var start = new ProcessStartInfo("dotnet")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = true,
			UseShellExecute = false,
			WorkingDirectory = root
		};
		start.ArgumentList.Add(host);
		start.ArgumentList.Add(marker);
		foreach (var argument in arguments)
			start.ArgumentList.Add(argument);
		start.Environment["FLATPAK_ID"] = "io.github.Avazbek22.DevProjex";
		start.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = root;
		start.Environment["DEVPROJEX_LANG"] = "en";
		return start;
	}
}
