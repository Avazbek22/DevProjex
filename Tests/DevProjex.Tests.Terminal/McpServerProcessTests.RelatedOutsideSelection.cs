using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessRelatedFilesListsReadableFilesOutsideANarrowedSelection()
	{
		using var workspace = new TemporaryDirectory();
		var project = WriteOutsideSelectionProject(workspace);
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		var narrowed = await CallAsync(server, "get_dependencies_and_usages", new Dictionary<string, object?>
		{
			["path"] = "Services/Store.cs",
			["direction"] = "dependencies",
			["include_patterns"] = new[] { "Services/**" }
		});
		var text = Assert.IsType<TextContentBlock>(Assert.Single(narrowed.Content)).Text;
		var trusted = OutsideSelectionTrustedText(text);

		Assert.NotEqual(true, narrowed.IsError);
		Assert.Contains("Dependencies outside the selection:", text, StringComparison.Ordinal);
		Assert.Contains("Models/User.cs — ", text, StringComparison.Ordinal);
		Assert.Contains("type reference User at line 1", text, StringComparison.Ordinal);
		Assert.Contains("[Resolution] resolved=1 · ambiguous=0 · unresolved=0 · external=1 · outside-selection=1",
			trusted, StringComparison.Ordinal);
		Assert.Contains("[Outside selection] relations=1 · listed=1;", trusted, StringComparison.Ordinal);
		Assert.DoesNotContain("Models/User.cs", trusted, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessRelatedFilesWithoutNarrowingKeepsItsResponseShape()
	{
		using var workspace = new TemporaryDirectory();
		var project = WriteOutsideSelectionProject(workspace);
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		var whole = await CallAsync(server, "get_dependencies_and_usages", new Dictionary<string, object?>
		{
			["path"] = "Services/Store.cs",
			["direction"] = "both"
		});
		var text = Assert.IsType<TextContentBlock>(Assert.Single(whole.Content)).Text;

		Assert.Contains("[Resolution] resolved=2 · ambiguous=0 · unresolved=0 · external=1\n", text,
			StringComparison.Ordinal);
		Assert.DoesNotContain("outside the selection", text, StringComparison.Ordinal);
		Assert.DoesNotContain("[Outside selection]", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessRelatedFilesNeverNamesAFileTheFiltersHide()
	{
		using var workspace = new TemporaryDirectory();
		var project = WriteOutsideSelectionProject(workspace);
		workspace.WriteFile("project/.private/Hidden.cs", "namespace App.Private; public sealed class Hidden { }\n");
		workspace.WriteFile("project/Models/Excluded.cs", "namespace App.Models; public sealed class Excluded { }\n");
		workspace.WriteFile("project/Models/Large.cs",
			"namespace App.Models; public sealed class Large { }\n" + new string('/', 4096) + "\n");
		workspace.WriteFile("project/Services/Uses.cs",
			"using App.Models; using App.Private; namespace App.Services; " +
			"public sealed class Uses { Hidden A; Excluded B; Large C; User D; }\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"),
			["--exclude", "default", "--exclude", "dot-folders"]);

		var result = await CallAsync(server, "get_dependencies_and_usages", new Dictionary<string, object?>
		{
			["path"] = "Services/Uses.cs",
			["direction"] = "dependencies",
			["include_patterns"] = new[] { "Services/**" },
			["exclude_patterns"] = new[] { "Models/Excluded.cs" },
			["max_file_bytes"] = 1024
		});
		var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

		Assert.NotEqual(true, result.IsError);
		Assert.Contains("Models/User.cs — ", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Hidden.cs", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Excluded.cs", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Large.cs", text, StringComparison.Ordinal);
		Assert.Contains("[Outside selection] relations=1 · listed=1;", text, StringComparison.Ordinal);

		// Each filter is what keeps its file out: lifting the call's own filters lists them, and a
		// server without the startup exclusion lists the dot-folder file too.
		var unfiltered = await CallAsync(server, "get_dependencies_and_usages", new Dictionary<string, object?>
		{
			["path"] = "Services/Uses.cs",
			["direction"] = "dependencies",
			["include_patterns"] = new[] { "Services/**" }
		});
		var unfilteredText = Assert.IsType<TextContentBlock>(Assert.Single(unfiltered.Content)).Text;
		Assert.Contains("Models/Excluded.cs — ", unfilteredText, StringComparison.Ordinal);
		Assert.Contains("Models/Large.cs — ", unfilteredText, StringComparison.Ordinal);
		Assert.DoesNotContain("Hidden.cs", unfilteredText, StringComparison.Ordinal);
		await using var permissive = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data-permissive"));
		var permissiveResult = await CallAsync(permissive, "get_dependencies_and_usages", new Dictionary<string, object?>
		{
			["path"] = "Services/Uses.cs",
			["direction"] = "dependencies",
			["include_patterns"] = new[] { "Services/**" }
		});
		Assert.Contains(".private/Hidden.cs — ",
			Assert.IsType<TextContentBlock>(Assert.Single(permissiveResult.Content)).Text,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessRelatedFilesAnswersANarrowedSeedFromTheReadableProject()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		NarrowedRelationFixtures.WriteGlobalUsingLibrary(workspace, "project");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		var narrowed = await CallAsync(server, "get_dependencies_and_usages", new Dictionary<string, object?>
		{
			["path"] = NarrowedRelationFixtures.LibrarySeed,
			["direction"] = "both",
			["include_patterns"] = new[] { NarrowedRelationFixtures.LibraryFolder + "/**" }
		});
		var whole = await CallAsync(server, "get_dependencies_and_usages", new Dictionary<string, object?>
		{
			["path"] = NarrowedRelationFixtures.LibrarySeed,
			["direction"] = "both"
		});
		var text = Assert.IsType<TextContentBlock>(Assert.Single(narrowed.Content)).Text;
		var wholeText = Assert.IsType<TextContentBlock>(Assert.Single(whole.Content)).Text;

		// The global using lies outside the selection, yet the files it reaches are in it and
		// belong in the seed's own section.
		Assert.NotEqual(true, narrowed.IsError);
		Assert.Equal(
			NarrowedRelationFixtures.LibraryDependenciesInside,
			NarrowedRelationFixtures.SectionPaths(text, "Dependencies"));
		Assert.Empty(NarrowedRelationFixtures.SectionPaths(text, "Dependents"));
		Assert.Equal(["Events/LogEvent.cs"],
			NarrowedRelationFixtures.SectionPaths(text, "Dependencies outside the selection"));
		Assert.Equal(["Configuration.cs"],
			NarrowedRelationFixtures.SectionPaths(text, "Dependents outside the selection"));
		Assert.Contains("[Resolution] resolved=3 · ambiguous=0 · unresolved=0 · external=0 · outside-selection=2\n",
			text, StringComparison.Ordinal);
		Assert.Contains("[Outside selection] relations=2 · listed=2;", text, StringComparison.Ordinal);
		Assert.Contains("[Search scope] files=4", text, StringComparison.Ordinal);
		Assert.Contains("[Resolution] resolved=5 · ambiguous=0 · unresolved=0 · external=0\n",
			wholeText, StringComparison.Ordinal);
		Assert.Equal(
			NarrowedRelationFixtures.SectionPaths(wholeText, "Dependencies"),
			NarrowedRelationFixtures.SectionPaths(text, "Dependencies")
				.Concat(NarrowedRelationFixtures.SectionPaths(text, "Dependencies outside the selection"))
				.Order(StringComparer.Ordinal));
	}

	[Fact]
	public async Task RealProcessRelatedFilesKeepsNarrowedPathsInsideTheUntrustedBlock()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		NarrowedRelationFixtures.WriteGlobalUsingLibrary(workspace, "project");
		workspace.WriteFile("project/Core/Marked.cs",
			"using Lib.Events;\nnamespace Lib.Core;\npublic sealed class Marked { MarkedEnricher First; MarkedEvent Second; }\n");
		workspace.WriteFile("project/Core/Enrichers/Zq7InsideMarker.cs",
			"namespace Lib.Core.Enrichers;\npublic sealed class MarkedEnricher { }\n");
		workspace.WriteFile("project/Events/Zq7OutsideMarker.cs",
			"namespace Lib.Events;\npublic sealed class MarkedEvent { }\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		var result = await CallAsync(server, "get_dependencies_and_usages", new Dictionary<string, object?>
		{
			["path"] = "Core/Marked.cs",
			["direction"] = "both",
			["include_patterns"] = new[] { "Core/**" }
		});
		var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
		var trusted = OutsideSelectionTrustedText(text);

		Assert.NotEqual(true, result.IsError);
		Assert.Equal(["Core/Enrichers/Zq7InsideMarker.cs"], NarrowedRelationFixtures.SectionPaths(text, "Dependencies"));
		Assert.Equal(["Events/Zq7OutsideMarker.cs"],
			NarrowedRelationFixtures.SectionPaths(text, "Dependencies outside the selection"));
		Assert.Contains("[Resolution] resolved=1 · ambiguous=0 · unresolved=0 · external=0 · outside-selection=1",
			trusted, StringComparison.Ordinal);
		Assert.DoesNotContain("Zq7", trusted, StringComparison.Ordinal);
		Assert.DoesNotContain("Marked", trusted, StringComparison.Ordinal);
	}

	private static string WriteOutsideSelectionProject(TemporaryDirectory workspace)
	{
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
		workspace.WriteFile("project/Models/User.cs", "namespace App.Models; public sealed class User { }\n");
		workspace.WriteFile("project/Services/Clock.cs", "namespace App.Services; public sealed class Clock { }\n");
		workspace.WriteFile("project/Services/Store.cs",
			"using App.Models; namespace App.Services; public sealed class Store { User Value; Clock Time; Missing Other; }\n");
		return project;
	}

	private static string OutsideSelectionTrustedText(string text) => Regex.Replace(
		text,
		@"<untrusted-data-(?<nonce>[0-9a-f]{24})>\n[\s\S]*?\n</untrusted-data-\k<nonce>>",
		string.Empty);
}
