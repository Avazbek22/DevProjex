using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessRelatedFilesCompactsRepeatedEvidenceSitesIntoOneEntry()
	{
		using var repeated = new TemporaryDirectory();
		using var single = new TemporaryDirectory();
		var repeatedText = await RelatedEvidenceTextAsync(repeated, WriteEvidenceProject(repeated, siteCount: 7));
		var singleText = await RelatedEvidenceTextAsync(single, WriteEvidenceProject(single, siteCount: 1));

		Assert.Contains(
			"Models/User.cs — one visible declaration identity · " +
			"type reference User at lines 5, 6, 7, 8, 9 (+2 more) — resolved — ",
			repeatedText,
			StringComparison.Ordinal);
		Assert.DoesNotContain("type reference User at line ", repeatedText, StringComparison.Ordinal);
		Assert.Contains("Services/Clock.cs — one visible declaration identity · type reference Clock at line 12 — resolved — ",
			repeatedText, StringComparison.Ordinal);
		Assert.Contains("Models/User.cs — one visible declaration identity · type reference User at line 5 — resolved — ",
			singleText, StringComparison.Ordinal);
		Assert.DoesNotContain("at lines", OutsideSelectionTrustedText(repeatedText), StringComparison.Ordinal);
		// Compaction shapes only the fenced rows: the trusted notices match a project that has one site.
		Assert.Equal(OutsideSelectionTrustedText(singleText), OutsideSelectionTrustedText(repeatedText));
	}

	[Fact]
	public async Task RealProcessRelatedFilesCompactsWithheldSitesUnderTheirLabel()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
		workspace.WriteFile("project/Models/User.cs", "namespace App.Models; public sealed class User { }\n");
		workspace.WriteFile("project/Services/Store.cs",
			"using App.Models;\nnamespace App.Services;\npublic sealed class Store\n{\n" +
			$"\tUser First; // {Secret}\n" +
			$"\tUser Second; // {Secret}\n" +
			"\tUser Third;\n" +
			"}\n");

		var text = await RelatedEvidenceTextAsync(workspace, project);

		// A site on a line that protection replaces keeps no reference text; protection still
		// decides per site, and the withheld sites then share one entry like any other.
		Assert.Contains(
			"Models/User.cs — one visible declaration identity · type reference User at line 7 · " +
			"type reference at lines 5, 6 — resolved — ",
			text,
			StringComparison.Ordinal);
		Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
	}

	private static async Task<string> RelatedEvidenceTextAsync(TemporaryDirectory workspace, string project)
	{
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		var result = await CallAsync(server, "get_dependencies_and_usages", new Dictionary<string, object?>
		{
			["path"] = "Services/Store.cs",
			["direction"] = "dependencies"
		});
		Assert.NotEqual(true, result.IsError);
		return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
	}

	private static string WriteEvidenceProject(TemporaryDirectory workspace, int siteCount)
	{
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
		workspace.WriteFile("project/Models/User.cs", "namespace App.Models; public sealed class User { }\n");
		workspace.WriteFile("project/Services/Clock.cs", "namespace App.Services; public sealed class Clock { }\n");
		// The User sites start on line 5; padding keeps Clock on line 12 whatever the site count.
		var members = Enumerable.Range(1, 7)
			.Select(index => index <= siteCount ? $"\tUser Value{index};\n" : "\n");
		workspace.WriteFile("project/Services/Store.cs",
			"using App.Models;\nnamespace App.Services;\npublic sealed class Store\n{\n" +
			string.Concat(members) +
			"\tClock Time;\n" +
			"}\n");
		return project;
	}
}
