using System.Diagnostics;
using System.Text.Json;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task ScalaRelatedFilesAndCliRelatedAgreeOnAliasesTypesAndWildcards()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/build.sbt", "// static ownership\n");
		workspace.WriteFile("project/Main.scala", "package app\nimport models.{Remote => Renamed}\nclass Main(value: Renamed)\nimport unknown._\n");
		workspace.WriteFile("project/models/Remote.scala", "package models\nclass Remote\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		var result = await CallAsync(server, "related_files", new Dictionary<string, object?> { ["path"] = "Main.scala", ["direction"] = "dependencies" });
		var text = AllProcessText(result);
		Assert.NotEqual(true, result.IsError);
		Assert.Contains("models/Remote.scala", text, StringComparison.Ordinal);
		Assert.Contains("[Resolution] resolved=2 · ambiguous=0 · unresolved=1 · external=0", text, StringComparison.Ordinal);
		var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
		foreach (var argument in new[] { PublishedApplicationLocator.FindApplicationAssembly(), "related", "Main.scala", "--project", project,
			"--direction", "dependencies", "--format", "json", "--git-mode", "none", "--exclude", "none", "--language", "en", "--plain", "--progress", "never" })
			start.ArgumentList.Add(argument);
		start.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = workspace.CreateDirectory("cli-data");
		var cli = TerminalTestProcess.Run(start, TimeSpan.FromMinutes(1));
		Assert.True(cli.ExitCode == 0, cli.StandardError + cli.StandardOutput);
		using var document = JsonDocument.Parse(cli.StandardOutput);
		var resolution = document.RootElement.GetProperty("resolution");
		Assert.Equal(2, resolution.GetProperty("resolved").GetInt32());
		Assert.Equal(1, resolution.GetProperty("unresolved").GetInt32());
		var dependencies = Assert.Single(document.RootElement.GetProperty("seeds").EnumerateArray()).GetProperty("dependencies");
		Assert.Equal("models/Remote.scala", Assert.Single(dependencies.EnumerateArray()).GetProperty("path").GetString());
	}
}
