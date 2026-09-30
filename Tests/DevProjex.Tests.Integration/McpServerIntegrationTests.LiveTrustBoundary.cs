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
			"[Live context] 1 named file(s) returned outside the current focus; effective filters still apply.",
			outsideText,
			StringComparison.Ordinal);
		Assert.Contains(
			"[Live context] 2 named file(s) returned outside the current focus; effective filters still apply.",
			AllText(results.Single(static item => item.Name == "get_file-batch").Result),
			StringComparison.Ordinal);
		var changedText = AllText(results.Single(static item => item.Name == "changed-profile").Result);
		Assert.Contains(
			"[Live context] changed since revision 1: +1 folder, -1 folder",
			changedText,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task DynamicFilterBudgetAndPatternValuesStayInsideUntrustedData()
	{
		if (!IsGitAvailable())
			return;
		const string marker = "MCP_DYNAMIC_TRUST_SENTINEL_8e2b";
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var skippedName = "Skipped-" + marker + ".txt";
		File.WriteAllText(Path.Combine(project, "Small.txt"), "small\n");
		File.WriteAllText(Path.Combine(project, skippedName), new string('x', 8_000));
		RunGit(project, "init", "--quiet");
		RunGit(project, "config", "user.name", "DevProjex Tests");
		RunGit(project, "config", "user.email", "devprojex@example.invalid");
		RunGit(project, "add", ".");
		RunGit(project, "commit", "--quiet", "-m", "baseline");
		var baseline = "baseline-" + marker;
		RunGit(project, "branch", baseline);
		File.AppendAllText(Path.Combine(project, "Small.txt"), "changed\n");
		RunGit(project, "add", "Small.txt");
		RunGit(project, "commit", "--quiet", "-m", "changed");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var budget = await server.CallAsync(
			"analyze",
			new Dictionary<string, object?> { ["max_tokens"] = 1 });
		var pattern = await server.CallAsync(
			"pack_context",
			new Dictionary<string, object?>
			{
				["view"] = "content",
				["format"] = "text",
				["detail_by_pattern"] = new object[]
				{
					new Dictionary<string, object?>
					{
						["patterns"] = new[] { "missing-" + marker + "/**" },
						["detail"] = "compact"
					}
				}
			});
		var diff = await server.CallAsync(
			"get_tree",
			new Dictionary<string, object?>
			{
				["format"] = "text",
				["git_scope"] = $"diff:{baseline}..HEAD"
			});

		AssertMarkerOnlyInsideUntrustedData(budget, marker, "analyze token budget");
		AssertMarkerOnlyInsideUntrustedData(pattern, marker, "detail pattern");
		AssertMarkerOnlyInsideUntrustedData(diff, marker, "diff reference");
	}

	[Fact]
	public async Task LiveFocusCoverageAndWindowFilterRefusalsStayCountOnly()
	{
		const string marker = "LIVE_FOCUS_SENTINEL_91c3";
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		Directory.CreateDirectory(Path.Combine(project, "src"));
		var dotFolder = ".ci-" + marker;
		Directory.CreateDirectory(Path.Combine(project, dotFolder));
		var guide = "Guide-" + marker + ".md";
		var extensionless = "Build" + marker;
		File.WriteAllText(Path.Combine(project, "src", "App.cs"), "class App { Util Value = new(); } // focus-marker\n");
		File.WriteAllText(Path.Combine(project, "src", "Util.cs"), "class Util { } // focus-marker\n");
		File.WriteAllText(Path.Combine(project, guide), "focus-marker guide\n");
		File.WriteAllText(Path.Combine(project, extensionless), "focus-marker build\n");
		File.WriteAllText(Path.Combine(project, dotFolder, "pipeline.yml"), "focus-marker: ci\n");
		new ProjectProfileStore(() => Path.Combine(workspace.Path, "app-data")).SaveProfile(
			project,
			new ProjectSelectionProfile(
				[],
				[],
				[IgnoreOptionId.ExtensionlessFiles, IgnoreOptionId.DotFolders],
				IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.ExtensionlessFiles] = true,
					[IgnoreOptionId.DotFolders] = true
				},
				SelectedPaths: ["src/App.cs"]));
		await using var server = await McpTestServer.StartAsync(project, workspace.Path, live: true);

		var focusCalls = new (string Tool, Dictionary<string, object?> Arguments, string Verb)[]
		{
			("get_tree", new Dictionary<string, object?> { ["format"] = "text" }, "are not listed"),
			("search_project", new Dictionary<string, object?> { ["pattern"] = "focus-marker" }, "were not searched"),
			("analyze", new Dictionary<string, object?>(), "were not measured"),
			("pack_context", new Dictionary<string, object?>(), "were not packed"),
			("related_files", new Dictionary<string, object?> { ["path"] = "src/App.cs" }, "were not traced")
		};
		foreach (var (tool, arguments, verb) in focusCalls)
		{
			var result = await server.CallAsync(tool, arguments);
			Assert.NotEqual(true, result.IsError);
			Assert.Contains(
				$"[Live context] focus: 1 of 3 selectable files; 2 outside the focus {verb}; " +
				"read any of them by name with get_file.",
				AllText(result),
				StringComparison.Ordinal);
			Assert.DoesNotContain(marker, TrustedText(result), StringComparison.Ordinal);
		}

		var named = await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = guide });
		Assert.NotEqual(true, named.IsError);
		Assert.DoesNotContain("focus:", AllText(named), StringComparison.Ordinal);

		var scalar = await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = extensionless });
		Assert.True(scalar.IsError);
		Assert.Contains(
			$"DPX-MCP-PATH-NOT-FOUND: file '{extensionless}' is hidden by the saved window filters; " +
			"change the filters in DevProjex or use a standard-mode server.",
			AllText(scalar),
			StringComparison.Ordinal);
		Assert.DoesNotContain(marker, TrustedText(scalar), StringComparison.Ordinal);

		var batch = await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			["requests"] = new object[]
			{
				new { path = extensionless },
				new { path = dotFolder + "/pipeline.yml" },
				new { path = "missing.txt" },
				new { path = guide }
			}
		});
		var batchText = AllText(batch);
		Assert.NotEqual(true, batch.IsError);
		const string windowReason =
			"hidden by the saved window filters; change the filters in DevProjex or use a standard-mode server";
		Assert.Contains("1.1 — unavailable — " + windowReason, batchText, StringComparison.Ordinal);
		Assert.Contains("2.1 — unavailable — " + windowReason, batchText, StringComparison.Ordinal);
		Assert.Contains("3.1 — unavailable — DPX-MCP-PATH-NOT-FOUND", batchText, StringComparison.Ordinal);
		Assert.Contains("focus-marker guide", batchText, StringComparison.Ordinal);
		Assert.DoesNotContain(marker, TrustedText(batch), StringComparison.Ordinal);
		Assert.DoesNotContain(windowReason, TrustedText(batch), StringComparison.Ordinal);
	}

	[Fact]
	public async Task LiveRefusalForAServerFilterKeepsTheGeneralReason()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		Directory.CreateDirectory(Path.Combine(project, "src"));
		File.WriteAllText(Path.Combine(project, "src", "App.cs"), "class App {}\n");
		File.WriteAllText(Path.Combine(project, "src", ".Hidden.cs"), "class Hidden {}\n");
		new ProjectProfileStore(() => Path.Combine(workspace.Path, "app-data")).SaveProfile(
			project,
			new ProjectSelectionProfile([], [], [], SelectedPaths: ["src"]));
		await using var server = await McpTestServer.StartAsync(
			project,
			workspace.Path,
			exclusions: [ProjectExclusion.DotFiles],
			live: true);

		var scalar = AllText(await server.CallAsync(
			"get_file",
			new Dictionary<string, object?> { ["path"] = "src/.Hidden.cs" }));
		var batch = AllText(await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			["requests"] = new object[] { new { path = "src/.Hidden.cs" }, new { path = "src/App.cs" } }
		}));

		Assert.Contains("is not in the effective project selection", scalar, StringComparison.Ordinal);
		Assert.DoesNotContain("saved window filters", scalar, StringComparison.Ordinal);
		Assert.Contains("1.1 — unavailable — outside effective selection", batch, StringComparison.Ordinal);
		Assert.DoesNotContain("saved window filters", batch, StringComparison.Ordinal);
	}

	[Fact]
	public async Task LiveMissingSelectionExplanationIsSentOncePerSession()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "App.cs"), "class App {}\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path, live: true);
		const string explanation = "[Live context] no window selection saved for this root; using server defaults.";

		var first = AllText(await server.CallAsync("get_tree"));
		var second = AllText(await server.CallAsync("search_project", new Dictionary<string, object?> { ["pattern"] = "App" }));

		Assert.Contains(explanation, first, StringComparison.Ordinal);
		Assert.DoesNotContain(explanation, second, StringComparison.Ordinal);
		Assert.Contains("[Live context] revision 1 · 1 files selected by server defaults", second, StringComparison.Ordinal);
		Assert.DoesNotContain("focus:", second, StringComparison.Ordinal);
	}

	private static string TrustedText(CallToolResult result) =>
		string.Join(
			'\n',
			result.Content.OfType<TextContentBlock>().Select(static block => Regex.Replace(
				block.Text,
				@"<untrusted-data-(?<nonce>[0-9a-f]{24})>\n[\s\S]*?\n</untrusted-data-\k<nonce>>",
				string.Empty)));

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
