using System.Text.RegularExpressions;
using DevProjex.Infrastructure.ProjectProfiles;
using DevProjex.Kernel.Models;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessSearchReportsOneExecutableNextReadAndStoredWindowsHonestly()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile(
			"project/App.cs",
			"namespace Sample;\npublic sealed class App\n{\n" +
			string.Join('\n', Enumerable.Range(0, 40).Select(index =>
				$"\tpublic int Needle{index:D2}() => {index};")) +
			"\n}\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var text = Normalize(AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = "public int Needle",
				["context_lines"] = 0,
				["max_results"] = 1
			})));

		const string boundary = "[Search boundary] partial; retained matches are available below.";
		const string nextRead =
			"[Next read] Call read_pack with the reported pack_id for the remaining retained matches.";
		Assert.True(
			text.IndexOf(boundary, StringComparison.Ordinal) <
			text.IndexOf("<untrusted-data-", StringComparison.Ordinal),
			text);
		Assert.Contains(
			"retained match windows only, not complete source files. Call read_pack with this pack_id.",
			text,
			StringComparison.Ordinal);
		Assert.Single(Regex.Matches(text, Regex.Escape(nextRead)).Cast<Match>());
		Assert.DoesNotContain("read_pack pages those files whole", text, StringComparison.Ordinal);
		Assert.DoesNotContain("read the other", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessSearchPointsOnlyAtNeededKnownDeclarationsWhenNothingIsStored()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile(
			"project/App.cs",
			"namespace Sample;\npublic sealed class ExactNeedle\n{\n\tpublic int Run() => 1;\n}\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var text = Normalize(AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = "ExactNeedle",
				["context_lines"] = 0
			})));

		Assert.Contains(
			"[Next read] Read only declarations needed for the task; batch known selections in one get_file call.",
			text,
			StringComparison.Ordinal);
		Assert.DoesNotContain("[Read declarations]", text, StringComparison.Ordinal);
		Assert.DoesNotContain("in full", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessGetFileExplainsBodyRemovalFromStandardAndLiveProfiles()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		const string implementation = "implementation-marker-from-method-body";
		workspace.WriteFile(
			"project/App.cs",
			$"public sealed class App {{ public int Run() {{ var value = \"{implementation}\"; return value.Length; }} }}\n");
		var dataRoot = workspace.CreateDirectory("data");

		await using (var standard = await ActualMcpProcess.StartAsync(project, dataRoot))
		{
			var listed = await CallAsync(
				standard,
				"list_projects",
				new Dictionary<string, object?>());
			var canonicalProject = Assert.Single(Structured(listed).GetProperty("projects").EnumerateArray())
				.GetProperty("path")
				.GetString()!;
			new ProjectProfileStore(() => dataRoot).SaveProfile(
				canonicalProject,
				new ProjectSelectionProfile(
					SelectedRootFolders: [],
					SelectedExtensions: [".cs"],
					SelectedIgnoreOptions: [IgnoreOptionId.CompressCode],
					IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
					{
						[IgnoreOptionId.CompressCode] = true
					}));
			var text = AllProcessText(await CallAsync(
				standard,
				"get_file",
				new Dictionary<string, object?> { ["path"] = "App.cs", ["profile"] = "local" }));
			Assert.DoesNotContain(implementation, text, StringComparison.Ordinal);
			Assert.Contains(
				"[Content transformed] Function bodies were removed by the active profile; this is not the original implementation.",
				text,
				StringComparison.Ordinal);
			Assert.DoesNotContain("[Next read] Ask the user", text, StringComparison.Ordinal);
		}

		await using (var live = await ActualMcpProcess.StartAsync(project, dataRoot, ["--live"]))
		{
			var text = AllProcessText(await CallAsync(
				live,
				"get_file",
				new Dictionary<string, object?> { ["path"] = "App.cs" }));
			Assert.DoesNotContain(implementation, text, StringComparison.Ordinal);
			Assert.Contains(
				"[Content transformed] Function bodies were removed by the active profile; this is not the original implementation.",
				text,
				StringComparison.Ordinal);
			Assert.Contains(
				"[Next read] Ask the user to disable code compression in the window before reading implementation bodies.",
				text,
				StringComparison.Ordinal);
		}
	}

	[Fact]
	public async Task RealProcessDetailSchemaSaysFullDoesNotUndoProfileTransforms()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/App.cs", "internal sealed class App;\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var tools = await server.Client.ListToolsAsync(options: null, TestContext.Current.CancellationToken);
		var descriptions = tools
			.Select(static tool => tool.ProtocolTool.InputSchema.GetProperty("properties"))
			.Where(static properties => properties.TryGetProperty("detail", out _))
			.Select(static properties => properties.GetProperty("detail").GetProperty("description").GetString())
			.ToArray();

		Assert.NotEmpty(descriptions);
		Assert.All(descriptions, description => Assert.Contains(
			"full adds no transformations; transformations enabled by the active profile still apply.",
			description,
			StringComparison.Ordinal));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RealProcessExclusionsSchemaMatchesTheServerMode(bool live)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/App.cs", "internal sealed class App;\n");
		var arguments = new List<string> { "--allow-agent-exclusions" };
		if (live)
			arguments.Add("--live");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"),
			arguments);

		var tools = await server.Client.ListToolsAsync(options: null, TestContext.Current.CancellationToken);
		var descriptions = tools
			.Select(static tool => tool.ProtocolTool.InputSchema.GetProperty("properties"))
			.Where(static properties => properties.TryGetProperty("exclusions", out _))
			.Select(static properties => properties.GetProperty("exclusions").GetProperty("description").GetString())
			.ToArray();

		Assert.NotEmpty(descriptions);
		Assert.All(descriptions, description => Assert.Contains(
			live
				? "Additional exclusions for this call. An empty array keeps the window and startup filters unchanged."
				: "Per-call exclusions can replace startup exclusions; paths and patterns only narrow the resulting selection.",
			description,
			StringComparison.Ordinal));
	}

	[Fact]
	public async Task RealProcessStoredRankedPackKeepsEveryUntrustedDelimiterBalanced()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		for (var index = 0; index < 24; index++)
		{
			var name = $"src/{index:D2}-{new string((char)('a' + index % 26), 90)}.cs";
			workspace.WriteFile(
				$"project/{name}",
				$"namespace Sample; public sealed class Type{index:D2} {{ public int Value => {index}; }}\n");
		}
		workspace.WriteFile("project/Large.txt", new string('x', 60_000));
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var text = AllProcessText(await CallAsync(
			server,
			"pack_context",
			new Dictionary<string, object?>
			{
				["view"] = "content",
				["format"] = "text",
				["rank"] = "importance"
			}));

		var openings = Regex.Matches(text, "<untrusted-data-([0-9a-f]{24})>");
		var closings = Regex.Matches(text, "</untrusted-data-([0-9a-f]{24})>");
		Assert.NotEmpty(openings);
		Assert.Equal(openings.Count, closings.Count);
		foreach (Match opening in openings)
		{
			Assert.Contains(
				$"</untrusted-data-{opening.Groups[1].Value}>",
				text,
				StringComparison.Ordinal);
		}
		Assert.Contains(
			"[Ranking truncated] Additional ranking details were omitted; packed content is unchanged.",
			text,
			StringComparison.Ordinal);
		Assert.True(
			text.IndexOf("[Ranking truncated]", StringComparison.Ordinal) >
			text.LastIndexOf("</untrusted-data-", StringComparison.Ordinal));
	}
}
