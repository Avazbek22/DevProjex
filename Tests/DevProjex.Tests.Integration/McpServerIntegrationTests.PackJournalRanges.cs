using DevProjex.Infrastructure.AgentJournal;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	[Fact]
	public async Task RelatedFilesJournalAttributesProtectionOnlyToReturnedText()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "tsconfig.json"),
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}\n");
		File.WriteAllText(Path.Combine(project, "Main.ts"), $"import value from './{Secret}.js';\n");
		File.WriteAllText(Path.Combine(project, Secret + ".ts"), "export default 1;\n");
		var imports = new StringBuilder();
		for (var index = 0; index < 700; index++)
		{
			var name = $"target{index:D4}-{Secret}";
			File.WriteAllText(Path.Combine(project, name + ".ts"), $"export default {index};\n");
			imports.Append("import value").Append(index).Append(" from './").Append(name).AppendLine(".js';");
		}
		File.WriteAllText(Path.Combine(project, "Large.ts"), imports.ToString());
		var appData = workspace.CreateDirectory("app-data");

		await using (var server = await McpTestServer.StartAsync(project, workspace.Path))
		{
			var inline = await server.CallAsync("related_files", new Dictionary<string, object?>
			{
				["path"] = "Main.ts",
				["direction"] = "dependencies"
			});
			Assert.NotEqual(true, inline.IsError);
			Assert.Contains("DEVPROJEX_REDACTED[", AllText(inline), StringComparison.Ordinal);

			var stored = await server.CallAsync("related_files", new Dictionary<string, object?>
			{
				["path"] = "Large.ts",
				["direction"] = "dependencies"
			});
			var match = Regex.Match(AllText(stored), "Related-files result stored as '([^']+)'");
			Assert.True(match.Success, AllText(stored));
			var page = await server.CallAsync("read_pack", new Dictionary<string, object?>
			{
				["pack_id"] = match.Groups[1].Value
			});
			Assert.NotEqual(true, page.IsError);
			Assert.Contains("DEVPROJEX_REDACTED[", AllText(page), StringComparison.Ordinal);
		}

		using var journal = new AgentJournalStore(() => appData, activeSessionProvider: static () => []);
		var session = Assert.Single(await journal.ListSessionsAsync(
			project,
			cancellationToken: TestContext.Current.CancellationToken));
		var calls = await journal.ReadCallsAsync(session.Id, TestContext.Current.CancellationToken);
		Assert.Equal(["related_files", "related_files", "read_pack"],
			calls.Select(static call => call.Tool));
		Assert.True(calls[0].SecretsMasked > 0);
		Assert.Equal(0, calls[1].SecretsMasked);
		Assert.Empty(calls[1].DeliveredPaths);
		Assert.Equal(0, calls[2].SecretsMasked);
		Assert.Contains(AgentJournalNoticeCodes.Unavailable, calls[2].Notices);
	}

	[Fact]
	public async Task RelatedFilesJournalDeliversOnlyFilesWhoseReferenceTextItQuotes()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "tsconfig.json"),
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}\n");
		File.WriteAllText(Path.Combine(project, "Main.ts"), "import value from './Target.js';\nexport const main = value;\n");
		File.WriteAllText(Path.Combine(project, "Consumer.ts"), "import value from './Target.js';\nexport const used = value;\n");
		File.WriteAllText(Path.Combine(project, "Target.ts"), "export default 1;\n");
		File.WriteAllText(Path.Combine(project, "Lonely.ts"), "export const lonely = 1;\n");
		var imports = new StringBuilder();
		for (var index = 0; index < 700; index++)
		{
			var name = $"stored-dependency-target-{index:D4}";
			File.WriteAllText(Path.Combine(project, name + ".ts"), $"export default {index};\n");
			imports.Append("import value").Append(index).Append(" from './").Append(name).AppendLine(".js';");
		}
		File.WriteAllText(Path.Combine(project, "Large.ts"), imports.ToString());
		var appData = workspace.CreateDirectory("app-data");

		await using (var server = await McpTestServer.StartAsync(project, workspace.Path))
		{
			var lonely = await server.CallAsync("related_files", new Dictionary<string, object?>
			{
				["path"] = "Lonely.ts"
			});
			Assert.NotEqual(true, lonely.IsError);
			Assert.Contains("[No related files]", AllText(lonely), StringComparison.Ordinal);
			var dependencies = await server.CallAsync("related_files", new Dictionary<string, object?>
			{
				["path"] = "Main.ts",
				["direction"] = "dependencies"
			});
			Assert.Contains("Target.ts", AllText(dependencies), StringComparison.Ordinal);
			var dependents = await server.CallAsync("related_files", new Dictionary<string, object?>
			{
				["path"] = "Target.ts",
				["direction"] = "dependents"
			});
			Assert.Contains("Consumer.ts", AllText(dependents), StringComparison.Ordinal);
			var stored = await server.CallAsync("related_files", new Dictionary<string, object?>
			{
				["path"] = "Large.ts",
				["direction"] = "dependencies"
			});
			var match = Regex.Match(AllText(stored), "Related-files result stored as '([^']+)'");
			Assert.True(match.Success, AllText(stored));
			var page = await server.CallAsync("read_pack", new Dictionary<string, object?>
			{
				["pack_id"] = match.Groups[1].Value
			});
			Assert.NotEqual(true, page.IsError);
			Assert.Contains("stored-dependency-target-0000.ts", AllText(page), StringComparison.Ordinal);
		}

		using var journal = new AgentJournalStore(() => appData, activeSessionProvider: static () => []);
		var session = Assert.Single(await journal.ListSessionsAsync(
			project,
			cancellationToken: TestContext.Current.CancellationToken));
		var calls = await journal.ReadCallsAsync(session.Id, TestContext.Current.CancellationToken);
		Assert.Equal(["related_files", "related_files", "related_files", "related_files", "read_pack"],
			calls.Select(static call => call.Tool));
		// An answer with no relations only names its seed, like a get_tree listing.
		Assert.Equal(0, calls[0].FilesDelivered);
		Assert.Empty(calls[0].DeliveredPaths);
		// Dependency evidence quotes the seed's import text; the targets are only named.
		Assert.Equal(["Main.ts"], calls[1].DeliveredPaths);
		// Dependent evidence quotes each dependent's import text; the seed is only named.
		Assert.Equal(["Consumer.ts", "Main.ts"], calls[2].DeliveredPaths.Order(StringComparer.Ordinal));
		Assert.Empty(calls[3].DeliveredPaths);
		Assert.Equal(["Large.ts"], calls[4].DeliveredPaths);
		Assert.All(calls, static call => Assert.Equal(
			call.DeliveredPaths.Count + call.AdditionalDeliveredPaths,
			call.FilesDelivered));
		Assert.Equal(4, session.Totals.FilesDelivered);
	}

	[Fact]
	public async Task TreeOnlyPackJournalDoesNotMarkFileContentDelivered()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "Source.txt"), "source-content-sentinel");
		var appData = workspace.CreateDirectory("app-data");

		await using (var server = await McpTestServer.StartAsync(project, workspace.Path))
		{
			var tree = await server.CallAsync("pack_context", new Dictionary<string, object?>
			{
				["view"] = "tree",
				["format"] = "text"
			});
			Assert.NotEqual(true, tree.IsError);
			Assert.Contains("Source.txt", AllText(tree), StringComparison.Ordinal);
			Assert.DoesNotContain("source-content-sentinel", AllText(tree), StringComparison.Ordinal);

			var content = await server.CallAsync("pack_context", new Dictionary<string, object?>
			{
				["view"] = "content",
				["format"] = "text"
			});
			Assert.NotEqual(true, content.IsError);
			Assert.Contains("source-content-sentinel", AllText(content), StringComparison.Ordinal);
		}

		using var journal = new AgentJournalStore(
			() => appData,
			activeSessionProvider: static () => []);
		var session = Assert.Single(await journal.ListSessionsAsync(
			project,
			cancellationToken: TestContext.Current.CancellationToken));
		var calls = (await journal.ReadCallsAsync(session.Id, TestContext.Current.CancellationToken))
			.Where(static call => call.Tool == "pack_context")
			.ToArray();
		Assert.Equal(2, calls.Length);
		Assert.Empty(calls[0].DeliveredPaths);
		Assert.Equal(0, calls[0].FilesDelivered);
		Assert.Equal(["Source.txt"], calls[1].DeliveredPaths);
		Assert.Equal(1, calls[1].FilesDelivered);
	}

	[Fact]
	public async Task ReadPackJournalAttributesACharacterLimitedSingleLinePage()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = workspace.CreateDirectory("project/src");
		File.WriteAllText(Path.Combine(source, "Large.txt"), new string('x', 60_000));
		var appData = workspace.CreateDirectory("app-data");

		await using (var server = await McpTestServer.StartAsync(project, workspace.Path))
		{
			var stored = await server.CallAsync(
				"pack_context",
				new Dictionary<string, object?>
				{
					["paths"] = new[] { "src/Large.txt" },
					["view"] = "content",
					["format"] = "text"
				});
			var packId = ExtractPackId(AllText(stored));
			Assert.NotEmpty(packId);
			var page = await server.CallAsync(
				"read_pack",
				new Dictionary<string, object?> { ["pack_id"] = packId });
			Assert.NotEqual(true, page.IsError);
		}

		using var journal = new AgentJournalStore(
			() => appData,
			activeSessionProvider: static () => []);
		var session = Assert.Single(await journal.ListSessionsAsync(
			project,
			cancellationToken: TestContext.Current.CancellationToken));
		var calls = await journal.ReadCallsAsync(session.Id, TestContext.Current.CancellationToken);
		var read = Assert.Single(calls, static call => call.Tool == "read_pack");
		Assert.Equal(["src/Large.txt"], read.DeliveredPaths);
	}

	[Fact]
	public async Task ReadPackJournalUsesStoredLineRangesInsteadOfPathTextOccurrences()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = workspace.CreateDirectory("project/src");
		var firstPath = Path.Combine(source, "A.txt");
		var secondPath = Path.Combine(source, "B.txt");
		File.WriteAllText(
			firstPath,
			string.Join('\n', Enumerable.Range(1, 1_100).Select(index =>
				index == 10
					? "the text src/B.txt is data, not a delivered file"
					: $"first-file-line-{index:D4}-{new string('a', 48)}")));
		File.WriteAllText(
			secondPath,
			string.Join('\n', Enumerable.Range(1, 100).Select(index =>
				$"second-file-line-{index:D4}-{new string('b', 48)}")));
		var appData = workspace.CreateDirectory("app-data");

		await using (var server = await McpTestServer.StartAsync(project, workspace.Path))
		{
			var stored = await server.CallAsync(
				"pack_context",
				new Dictionary<string, object?>
				{
					["paths"] = new[] { "src/A.txt", "src/B.txt" },
					["view"] = "content",
					["format"] = "text"
				});
			var packId = ExtractPackId(AllText(stored));
			Assert.NotEmpty(packId);
			var page = await server.CallAsync(
				"read_pack",
				new Dictionary<string, object?> { ["pack_id"] = packId });
			Assert.NotEqual(true, page.IsError);
			Assert.Contains("first-file-line-0001", AllText(page), StringComparison.Ordinal);
			Assert.DoesNotContain("second-file-line-0001", AllText(page), StringComparison.Ordinal);
		}

		using var journal = new AgentJournalStore(
			() => appData,
			activeSessionProvider: static () => []);
		var session = Assert.Single(await journal.ListSessionsAsync(
			project,
			cancellationToken: TestContext.Current.CancellationToken));
		var calls = await journal.ReadCallsAsync(session.Id, TestContext.Current.CancellationToken);
		var read = Assert.Single(calls, static call => call.Tool == "read_pack");
		Assert.Equal(["src/A.txt"], read.DeliveredPaths);
	}
}
