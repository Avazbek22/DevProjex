using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Theory]
	[InlineData(false, "index")]
	[InlineData(false, "unix-path")]
	[InlineData(false, "drive-path")]
	[InlineData(true, "index")]
	[InlineData(true, "unix-path")]
	[InlineData(true, "drive-path")]
	public async Task RealProcessUnknownProjectExplainsHowToAddressTheConfiguredRoots(bool multipleRoots, string spelling)
	{
		const string marker = "Zq9UnknownProjectMarker";
		const string singleProjectHint = "DPX-MCP-UNKNOWN-PROJECT: 'project' does not name the configured root. One local root is configured: omit 'project'.";
		using var workspace = new TemporaryDirectory();
		var first = workspace.CreateDirectory("first-project");
		var second = workspace.CreateDirectory("second-project");
		workspace.WriteFile("first-project/first.log", "first\n");
		workspace.WriteFile("second-project/second.log", "second\n");
		await using var server = await ActualMcpProcess.StartAsync(first, workspace.CreateDirectory("data"),
			multipleRoots ? ["--root", second] : []);
		var project = spelling switch
		{
			"index" => "#99",
			"unix-path" => "/home/" + marker,
			"drive-path" => "/C:/Users/" + marker,
			_ => throw new ArgumentOutOfRangeException(nameof(spelling))
		};

		var response = await CallAsync(server, "get_tree", new Dictionary<string, object?> { ["project"] = project });

		Assert.True(response.IsError);
		var text = AllProcessText(response);
		Assert.StartsWith("DPX-MCP-UNKNOWN-PROJECT:", text, StringComparison.Ordinal);
		Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
		if (multipleRoots)
		{
			Assert.Single(Regex.Matches(text, "Call list_projects", RegexOptions.None, TimeSpan.FromSeconds(2)));
			Assert.Equal("DPX-MCP-UNKNOWN-PROJECT: project is not an allowed root name, path, or index. Call list_projects and use a listed project index: #1, #2.", SpotlightBody(Normalize(text)).TrimEnd());
			Assert.Contains("Call list_projects and use a listed project index:", text, StringComparison.Ordinal);
		}
		else
		{
			Assert.DoesNotContain("list_projects", text, StringComparison.Ordinal);
			Assert.Equal(singleProjectHint, SpotlightBody(Normalize(text)).TrimEnd());
		}

		var valid = await CallAsync(server, "get_tree", new Dictionary<string, object?>
		{
			["project"] = multipleRoots ? "#2" : "#0",
			["format"] = "text"
		});
		Assert.NotEqual(true, valid.IsError);
		Assert.Contains(multipleRoots ? "second.log" : "first.log", AllProcessText(valid), StringComparison.Ordinal);
		if (!multipleRoots)
		{
			var omitted = await CallAsync(server, "get_tree", new Dictionary<string, object?> { ["format"] = "text" });
			Assert.NotEqual(true, omitted.IsError);
			Assert.Contains("first.log", AllProcessText(omitted), StringComparison.Ordinal);
		}
	}
}
