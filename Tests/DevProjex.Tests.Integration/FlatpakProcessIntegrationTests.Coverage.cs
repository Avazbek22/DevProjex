using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Integration;

public sealed partial class FlatpakProcessIntegrationTests
{
	[Fact]
	public async Task DoctorReportsTheSandboxAndItsHostCommandThroughTheRealCli()
	{
		using var fixture = new TemporaryDirectory();
		var marker = fixture.CreateFile("flatpak-info", "marker");
		var start = Start(marker, fixture.Path, ["doctor", "--format", "json"]);
		start.Environment["PATH"] = string.Empty;
		using var process = Process.Start(start)!;
		var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		await process.WaitForExitAsync(TestContext.Current.CancellationToken);
		Assert.Equal(CommandLineExitCodes.Success, process.ExitCode);
		Assert.Equal(string.Empty, await stderr);
		using var document = JsonDocument.Parse(await stdout);
		Assert.Equal("flatpak", document.RootElement.GetProperty("packageType").GetString());
		var environment = Assert.Single(document.RootElement.GetProperty("checks").EnumerateArray(), check => check.GetProperty("detail").GetString()!.Contains("flatpak run io.github.Avazbek22.DevProjex mcp", StringComparison.Ordinal));
		Assert.Contains("flatpak run io.github.Avazbek22.DevProjex mcp", environment.GetProperty("detail").GetString());
		Assert.Contains(fixture.Path, environment.GetProperty("detail").GetString());
	}

	[Fact]
	public async Task NativeProductDoesNotHonorTestMarkerOrOverrideEnvironmentVariables()
	{
		using var fixture = new TemporaryDirectory();
		var marker = fixture.CreateFile("flatpak-info", "marker");
		var start = Start(marker, fixture.Path, ["doctor", "--format", "json"]);
		var root = new DirectoryInfo(AppContext.BaseDirectory);
		while (root is not null && !File.Exists(Path.Combine(root.FullName, "DevProjex.sln"))) root = root.Parent;
		Assert.NotNull(root);
		start.ArgumentList.Clear();
		start.ArgumentList.Add(Path.Combine(root.FullName, "Apps", "Avalonia", "bin", "Release", "net10.0", "DevProjex.dll"));
		foreach (var argument in new[] { "doctor", "--format", "json" }) start.ArgumentList.Add(argument);
		start.Environment["DEVPROJEX_FLATPAK_INFO"] = marker;
		start.Environment["DEVPROJEX_FLATPAK_OVERRIDE"] = "1";
		start.Environment["PATH"] = string.Empty;
		using var process = Process.Start(start)!;
		var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		await process.WaitForExitAsync(TestContext.Current.CancellationToken);
		Assert.Equal(CommandLineExitCodes.Success, process.ExitCode);
		Assert.Equal(string.Empty, await stderr);
		using var document = JsonDocument.Parse(await stdout);
		Assert.NotEqual("flatpak", document.RootElement.GetProperty("packageType").GetString());
		Assert.DoesNotContain(document.RootElement.GetProperty("checks").EnumerateArray(), check => check.GetProperty("detail").GetString()!.Contains("flatpak run", StringComparison.Ordinal));
	}

	[Fact]
	public async Task RealMcpClientReceivesContentAndKeepsMissingPathInstructionsInsideTheUntrustedZone()
	{
		using var fixture = new TemporaryDirectory();
		var marker = fixture.CreateFile("flatpak-info", "marker");
		fixture.CreateFile("sample.cs", "public class CoverageNeedle {}\n");
		var start = Start(marker, fixture.Path, ["mcp", "--root", fixture.Path]);
		await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
		{
			Name = "Sandbox content",
			Command = start.FileName,
			Arguments = start.ArgumentList.ToArray(),
			WorkingDirectory = fixture.Path,
			EnvironmentVariables = start.Environment.ToDictionary(pair => pair.Key, pair => pair.Value)
		}), cancellationToken: TestContext.Current.CancellationToken);
		foreach (var (tool, arguments, expected) in new[]
		{
			("get_tree", new Dictionary<string, object?>(), "sample.cs"),
			("search_project", new Dictionary<string, object?> { ["pattern"] = "CoverageNeedle" }, "CoverageNeedle"),
			("get_file", new Dictionary<string, object?> { ["path"] = "sample.cs" }, "CoverageNeedle")
		})
		{
			var result = await client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);
			Assert.NotEqual(true, result.IsError);
			Assert.Contains(expected, string.Join('\n', result.Content.OfType<TextContentBlock>().Select(block => block.Text)));
		}
		var missing = await client.CallToolAsync("get_file", new Dictionary<string, object?> { ["path"] = "missing project.txt" }, cancellationToken: TestContext.Current.CancellationToken);
		Assert.True(missing.IsError);
		var error = Assert.IsType<TextContentBlock>(Assert.Single(missing.Content)).Text;
		var begin = error.IndexOf("<untrusted-data-", StringComparison.Ordinal);
		var end = error.LastIndexOf("</untrusted-data-", StringComparison.Ordinal);
		Assert.True(begin > 0 && end > begin);
		var command = error.IndexOf("flatpak override", StringComparison.Ordinal);
		Assert.InRange(command, begin, end);
		Assert.Equal("DPX-MCP-PATH-NOT-FOUND: request failed.", error.Split('\n')[0]);
		Assert.DoesNotContain("missing project.txt", error[..begin]);
		Assert.DoesNotContain(fixture.Path, error[..begin]);
	}
}
