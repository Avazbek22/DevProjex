namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task InstalledHeadlessArtifactSupportsTheReadWorkflow(bool live)
	{
		var executable = Environment.GetEnvironmentVariable("DEVPROJEX_INSTALLED_ARTIFACT");
		if (string.IsNullOrWhiteSpace(executable))
			Assert.Skip("Set DEVPROJEX_INSTALLED_ARTIFACT to verify an installed headless artifact.");

		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/Probe.txt", "artifactNeedle\n");
		workspace.WriteFile("project/Large.txt", "large-marker\n" + new string('x', 70_000));
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"),
			live ? ["--live"] : [],
			executable: executable);

		var listed = await CallAsync(server, "list_projects", new Dictionary<string, object?>());
		Assert.Contains("project", AllProcessText(listed), StringComparison.Ordinal);
		var tree = await CallAsync(server, "get_tree", new Dictionary<string, object?> { ["format"] = "text" });
		Assert.Contains("Probe.txt", AllProcessText(tree), StringComparison.Ordinal);
		var search = await CallAsync(server, "search_project", new Dictionary<string, object?>
		{
			["pattern"] = "artifactNeedle",
			["context_lines"] = 0,
			["ignore_case"] = false
		});
		Assert.Contains("src/Probe.txt", AllProcessText(search), StringComparison.Ordinal);
		var file = await CallAsync(server, "get_file", new Dictionary<string, object?>
		{
			["path"] = "src/Probe.txt"
		});
		Assert.Contains("artifactNeedle", AllProcessText(file), StringComparison.Ordinal);
		var packed = await CallAsync(server, "pack_context", new Dictionary<string, object?>
		{
			["paths"] = new[] { "Large.txt" },
			["view"] = "content",
			["format"] = "text"
		});
		var page = await CallAsync(server, "read_pack", new Dictionary<string, object?>
		{
			["pack_id"] = ExtractPackId(AllProcessText(packed))
		});
		Assert.Contains("large-marker", AllProcessText(page), StringComparison.Ordinal);
	}
}
