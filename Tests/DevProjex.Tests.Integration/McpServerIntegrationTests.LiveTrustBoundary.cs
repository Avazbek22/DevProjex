using DevProjex.Application.Context;
using DevProjex.Infrastructure.LiveContext;
using DevProjex.Tests.Mcp;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	[Fact]
	public async Task LiveResponsesKeepProjectControlledNamesInsideUntrustedData()
	{
		const string marker = "LIVE_TRUST_SENTINEL_4d7c";
		using var workspace = new TemporaryDirectory();
		var lineBreak = OperatingSystem.IsWindows() ? string.Empty : "\nsecond-line";
		var first = workspace.CreateDirectory("first-" + marker + lineBreak);
		var second = workspace.CreateDirectory("second-" + marker + lineBreak);
		var selectedName = "selected-" + marker + lineBreak;
		var changedName = "changed-" + marker + lineBreak;
		var outsideName = "outside-" + marker + lineBreak;
		var selected = Directory.CreateDirectory(Path.Combine(first, selectedName)).FullName;
		var changed = Directory.CreateDirectory(Path.Combine(first, changedName)).FullName;
		var outside = Directory.CreateDirectory(Path.Combine(first, outsideName)).FullName;
		var sourceName = "Source-" + marker + ".cs";
		var largeName = "Large-" + marker + ".txt";
		var outsideFileName = "Outside-" + marker + ".cs";
		var secondOutsideFileName = "SecondOutside-" + marker + ".cs";
		File.WriteAllText(
			Path.Combine(selected, sourceName),
			"namespace Sample; class Source { const string Value = \"" + marker + "\"; }\n");
		File.WriteAllText(Path.Combine(selected, largeName), marker + "\n" + new string('x', 70_000));
		File.WriteAllText(Path.Combine(changed, "Changed.cs"), "class Changed {}\n");
		File.WriteAllText(Path.Combine(outside, outsideFileName), "class Outside {}\n");
		File.WriteAllText(Path.Combine(outside, secondOutsideFileName), "class SecondOutside {}\n");
		File.WriteAllText(Path.Combine(second, "Second.cs"), "class Second {}\n");
		var profileStore = new ProjectProfileStore(() => Path.Combine(workspace.Path, "app-data"));
		profileStore.SaveProfile(first, new ProjectSelectionProfile([], [], [], SelectedPaths: [selectedName]));
		profileStore.SaveProfile(second, new ProjectSelectionProfile([], [], [], SelectedPaths: null));
		await using var server = await McpTestServer.StartAsync(
			[first, second],
			workspace.Path,
			live: true);

		var pack = await server.CallAsync(
			"pack_context",
			new Dictionary<string, object?>
			{
				["project"] = first,
				["paths"] = new[] { selectedName + "/" + largeName },
				["view"] = "content",
				["format"] = "text"
			});
		var packId = ExtractPackId(AllText(pack));
		var results = new List<(string Name, CallToolResult Result)>
		{
			("list_projects", await server.CallAsync("list_projects")),
			("get_tree", await server.CallAsync(
				"get_tree",
				new Dictionary<string, object?> { ["project"] = first, ["format"] = "text" })),
			("analyze", await server.CallAsync(
				"analyze",
				new Dictionary<string, object?> { ["project"] = first })),
			("pack_context", pack),
			("read_pack", await server.CallAsync(
				"read_pack",
				new Dictionary<string, object?> { ["pack_id"] = packId })),
			("search_project", await server.CallAsync(
				"search_project",
				new Dictionary<string, object?> { ["project"] = first, ["pattern"] = marker })),
			("related_files", await server.CallAsync(
				"related_files",
				new Dictionary<string, object?>
				{
					["project"] = first,
					["path"] = selectedName + "/" + sourceName
				})),
			("get_file", await server.CallAsync(
				"get_file",
				new Dictionary<string, object?>
				{
					["project"] = first,
					["path"] = outsideName + "/" + outsideFileName
				}))
		};

		Assert.Equal(
			ExpectedTools.Order(StringComparer.Ordinal),
			results.Select(static item => item.Name).Order(StringComparer.Ordinal));
		results.Add(("get_file-batch", await server.CallAsync(
			"get_file",
			new Dictionary<string, object?>
			{
				["project"] = first,
				["requests"] = new object[]
				{
					new { path = outsideName + "/" + outsideFileName },
					new { path = outsideName + "/" + secondOutsideFileName }
				}
			})));
		profileStore.SaveProfile(first, new ProjectSelectionProfile([], [], [], SelectedPaths: [changedName]));
		results.Add(("changed-profile", await server.CallAsync(
			"get_tree",
			new Dictionary<string, object?> { ["project"] = first, ["format"] = "text" })));

		foreach (var (name, result) in results)
		{
			Assert.NotEqual(true, result.IsError);
			AssertMarkerOnlyInsideUntrustedData(result, marker, name);
			if (!OperatingSystem.IsWindows())
				AssertMarkerOnlyInsideUntrustedData(result, "second-line", name + " line-break name");
		}
		var outsideText = AllText(results.Single(static item => item.Name == "get_file").Result);
		Assert.Contains(
			"[Live context] the named path is outside the current window selection",
			outsideText,
			StringComparison.Ordinal);
		Assert.Contains(
			"[Live context] 2 named paths are outside the current window selection",
			AllText(results.Single(static item => item.Name == "get_file-batch").Result),
			StringComparison.Ordinal);
		var changedText = AllText(results.Single(static item => item.Name == "changed-profile").Result);
		Assert.Contains(
			"[Live context] changed since revision 1: +1 folder, -1 folder",
			changedText,
			StringComparison.Ordinal);
	}

	private static void AssertMarkerOnlyInsideUntrustedData(
		CallToolResult result,
		string marker,
		string context)
	{
		var found = false;
		foreach (var block in result.Content.OfType<TextContentBlock>())
		{
			var ranges = Regex.Matches(
				block.Text,
				@"<untrusted-data-(?<nonce>[0-9a-f]{24})>\n(?<body>[\s\S]*?)\n</untrusted-data-\k<nonce>>")
				.Cast<Match>()
				.Select(static match =>
					(Start: match.Groups["body"].Index, End: match.Groups["body"].Index + match.Groups["body"].Length))
				.ToArray();
			foreach (Match occurrence in Regex.Matches(block.Text, Regex.Escape(marker)))
			{
				found = true;
				Assert.Contains(
					ranges,
					range => occurrence.Index >= range.Start && occurrence.Index + occurrence.Length <= range.End);
			}
		}
		Assert.True(found, $"{context} did not contain the project-controlled marker.");
	}
}
