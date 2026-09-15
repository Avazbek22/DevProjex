using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task BashRelatedFilesAndCliRelatedAgreeOnLiteralAndDynamicSources()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/main.sh", "source ./lib.sh\n. \"$SCRIPT_DIR/dynamic.sh\"\ngrep value input\n");
		workspace.WriteFile("project/lib.sh", "setup() { :; }\n");
		workspace.WriteFile("project/dynamic.sh", ":\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		var result = await CallAsync(server, "related_files", new Dictionary<string, object?> { ["path"] = "main.sh", ["direction"] = "dependencies" });
		var text = AllProcessText(result);
		Assert.NotEqual(true, result.IsError);
		Assert.Contains("lib.sh", text, StringComparison.Ordinal);
		Assert.Contains("[Resolution] resolved=1 · ambiguous=0 · unresolved=1 · external=0", text, StringComparison.Ordinal);
		var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
		foreach (var argument in new[] { PublishedApplicationLocator.FindApplicationAssembly(), "related", "main.sh", "--project", project,
			"--direction", "dependencies", "--format", "json", "--git-mode", "none", "--exclude", "none", "--language", "en", "--plain", "--progress", "never" })
			start.ArgumentList.Add(argument);
		start.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = workspace.CreateDirectory("cli-data");
		var cli = TerminalTestProcess.Run(start, TimeSpan.FromMinutes(1));
		Assert.True(cli.ExitCode == 0, cli.StandardError + cli.StandardOutput);
		using var document = JsonDocument.Parse(cli.StandardOutput);
		var resolution = document.RootElement.GetProperty("resolution");
		Assert.Equal(1, resolution.GetProperty("resolved").GetInt32());
		Assert.Equal(1, resolution.GetProperty("unresolved").GetInt32());
		var dependencies = Assert.Single(document.RootElement.GetProperty("seeds").EnumerateArray()).GetProperty("dependencies");
		Assert.Equal("lib.sh", Assert.Single(dependencies.EnumerateArray()).GetProperty("path").GetString());
	}
}
