using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	private const string UnchangedServiceNotice = "[Unchanged] filters, protection; see list_projects.";
	private const string UnchangedFiltersServiceNotice = "[Unchanged] filters; see list_projects.";
	private const string UnchangedProtectionServiceNotice = "[Unchanged] protection; see list_projects.";

	[Fact]
	public async Task RealProcessSendsServiceNoticesOnceWhileTheyKeepSayingTheSameThing()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("notice-project");
		workspace.WriteFile("notice-project/src/Alpha.ts", "export const alpha = 1\n");
		workspace.WriteFile("notice-project/src/Beta.ts", "export const beta = 2\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var texts = new List<string>();
		var listed = await CallAsync(server, "list_projects", new Dictionary<string, object?>());
		texts.Add(AllProcessText(listed));
		for (var call = 0; call < 3; call++)
		{
			texts.Add(AllProcessText(await CallAsync(
				server,
				"get_tree",
				new Dictionary<string, object?> { ["format"] = "text" })));
			texts.Add(AllProcessText(await CallAsync(
				server,
				"search_project",
				new Dictionary<string, object?> { ["pattern"] = "alpha", ["context_lines"] = 0 })));
			texts.Add(AllProcessText(await CallAsync(
				server,
				"get_file",
				new Dictionary<string, object?> { ["path"] = "src/Alpha.ts" })));
		}

		Assert.Equal(10, texts.Count);
		Assert.Single(texts, static text => text.Contains("[Effective filters]", StringComparison.Ordinal));
		Assert.Single(texts, static text => text.Contains("[Protection]", StringComparison.Ordinal));
		Assert.Equal(8, texts.Count(HasContinuation));
		// Two of the eight are tree calls, which carry both lines. The six content reads carry no
		// filters line at all, so their continuation names only the line they actually withheld.
		Assert.Equal(
			2,
			texts.Count(static text => text.Contains(UnchangedServiceNotice, StringComparison.Ordinal)));
		Assert.Equal(
			6,
			texts.Count(static text =>
				text.Contains(UnchangedProtectionServiceNotice, StringComparison.Ordinal)));
		// list_projects is the orientation call and always answers with the whole baseline.
		Assert.False(HasContinuation(texts[0]));
		Assert.Contains("\"exclusions\"", texts[0], StringComparison.Ordinal);
		Assert.Contains("\"protection\"", texts[0], StringComparison.Ordinal);
		// The first response that carries them carries both in full.
		Assert.Contains("[Effective filters]", texts[1], StringComparison.Ordinal);
		Assert.Contains("[Protection]", texts[1], StringComparison.Ordinal);
		Assert.False(HasContinuation(texts[1]));
	}

	[Fact]
	public async Task RealProcessRepeatsTheDetailMixOnEveryCallThatProducedOne()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("detail-project");
		workspace.WriteFile(
			"detail-project/src/Alpha.cs",
			"namespace P;\n\npublic static class Alpha\n{\n\n	public static int Run() => 1;\n}\n");
		workspace.WriteFile(
			"detail-project/src/Beta.cs",
			"namespace P;\n\npublic static class Beta\n{\n\n	public static int Run() => 2;\n}\n");
		workspace.WriteFile("detail-project/docs/Notes.md", "# Notes\n\nProse.\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		// The same arguments every time, so the filters and protection lines stay byte-equal and the
		// memo is free to collapse them. Nothing here sets alwaysSend: no max_file_bytes, and the
		// selection is not empty.
		var arguments = new Dictionary<string, object?>
		{
			["view"] = "content",
			["format"] = "text",
			["detail"] = "compact",
			["detail_by_pattern"] = new object[]
			{
				new Dictionary<string, object?>
				{
					["patterns"] = new[] { "src/Alpha.cs" },
					["detail"] = "signatures"
				}
			}
		};

		var texts = new List<string>();
		for (var call = 0; call < 4; call++)
			texts.Add(AllProcessText(await CallAsync(server, "pack_context", arguments)));

		// The mix reports what THIS call did, so it is never memoised: it is on every response.
		Assert.All(
			texts,
			static text => Assert.Contains("[Detail] full", text, StringComparison.Ordinal));

		// Meanwhile the session-state lines behave exactly as they do without a detail mix: sent once,
		// then replaced by the continuation line.
		Assert.Single(texts, static text => text.Contains("[Effective filters]", StringComparison.Ordinal));
		Assert.Single(texts, static text => text.Contains("[Protection]", StringComparison.Ordinal));
		Assert.Contains("[Effective filters]", texts[0], StringComparison.Ordinal);
		Assert.False(HasContinuation(texts[0]));
		Assert.Equal(
			3,
			texts.Count(static text => text.Contains(UnchangedServiceNotice, StringComparison.Ordinal)));
	}

	[Fact]
	public async Task RealProcessRepeatsServiceNoticesWhenTheirContentChanges()
	{
		using var workspace = new TemporaryDirectory();
		var first = workspace.CreateDirectory("first-project");
		var second = workspace.CreateDirectory("second-project");
		workspace.WriteFile("first-project/src/Alpha.ts", "export const alpha = 1\n");
		workspace.WriteFile("first-project/.hidden/Note.md", "note\n");
		workspace.WriteFile("second-project/src/Gamma.ts", "export const gamma = 3\n");
		await using var server = await ActualMcpProcess.StartAsync(
			first,
			workspace.CreateDirectory("data"),
			["--root", second, "--allow-agent-exclusions"]);

		var baseline = AllProcessText(await CallAsync(
			server,
			"get_tree",
			new Dictionary<string, object?> { ["project"] = first, ["format"] = "text" }));
		var repeated = AllProcessText(await CallAsync(
			server,
			"get_tree",
			new Dictionary<string, object?> { ["project"] = first, ["format"] = "text" }));
		var changedExclusions = AllProcessText(await CallAsync(
			server,
			"get_tree",
			new Dictionary<string, object?>
			{
				["project"] = first,
				["format"] = "text",
				["exclusions"] = new[] { "dot-folders" }
			}));
		var changedProject = AllProcessText(await CallAsync(
			server,
			"get_tree",
			new Dictionary<string, object?> { ["project"] = second, ["format"] = "text" }));
		var changedProfile = AllProcessText(await CallAsync(
			server,
			"pack_context",
			new Dictionary<string, object?>
			{
				["project"] = second,
				["profile"] = "standard",
				["view"] = "tree",
				["format"] = "text"
			}));

		Assert.Contains("[Effective filters]", baseline, StringComparison.Ordinal);
		Assert.Contains(UnchangedServiceNotice, repeated, StringComparison.Ordinal);
		Assert.Contains("[Effective filters]", changedExclusions, StringComparison.Ordinal);
		Assert.DoesNotContain(UnchangedServiceNotice, changedExclusions, StringComparison.Ordinal);
		Assert.Contains("[Effective filters]", changedProject, StringComparison.Ordinal);
		Assert.DoesNotContain(UnchangedServiceNotice, changedProject, StringComparison.Ordinal);
		Assert.Contains("[Effective filters]", changedProfile, StringComparison.Ordinal);
		Assert.DoesNotContain(UnchangedServiceNotice, changedProfile, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessStartsEverySessionWithTheFullServiceNotices()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("session-project");
		workspace.WriteFile("session-project/src/Alpha.ts", "export const alpha = 1\n");
		var dataRoot = workspace.CreateDirectory("data");

		string firstResponse;
		await using (var first = await ActualMcpProcess.StartAsync(project, dataRoot))
		{
			await CallAsync(first, "get_tree", new Dictionary<string, object?> { ["format"] = "text" });
			firstResponse = AllProcessText(await CallAsync(
				first,
				"get_file",
				new Dictionary<string, object?> { ["path"] = "src/Alpha.ts" }));
		}

		await using var second = await ActualMcpProcess.StartAsync(project, dataRoot);
		var freshResponse = AllProcessText(await CallAsync(
			second,
			"get_file",
			new Dictionary<string, object?> { ["path"] = "src/Alpha.ts" }));

		Assert.Contains(UnchangedProtectionServiceNotice, firstResponse, StringComparison.Ordinal);
		Assert.Contains("[Protection]", freshResponse, StringComparison.Ordinal);
		Assert.False(HasContinuation(freshResponse));
	}

	[Fact]
	public async Task RealProcessKeepsExplainingAnEmptySelectionEveryTime()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("empty-project");
		workspace.WriteFile("empty-project/src/Alpha.ts", "export const alpha = 1\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		await CallAsync(server, "get_tree", new Dictionary<string, object?> { ["format"] = "text" });
		var empty = AllProcessText(await CallAsync(
			server,
			"get_tree",
			new Dictionary<string, object?>
			{
				["format"] = "text",
				["include_patterns"] = new[] { "**/*.nothing" }
			}));

		// A response that has to explain why it is empty always names the filters that emptied it.
		Assert.Contains("[Empty selection]", empty, StringComparison.Ordinal);
		Assert.Contains("[Effective filters]", empty, StringComparison.Ordinal);
		Assert.False(HasContinuation(empty));
	}

	[Fact]
	public async Task RealProcessRepeatsServiceNoticesAfterAResponseThatCouldNotCarryThem()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("stored-project");
		workspace.WriteFile("stored-project/target.ts", "export const target = 1;\n");
		workspace.WriteFile(
			"stored-project/tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}\n");
		for (var index = 0; index < 700; index++)
		{
			workspace.WriteFile(
				$"stored-project/related/very-long-related-target-{index:D4}.ts",
				$"import target from '../target.js'; export const value{index} = target;\n");
		}
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		// The dependency answer overflows into a stored pack, so the response body is the
		// pack pointer and carries none of the service notices.
		var stored = AllProcessText(await CallAsync(
			server,
			"related_files",
			new Dictionary<string, object?> { ["path"] = "target.ts", ["direction"] = "dependents" }));
		var next = AllProcessText(await CallAsync(
			server,
			"get_tree",
			new Dictionary<string, object?> { ["format"] = "text", ["max_depth"] = 1 }));

		Assert.Contains("Related-files result stored as", stored, StringComparison.Ordinal);
		Assert.DoesNotContain("[Protection]", stored, StringComparison.Ordinal);
		Assert.False(HasContinuation(next));
		Assert.Contains("[Effective filters]", next, StringComparison.Ordinal);
		Assert.Contains("[Protection]", next, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessRepeatsServiceNoticesAfterAFailedCall()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("failing-project");
		workspace.WriteFile("failing-project/src/Alpha.ts", "export const alpha = 1\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var failed = await CallAsync(
			server,
			"get_file",
			new Dictionary<string, object?> { ["path"] = "src/Missing.ts" });
		var next = AllProcessText(await CallAsync(
			server,
			"get_file",
			new Dictionary<string, object?> { ["path"] = "src/Alpha.ts" }));

		Assert.True(failed.IsError);
		Assert.False(HasContinuation(next));
		Assert.Contains("[Protection]", next, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessNeverReportsAnUnchangedProtectionLineInAnAnalyzeOnlySession()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("analyze-only-project");
		workspace.WriteFile("analyze-only-project/src/Alpha.ts", "export const alpha = 1\n");
		workspace.WriteFile("analyze-only-project/src/Beta.ts", "export const beta = 2\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var texts = new List<string>();
		for (var call = 0; call < 4; call++)
		{
			texts.Add(AllProcessText(await CallAsync(
				server,
				"analyze",
				new Dictionary<string, object?>())));
		}

		// analyze reports no protection line on any call, so no response of this session may say a
		// protection line is unchanged: the session has never carried one.
		Assert.Equal(4, texts.Count);
		Assert.All(
			texts,
			static text => Assert.DoesNotContain("[Protection]", text, StringComparison.Ordinal));
		Assert.All(texts, static text => Assert.False(HasContinuation(text)));
	}

	[Fact]
	public async Task RealProcessNamesOnlyTheProtectionLineWhenTheSessionNeverCarriedFilters()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("reads-only-project");
		workspace.WriteFile("reads-only-project/src/Alpha.ts", "export const alpha = 1\n");
		workspace.WriteFile("reads-only-project/src/Beta.ts", "export const beta = 2\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		// Neither tool reports an effective-filters line on a selection that is not empty, so this
		// session never carries one.
		var first = AllProcessText(await CallAsync(
			server,
			"get_file",
			new Dictionary<string, object?> { ["path"] = "src/Alpha.ts" }));
		var second = AllProcessText(await CallAsync(
			server,
			"get_file",
			new Dictionary<string, object?> { ["path"] = "src/Beta.ts" }));
		var third = AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?> { ["pattern"] = "alpha", ["context_lines"] = 0 }));

		Assert.Contains("[Protection]", first, StringComparison.Ordinal);
		Assert.DoesNotContain("[Effective filters]", first, StringComparison.Ordinal);
		Assert.False(HasContinuation(first));

		foreach (var text in new[] { second, third })
		{
			Assert.Contains(UnchangedProtectionServiceNotice, text, StringComparison.Ordinal);
			Assert.DoesNotContain(UnchangedServiceNotice, text, StringComparison.Ordinal);
			Assert.DoesNotContain(UnchangedFiltersServiceNotice, text, StringComparison.Ordinal);
			Assert.DoesNotContain("[Effective filters]", text, StringComparison.Ordinal);
		}
	}

	[Fact]
	public async Task RealProcessNamesTheLinesEachResponseActuallyWithholds()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("mixed-project");
		workspace.WriteFile("mixed-project/src/Alpha.ts", "export const alpha = 1\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var tree = AllProcessText(await CallAsync(
			server,
			"get_tree",
			new Dictionary<string, object?> { ["format"] = "text" }));
		var read = AllProcessText(await CallAsync(
			server,
			"get_file",
			new Dictionary<string, object?> { ["path"] = "src/Alpha.ts" }));
		var repeatedTree = AllProcessText(await CallAsync(
			server,
			"get_tree",
			new Dictionary<string, object?> { ["format"] = "text" }));

		// The tree carries both lines, so it is the one response that can withhold both later.
		Assert.Contains("[Effective filters]", tree, StringComparison.Ordinal);
		Assert.Contains("[Protection]", tree, StringComparison.Ordinal);
		Assert.False(HasContinuation(tree));

		// The read would have carried only a protection line, so that is all it can stand in for,
		// even though the session has by now reported an effective-filters line as well.
		Assert.Contains(UnchangedProtectionServiceNotice, read, StringComparison.Ordinal);
		Assert.DoesNotContain(UnchangedServiceNotice, read, StringComparison.Ordinal);

		Assert.Contains(UnchangedServiceNotice, repeatedTree, StringComparison.Ordinal);
		Assert.DoesNotContain(UnchangedProtectionServiceNotice, repeatedTree, StringComparison.Ordinal);
	}

	private static bool HasContinuation(string text) =>
		text.Contains(UnchangedServiceNotice, StringComparison.Ordinal) ||
		text.Contains(UnchangedFiltersServiceNotice, StringComparison.Ordinal) ||
		text.Contains(UnchangedProtectionServiceNotice, StringComparison.Ordinal);

	private static ValueTask<CallToolResult> CallAsync(
		ActualMcpProcess server,
		string tool,
		Dictionary<string, object?> arguments) =>
		server.Client.CallToolAsync(
			tool,
			arguments,
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
}
