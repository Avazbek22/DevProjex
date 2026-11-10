using DevProjex.Infrastructure.ProjectProfiles;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	[Fact]
	public async Task PackageExportMetadataHonorsManualMarksOutsideManifestContent()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		const string marked = "manually-protected-package-9472";
		var source = marked + "/src/entry.ts";
		WriteProjectFile(project, source, "export const entry = 1;\n");
		WriteProjectFile(project, marked + "/package.json", """{"exports":{".":"./src/entry.ts"}}""");
		WriteProjectFile(project, "mark.txt", marked);
		var appData = Path.Combine(workspace.Path, "app-data");
		var store = new ProjectProfileStore(() => appData);
		store.SaveProfile(project, new ProjectSelectionProfile([], [], [], SelectedPaths: null));
		await AddGlobalExportMarkAsync(store, project, marked);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path, live: true);

		var markRead = await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "mark.txt" });
		Assert.DoesNotContain(marked, AllText(markRead), StringComparison.Ordinal);
		var whole = await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = source });
		Assert.NotEqual(true, whole.IsError);
		Assert.Contains("export const entry = 1", AllText(whole), StringComparison.Ordinal);
		Assert.DoesNotContain("Exported by:", AllText(whole), StringComparison.Ordinal);
		var dependents = await server.CallAsync("related_files", new Dictionary<string, object?>
		{
			["path"] = source,
			["direction"] = "dependents"
		});
		Assert.NotEqual(true, dependents.IsError);
		Assert.DoesNotContain("package export", AllText(dependents), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("automatic")]
	[InlineData("manual")]
	[InlineData("source-bound")]
	[InlineData("private")]
	public async Task ProtectedManifestContentCannotProducePackageExportMetadata(string protection)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var manual = protection is "manual" or "source-bound";
		var protectedValue = manual ? "manually-protected-export-9472" : protection == "private" ? "alice.smith@company.io" : Secret;
		WriteProjectFile(project, "src/entry.ts", "export const entry = 1;\n");
		var manifestName = protection == "private" ? "jsr.json" : "package.json";
		WriteProjectFile(project, manifestName, System.Text.Json.JsonSerializer.Serialize(new
		{
			exports = new Dictionary<string, string> { ["./" + protectedValue] = "./src/entry.ts" }
		}));
		if (manual)
		{
			WriteProjectFile(project, "mark.txt", protectedValue);
			var appData = Path.Combine(workspace.Path, "app-data");
			var store = new ProjectProfileStore(() => appData);
			store.SaveProfile(project, new ProjectSelectionProfile([], [], [], SelectedPaths: null));
			if (protection == "source-bound")
			{
				var manifest = File.ReadAllText(Path.Combine(project, "package.json"));
				await AddPersistentMarkAsync(store, appData, project, "package.json", manifest.IndexOf(protectedValue, StringComparison.Ordinal), protectedValue);
			}
			else
				await AddGlobalExportMarkAsync(store, project, protectedValue);
		}
		await using var server = await McpTestServer.StartAsync(project, workspace.Path, hidePrivateData: protection == "private", live: true);
		var manifestRead = await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = manifestName });
		Assert.DoesNotContain(protectedValue, AllText(manifestRead), StringComparison.Ordinal);
		if (protection == "manual")
		{
			var markRead = await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "mark.txt" });
			Assert.DoesNotContain(protectedValue, AllText(markRead), StringComparison.Ordinal);
		}
		foreach (var tool in new[] { "get_file", "related_files" })
		{
			var result = await server.CallAsync(tool, new Dictionary<string, object?> { ["path"] = "src/entry.ts" });
			Assert.NotEqual(true, result.IsError);
			Assert.DoesNotContain(protectedValue, AllText(result), StringComparison.Ordinal);
			Assert.DoesNotContain("Exported by:", AllText(result), StringComparison.Ordinal);
			Assert.DoesNotContain("package export", AllText(result), StringComparison.Ordinal);
		}
	}
	private static async Task AddGlobalExportMarkAsync(ProjectProfileStore store, string project, string value)
	{
		Assert.True(DevProjex.Application.Secrets.MarkedSecretValueNormalizer.TryCreate(value, out var normalized, out _));
		var result = await store.AddMarkAsync(project, new MarkedSecretProfileEntry(normalized.Hash, null, normalized.Length),
			TestContext.Current.CancellationToken);
		Assert.True(result.Succeeded);
	}

}
