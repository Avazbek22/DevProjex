using DevProjex.Infrastructure.AgentJournal;
using DevProjex.Infrastructure.ProjectProfiles;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task PublishedServerRecordsEveryToolInStandardAndLiveModes(bool live)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var dataRoot = workspace.CreateDirectory("data");
		workspace.CreateDirectory("project/src");
		workspace.WriteFile("project/src/Program.cs", "class Program { Model Value = new(); }\n");
		workspace.WriteFile("project/src/Model.cs", "class Model { }\n");
		workspace.WriteFile("project/src/Large.txt", new string('x', 60_000));
		workspace.WriteFile("project/Outside.cs", $"class Outside {{ string Token = \"{Secret}\"; }}\n");
		if (live)
		{
			new ProjectProfileStore(() => dataRoot).SaveProfile(
				project,
				new ProjectSelectionProfile([], [], [], SelectedPaths: ["src"]));
		}
		var arguments = live ? new[] { "--live" } : Array.Empty<string>();

		await using (var server = await ActualMcpProcess.StartAsync(
			project,
			dataRoot,
			arguments,
			clientInfo: new Implementation { Name = "journal-process-test", Version = "1.0" }))
		{
			await AssertSuccessful(server, "list_projects");
			await AssertSuccessful(server, "get_tree", new Dictionary<string, object?> { ["format"] = "text" });
			await AssertSuccessful(server, "analyze");
			var pack = await server.Client.CallToolAsync(
				"pack_context",
				new Dictionary<string, object?>
				{
					["paths"] = new[] { "src/Large.txt" },
					["view"] = "content",
					["format"] = "text"
				},
				progress: null,
				options: null,
				TestContext.Current.CancellationToken);
			Assert.NotEqual(true, pack.IsError);
			await AssertSuccessful(
				server,
				"read_pack",
				new Dictionary<string, object?> { ["pack_id"] = ExtractPackId(AllProcessText(pack)) });
			await AssertSuccessful(
				server,
				"search_project",
				new Dictionary<string, object?> { ["pattern"] = "Model" });
			await AssertSuccessful(
				server,
				"related_files",
				new Dictionary<string, object?> { ["path"] = "src/Program.cs" });
			await AssertSuccessful(
				server,
				"get_file",
				new Dictionary<string, object?> { ["path"] = "Outside.cs" });
		}

		using var journal = new AgentJournalStore(() => dataRoot, activeSessionProvider: static () => []);
		var session = Assert.Single(await journal.ListSessionsAsync(
			project,
			cancellationToken: TestContext.Current.CancellationToken));
		var calls = await journal.ReadCallsAsync(session.Id, TestContext.Current.CancellationToken);

		Assert.Equal(live ? AgentJournalMode.Live : AgentJournalMode.Standard, session.Mode);
		Assert.Equal(ExpectedTools, calls.Select(static call => call.Tool));
		Assert.Equal(8, session.Totals.Calls);
		Assert.True(session.Totals.ResultCharacters > 0);
		Assert.True(session.Totals.EstimatedTokens > 0);
		Assert.DoesNotContain(calls, call => call.Tool == "pack_context" && call.DeliveredPaths.Contains("src/Large.txt"));
		Assert.Contains(calls, call => call.Tool == "read_pack" && call.DeliveredPaths.Contains("src/Large.txt"));
		Assert.DoesNotContain(
			AgentJournalNoticeCodes.StalePack,
			calls.Single(call => call.Tool == "read_pack").Notices);
		Assert.Contains(calls, call => call.Tool == "search_project" && call.DeliveredPaths.Contains("src/Program.cs"));
		Assert.Contains(calls, call => call.Tool == "get_file" && call.DeliveredPaths.Contains("Outside.cs"));
		Assert.Equal(
			live,
			calls.Single(call => call.Tool == "get_file").Notices.Contains(AgentJournalNoticeCodes.OutsideSelection));
		Assert.True(calls.Sum(static call => call.SecretsMasked) > 0);
	}

	[Fact]
	public async Task PublishedJournalCountsOnlyReturnedFilesRangesAndExplicitNotices()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var dataRoot = workspace.CreateDirectory("data");
		workspace.CreateDirectory("project/src");
		workspace.WriteFile("project/src/A.cs", "namespace P; class A { int Good() => 1; }\n");
		workspace.WriteFile("project/src/B.cs", "namespace P; class B { int Other() => 2; }\n");
		workspace.WriteFile(
			"project/src/Secret.cs",
			string.Join('\n', Enumerable.Range(1, 99).Select(static index => $"line {index}")) +
			$"\nconst string Token = \"{Secret}\";\n");
		workspace.WriteFile("project/src/Matches.txt", "needle one\nneedle two\n");

		await using (var server = await ActualMcpProcess.StartAsync(
			project,
			dataRoot,
			clientInfo: new Implementation { Name = "journal-render-test", Version = "1.0" }))
		{
			var unknown = await server.Client.CallToolAsync(
				"get_file",
				new Dictionary<string, object?> { ["path"] = "src/A.cs", ["symbol"] = "Missing" },
				progress: null,
				options: null,
				TestContext.Current.CancellationToken);
			Assert.True(unknown.IsError);
			await AssertSuccessful(
				server,
				"get_file",
				new Dictionary<string, object?>
				{
					["requests"] = new object[]
					{
						new { path = "src/A.cs", symbol = "P.A.Good" },
						new { path = "src/B.cs", symbol = "Missing" }
					}
				});
			await AssertSuccessful(
				server,
				"get_file",
				new Dictionary<string, object?>
				{
					["path"] = "src/Secret.cs",
					["start_line"] = 1,
					["end_line"] = 3
				});
			await AssertSuccessful(
				server,
				"search_project",
				new Dictionary<string, object?> { ["pattern"] = "needle", ["max_results"] = 1 });
		}

		using var journal = new AgentJournalStore(() => dataRoot, activeSessionProvider: static () => []);
		var session = Assert.Single(await journal.ListSessionsAsync(
			project,
			cancellationToken: TestContext.Current.CancellationToken));
		var calls = await journal.ReadCallsAsync(session.Id, TestContext.Current.CancellationToken);
		var reads = calls.Where(static call => call.Tool == "get_file").OrderBy(static call => call.Sequence).ToArray();

		Assert.Empty(reads[0].DeliveredPaths);
		Assert.Equal(["src/A.cs"], reads[1].DeliveredPaths);
		Assert.Equal(0, reads[2].SecretsMasked);
		var search = Assert.Single(calls, static call => call.Tool == "search_project");
		Assert.Contains(AgentJournalNoticeCodes.SearchPartial, search.Notices);
		Assert.Contains(AgentJournalNoticeCodes.MatchesOmitted, search.Notices);
	}

	private static async Task AssertSuccessful(
		ActualMcpProcess server,
		string tool,
		IReadOnlyDictionary<string, object?>? arguments = null)
	{
		var result = await server.Client.CallToolAsync(
			tool,
			arguments ?? new Dictionary<string, object?>(),
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		Assert.NotEqual(true, result.IsError);
	}
}
