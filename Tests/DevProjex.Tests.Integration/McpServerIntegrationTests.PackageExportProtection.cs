using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.ProjectProfiles;
using DevProjex.Infrastructure.Secrets;
using DevProjex.Mcp;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task MetadataHintsDoNotReplaceTheSourceSecretScanCache(bool imports)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "package.json", """{"exports":{".":"./src/entry.ts"}}""");
		WriteProjectFile(project, "tsconfig.json", """{"compilerOptions":{"module":"esnext","moduleResolution":"bundler"}}""");
		WriteProjectFile(project, "src/entry.ts", "export const entry = \"" + Secret + "\";\n");
		WriteProjectFile(project, "src/consumer.ts", "import { entry } from './entry';\nexport const token = \"" + Secret + "\";\n");
		McpServices? services = null;
		await using var server = await McpTestServer.StartAsync(project, workspace.Path, configureServices: created => services = created);
		var arguments = new Dictionary<string, object?> { ["path"] = imports ? "src/consumer.ts" : "src/entry.ts" };
		var first = await server.CallAsync("get_file", arguments);
		Assert.NotEqual(true, first.IsError);
		var firstText = AllText(first);
		Assert.DoesNotContain(Secret, firstText, StringComparison.Ordinal);
		Assert.Contains(imports ? "Imports:" : "Exported by:", firstText, StringComparison.Ordinal);
		Assert.NotNull(services);
		var runs = services.RedactionSession.GetCacheDiagnostics().DetectionRuns;
		var second = await server.CallAsync("get_file", arguments);
		Assert.NotEqual(true, second.IsError);
		Assert.DoesNotContain(Secret, AllText(second), StringComparison.Ordinal);
		Assert.Equal(runs, services.RedactionSession.GetCacheDiagnostics().DetectionRuns);
		Assert.Equal(ExtractSpotlightBody(firstText), ExtractSpotlightBody(AllText(second)));
		var path = Path.Combine(project, arguments["path"]!.ToString()!);
		File.AppendAllText(path, "export const updatedValue = true;\n");
		var changed = await server.CallAsync("get_file", arguments);
		Assert.NotEqual(true, changed.IsError);
		Assert.Contains("updatedValue", AllText(changed), StringComparison.Ordinal);
		Assert.DoesNotContain(Secret, AllText(changed), StringComparison.Ordinal);
		var updatedRuns = services.RedactionSession.GetCacheDiagnostics().DetectionRuns;
		Assert.True(updatedRuns > runs);
		var warmed = await server.CallAsync("get_file", arguments);
		Assert.DoesNotContain(Secret, AllText(warmed), StringComparison.Ordinal);
		Assert.Equal(updatedRuns, services.RedactionSession.GetCacheDiagnostics().DetectionRuns);
		Assert.Equal(ExtractSpotlightBody(AllText(changed)), ExtractSpotlightBody(AllText(warmed)));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task PackageExportMetadataHonorsManualMarksOutsideManifestContent(bool keyed)
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
		await AddGlobalExportMarkAsync(store, project, marked, keyed, appData);
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
	[InlineData("manual-v2")]
	[InlineData("source-bound")]
	[InlineData("private")]
	public async Task ProtectedManifestContentCannotProducePackageExportMetadata(string protection)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var manual = protection is "manual" or "manual-v2" or "source-bound";
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
				await AddGlobalExportMarkAsync(store, project, protectedValue, protection == "manual-v2", appData);
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

	private static async Task AddGlobalExportMarkAsync(
		ProjectProfileStore store, string project, string value, bool keyed, string appData)
	{
		MarkedSecretProfileEntry mark;
		if (keyed)
		{
			using var provider = new PersistentSecretIdentityProvider(() => appData);
			Assert.Equal(PersistentSecretIdentityAvailability.Ready,
				await provider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
			Assert.True(PersistentSecretIdentity.TryCreateV2(provider, value, out var identity));
			mark = new(identity, null, value.Length);
		}
		else
		{
			Assert.True(MarkedSecretValueNormalizer.TryCreate(value, out var normalized, out _));
			mark = new(normalized.Hash, null, normalized.Length);
		}
		var result = await store.AddMarkAsync(project, mark, TestContext.Current.CancellationToken);
		Assert.True(result.Succeeded);
	}
}
