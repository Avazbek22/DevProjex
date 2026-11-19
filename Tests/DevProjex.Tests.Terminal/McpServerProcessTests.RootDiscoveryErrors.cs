namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Theory]
	[InlineData(false, "full")]
	[InlineData(true, "full")]
	[InlineData(false, "reduced")]
	[InlineData(true, "reduced")]
	public async Task RealProcessDiscoveryErrorsMatchTheRootCount(bool multiple, string toolSet)
	{
		using var workspace = new TemporaryDirectory();
		var root = workspace.CreateDirectory("first");
		var arguments = new List<string> { "--tool-set", toolSet };
		if (multiple) arguments.AddRange(["--root", workspace.CreateDirectory("second")]);
		await using var server = await ActualMcpProcess.StartAsync(root, workspace.CreateDirectory("data"), arguments);
		var host = ExtractSpotlightBody(AllProcessText(await CallAsync(server, "get_tree",
			new() { ["project"] = "//catalog.invalid/share" })));
		Assert.Equal("DPX-MCP-INVALID-ARGUMENTS: 'project' is written as a path that names a host. Such a form is refused before 'project' is read as a path at all, so that naming one cannot make the server reach it. " +
			(multiple ? "Call list_projects and use a listed name or path." : "One local root is configured: omit 'project'."), host);
		foreach (var constraint in new[] { "tracked_only", "git_scope", "tracked_only and git_scope" })
		{
			var args = new Dictionary<string, object?>();
			if (multiple) args["project"] = "#1";
			if (constraint.Contains("tracked_only", StringComparison.Ordinal)) args["tracked_only"] = true;
			if (constraint.Contains("git_scope", StringComparison.Ordinal)) args["git_scope"] = "staged";
			var text = ExtractSpotlightBody(AllProcessText(await CallAsync(server, "get_tree", args)));
			Assert.Equal($"DPX-MCP-INVALID-ARGUMENTS: project is not a git repository; omit {constraint}" +
				(multiple ? " or choose a Git repository returned by list_projects." : "."), text);
		}
	}

}
