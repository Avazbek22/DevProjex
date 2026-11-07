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

	[Fact]
	public async Task LiveRelatedFilesAnswersTheFocusFromReadableFilesOnly()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		void Write(string relativePath, string content)
		{
			var path = Path.Combine(project, relativePath);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, content);
		}
		Write("Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
		Write("GlobalUsings.cs", "global using Lib.Core.Enrichers;\n");
		Write(".gen/HiddenUsings.cs", "global using Lib.Core.Secretive;\n");
		Write(".gen/Generated.cs", "namespace Lib.Core;\npublic sealed class Generated { }\n");
		Write("Core/Logger.cs",
			"using Lib.Events;\nnamespace Lib.Core;\npublic sealed class Logger { FixedEnricher A; Gadget B; Generated C; LogEvent D; }\n");
		Write("Core/Enrichers/FixedEnricher.cs", "namespace Lib.Core.Enrichers;\npublic sealed class FixedEnricher { }\n");
		Write("Core/Secretive/Gadget.cs", "namespace Lib.Core.Secretive;\npublic sealed class Gadget { }\n");
		Write("Events/LogEvent.cs", "namespace Lib.Events;\npublic sealed class LogEvent { }\n");
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
				SelectedPaths: ["Core/Logger.cs", "Core/Enrichers/FixedEnricher.cs", "Core/Secretive/Gadget.cs"]));
		await using var server = await McpTestServer.StartAsync(project, workspace.Path, live: true);

		var result = await server.CallAsync("related_files", new Dictionary<string, object?>
		{
			["path"] = "Core/Logger.cs",
			["direction"] = "dependencies"
		});
		var text = AllText(result);
		var trusted = TrustedText(result);

		// The unfocused global using reaches a focused file, so it is listed with the seed; the
		// one inside a hidden folder is never read, so the file it alone would reach stays unlisted.
		Assert.NotEqual(true, result.IsError);
		var insideSection = text.IndexOf("Dependencies:", StringComparison.Ordinal);
		var outsideSection = text.IndexOf("Dependencies outside the selection:", StringComparison.Ordinal);
		Assert.InRange(insideSection, 0, outsideSection - 1);
		Assert.InRange(
			text.IndexOf("Core/Enrichers/FixedEnricher.cs — ", StringComparison.Ordinal),
			insideSection + 1,
			outsideSection - 1);
		Assert.True(text.IndexOf("Events/LogEvent.cs — ", StringComparison.Ordinal) > outsideSection);
		Assert.DoesNotContain("Gadget.cs", text, StringComparison.Ordinal);
		Assert.DoesNotContain(".gen", text, StringComparison.Ordinal);
		Assert.DoesNotContain("HiddenUsings", text, StringComparison.Ordinal);
		Assert.Contains("[Resolution] resolved=1 · ambiguous=0 · ", trusted, StringComparison.Ordinal);
		Assert.Contains(" · outside-selection=1", trusted, StringComparison.Ordinal);
		Assert.Contains("[Outside selection] relations=1 · listed=1;", trusted, StringComparison.Ordinal);
		Assert.DoesNotContain("FixedEnricher", trusted, StringComparison.Ordinal);
		Assert.DoesNotContain("LogEvent", trusted, StringComparison.Ordinal);
	}
}
