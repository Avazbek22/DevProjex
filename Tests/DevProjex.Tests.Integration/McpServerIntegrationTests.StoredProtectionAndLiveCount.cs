using System.Text;
using DevProjex.Mcp;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task StandardLocalPackRejectsProtectionChangesBeforeRead(bool hidePrivateData)
	{
		const string markedValue = "manual-protection-value";
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var appData = Path.Combine(workspace.Path, "app-data");
		var content = markedValue + " " + PrivateEmail + "\n" +
			string.Join('\n', Enumerable.Range(1, 2_000).Select(static line =>
				$"pack-line-{line:D4}-{new string('x', 24)}"));
		File.WriteAllText(Path.Combine(project, "Large.txt"), content);
		var store = new ProjectProfileStore(() => appData);
		store.SaveProfile(project, new ProjectSelectionProfile([], [], []));
		await using var server = await McpTestServer.StartAsync(project, workspace.Path, hidePrivateData: hidePrivateData);

		var stored = await server.CallAsync("pack_context", new Dictionary<string, object?>
		{
			["profile"] = "local",
			["paths"] = new[] { "Large.txt" },
			["view"] = "content",
			["format"] = "text"
		});
		Assert.NotEqual(true, stored.IsError);
		var packId = ExtractPackId(AllText(stored));
		await AddPersistentMarkAsync(store, appData, project, "Large.txt", 0, markedValue);

		var page = await server.CallAsync("read_pack", new Dictionary<string, object?> { ["pack_id"] = packId });
		var text = AllText(page);

		Assert.True(page.IsError);
		Assert.StartsWith("DPX-MCP-STORED-PROTECTION-CHANGED: request failed.", text, StringComparison.Ordinal);
		Assert.DoesNotContain("pack-line-", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task StandardLocalRelatedFilesPackRejectsManualProtectionChange()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var appData = Path.Combine(workspace.Path, "app-data");
		File.WriteAllText(Path.Combine(project, "tsconfig.json"),
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}\n");
		var imports = new StringBuilder();
		for (var index = 0; index < 700; index++)
		{
			var name = $"target{index:D4}";
			File.WriteAllText(Path.Combine(project, name + ".ts"), $"export default {index};\n");
			imports.Append("import ").Append(name).Append(" from './").Append(name).AppendLine(".js';");
		}
		var source = imports.ToString();
		File.WriteAllText(Path.Combine(project, "Main.ts"), source);
		var store = new ProjectProfileStore(() => appData);
		store.SaveProfile(project, new ProjectSelectionProfile([], [".ts"], []));
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var stored = await server.CallAsync("related_files", new Dictionary<string, object?>
		{
			["profile"] = "local",
			["path"] = "Main.ts",
			["direction"] = "dependencies"
		});
		Assert.NotEqual(true, stored.IsError);
		var match = Regex.Match(AllText(stored), "Related-files result stored as '([^']+)'", RegexOptions.CultureInvariant);
		Assert.True(match.Success, AllText(stored));
		var packId = match.Groups[1].Value;
		await AddPersistentMarkAsync(store, appData, project, "Main.ts", source.IndexOf("target0000", StringComparison.Ordinal), "target0000");

		var page = await server.CallAsync("read_pack", new Dictionary<string, object?> { ["pack_id"] = packId });
		var text = AllText(page);

		Assert.True(page.IsError);
		Assert.StartsWith("DPX-MCP-STORED-PROTECTION-CHANGED: request failed.", text, StringComparison.Ordinal);
		Assert.DoesNotContain("target0000.ts", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task StandardLocalPackFailsClosedWhenSavedProtectionCannotBeRead()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var appData = Path.Combine(workspace.Path, "app-data");
		File.WriteAllText(Path.Combine(project, "Large.txt"), new string('x', 60_000));
		var store = new ProjectProfileStore(() => appData);
		store.SaveProfile(project, new ProjectSelectionProfile([], [], []));
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var stored = await server.CallAsync("pack_context", new Dictionary<string, object?>
		{
			["profile"] = "local",
			["paths"] = new[] { "Large.txt" },
			["view"] = "content",
			["format"] = "text"
		});
		Assert.NotEqual(true, stored.IsError);
		var packId = ExtractPackId(AllText(stored));
		Assert.True(store.TryDeleteProfile(project));

		var page = await server.CallAsync("read_pack", new Dictionary<string, object?> { ["pack_id"] = packId });
		var text = AllText(page);

		Assert.True(page.IsError);
		Assert.StartsWith("DPX-MCP-STORED-PROTECTION-UNAVAILABLE: request failed.", text, StringComparison.Ordinal);
		Assert.DoesNotContain(new string('x', 100), text, StringComparison.Ordinal);
	}

	[Fact(Timeout = 15_000)]
	public async Task LiveListProjectsRefreshesSelectedFileCountAfterRootChange()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var selected = workspace.CreateDirectory("project/src");
		File.WriteAllText(Path.Combine(selected, "First.cs"), "class First;\n");
		var store = new ProjectProfileStore(() => Path.Combine(workspace.Path, "app-data"));
		store.SaveProfile(project, new ProjectSelectionProfile([], [], [], SelectedPaths: ["src"]));
		await using var server = await McpTestServer.StartAsync(project, workspace.Path, live: true);

		var initial = await server.CallAsync("list_projects");
		Assert.Contains("1 files selected in the window", AllText(initial), StringComparison.Ordinal);
		File.WriteAllText(Path.Combine(selected, "Second.cs"), "class Second;\n");

		var refreshed = string.Empty;
		var deadline = Stopwatch.StartNew();
		while (deadline.Elapsed < TimeSpan.FromSeconds(5))
		{
			refreshed = AllText(await server.CallAsync("list_projects"));
			if (refreshed.Contains("2 files selected in the window", StringComparison.Ordinal))
				break;
			await Task.Delay(50, TestContext.Current.CancellationToken);
		}

		Assert.Contains("2 files selected in the window", refreshed, StringComparison.Ordinal);
	}
}
