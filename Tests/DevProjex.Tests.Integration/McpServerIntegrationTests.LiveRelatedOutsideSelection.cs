using DevProjex.Application.Context;
using DevProjex.Infrastructure.LiveContext;
using DevProjex.Tests.Mcp;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	[Fact]
	public async Task LiveRelatedFilesNamesFilesOutsideTheFocusButNeverOnesTheWindowFiltersHide()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		Directory.CreateDirectory(Path.Combine(project, "src"));
		Directory.CreateDirectory(Path.Combine(project, ".gen"));
		File.WriteAllText(Path.Combine(project, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
		File.WriteAllText(Path.Combine(project, "src", "App.cs"),
			"namespace Sample; class App { Util First; Generated Second; }\n");
		File.WriteAllText(Path.Combine(project, "src", "Util.cs"), "namespace Sample; class Util { }\n");
		File.WriteAllText(Path.Combine(project, "src", "Caller.cs"), "namespace Sample; class Caller { App Value; }\n");
		File.WriteAllText(Path.Combine(project, ".gen", "Generated.cs"), "namespace Sample; class Generated { }\n");
		new ProjectProfileStore(() => Path.Combine(workspace.Path, "app-data")).SaveProfile(
			project,
			new ProjectSelectionProfile(
				[],
				[],
				[IgnoreOptionId.DotFolders],
				IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.DotFolders] = true
				},
				SelectedPaths: ["src/App.cs"]));
		await using var server = await McpTestServer.StartAsync(project, workspace.Path, live: true);

		var result = await server.CallAsync("related_files", new Dictionary<string, object?>
		{
			["path"] = "src/App.cs",
			["direction"] = "both"
		});
		var text = AllText(result);
		var trusted = TrustedText(result);

		Assert.NotEqual(true, result.IsError);
		Assert.Contains("Dependencies outside the selection:", text, StringComparison.Ordinal);
		Assert.Contains("src/Util.cs — ", text, StringComparison.Ordinal);
		Assert.Contains("Dependents outside the selection:", text, StringComparison.Ordinal);
		Assert.Contains("src/Caller.cs — ", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Generated.cs", text, StringComparison.Ordinal);
		Assert.Contains("[Outside selection] relations=2 · listed=2;", trusted, StringComparison.Ordinal);
		Assert.Contains("outside the focus were checked only for links to the seeds", trusted, StringComparison.Ordinal);
		Assert.DoesNotContain("src/Util.cs", trusted, StringComparison.Ordinal);
		Assert.DoesNotContain("src/Caller.cs", trusted, StringComparison.Ordinal);

		// The listed file is exactly what a named read may open; the hidden one stays refused.
		var listed = await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "src/Util.cs" });
		Assert.NotEqual(true, listed.IsError);
		var hidden = await server.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = ".gen/Generated.cs" });
		Assert.True(hidden.IsError);
	}
}
