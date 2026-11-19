using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	[Fact]
	public async Task OutlinesSearchContextAndStoredPacksUseProtectedTextAndKeepTheJournalClean()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		const string manual = "manually-private-heading-8173";
		const string address = "alice.smith@company.io";
		var content = "# Section " + manual + "\r\n## Section " + address + "\r\n## Section token=" + Secret + "\r\n" +
			string.Concat(Enumerable.Range(0, 700).Select(index => $"Paragraph {index} with ordinary words and enough padding to create a long document.\r\n"));
		WriteProjectFile(project, "guide.md", content);
		var store = new ProjectProfileStore(() => Path.Combine(workspace.Path, "app-data"));
		store.SaveProfile(project, new ProjectSelectionProfile([], [], [], SelectedPaths: null));
		Assert.True(MarkedSecretValueNormalizer.TryCreate(manual, out var normalized, out _));
		Assert.True((await store.AddMarkAsync(project, new MarkedSecretProfileEntry(normalized.Hash, null, normalized.Length),
			TestContext.Current.CancellationToken)).Succeeded);
		await using (var server = await McpTestServer.StartAsync(project, workspace.Path, hidePrivateData: true, live: true))
		{
			var file = AllText(await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "guide.md" }));
			Assert.Contains("Structure:", file, StringComparison.Ordinal);
			Assert.Contains("DEVPROJEX_REDACTED[", file, StringComparison.Ordinal);
			var search = AllText(await server.CallAsync("search_project", new Dictionary<string, object?>
			{
				["pattern"] = "Section",
				["context_lines"] = 2
			}));
			Assert.Contains("Section", search, StringComparison.Ordinal);
			var packed = await server.CallAsync("pack_context", new Dictionary<string, object?> { ["view"] = "content" });
			Assert.NotEqual(true, packed.IsError);
			var pack = AllText(packed);
			var id = Regex.Match(pack, "'(?<id>[a-f0-9]{48})'").Groups["id"].Value;
			Assert.NotEmpty(id);
			var page = AllText(await server.CallAsync("read_pack", new Dictionary<string, object?> { ["pack_id"] = id }));
			foreach (var response in new[] { file, search, pack, page })
			{
				foreach (var value in new[] { manual, address, Secret })
					Assert.DoesNotContain(value, response, StringComparison.Ordinal);
				Assert.DoesNotContain("\r", response, StringComparison.Ordinal);
			}
		}
		var journals = Directory.GetFiles(Path.Combine(workspace.Path, "app-data"), "*.jsonl", SearchOption.AllDirectories);
		Assert.NotEmpty(journals);
		foreach (var journal in journals)
		{
			var text = await File.ReadAllTextAsync(journal, TestContext.Current.CancellationToken);
			foreach (var value in new[] { manual, address, Secret })
				Assert.DoesNotContain(value, text, StringComparison.Ordinal);
		}
	}
}
