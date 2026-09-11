namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	private const string NameSearchNotice =
		"[Name search] File names and paths are matched only by include_patterns: prefix a bare name " +
		"with '**/' to find it at any depth, or append '/**' to a directory to select its files. " +
		"search_project matches file content, and paths selects a path that already exists.";

	[Fact]
	public async Task RealProcessAnswersEveryNaturalFileNameSearchWithAResultOrTheFormToType()
	{
		using var workspace = new TemporaryDirectory();
		await using var server = await StartNameSearchProjectAsync(workspace);
		{
			// The eight forms a caller reaches for to answer "where is the file named App.cs".
			// The project holds src/App.cs and src/deep/App.cs and no App.cs in its root.
			var contentWithName = await SearchAsync(server, "App.cs");
			var contentWithPathRegex = await SearchAsync(server, "src/.*App.*\\.cs$");
			var bareName = await TreeAsync(server, "include_patterns", ["App.cs"]);
			var starsWithoutSlash = await TreeAsync(server, "include_patterns", ["*App*"]);
			var spanningName = await TreeAsync(server, "include_patterns", ["**/App.cs"]);
			var spanningStars = await TreeAsync(server, "include_patterns", ["**/*App*"]);
			var pathBareName = await TreeAsync(server, "paths", ["App.cs"]);
			var pathDirectory = await TreeAsync(server, "paths", ["src"]);

			// Content search never matches a name, and now says so and names the form that does.
			foreach (var text in new[] { contentWithName, contentWithPathRegex })
			{
				Assert.Contains("[No matches]", text, StringComparison.Ordinal);
				Assert.Contains(NameSearchNotice, text, StringComparison.Ordinal);
			}

			// A pattern with no '/' and no '**' is root-only, which is the documented rule; the
			// empty result already names the rewrite that reaches any depth.
			foreach (var text in new[] { bareName, starsWithoutSlash })
			{
				Assert.Contains("[Empty selection] stage=patterns.", text, StringComparison.Ordinal);
				Assert.Contains(
					"prefix it with '**/' to match that name at any depth",
					text,
					StringComparison.Ordinal);
			}

			// The two spanning forms answer with the files themselves and gain nothing.
			foreach (var text in new[] { spanningName, spanningStars })
			{
				Assert.Contains("App.cs", text, StringComparison.Ordinal);
				Assert.Contains("deep", text, StringComparison.Ordinal);
				Assert.DoesNotContain("Helper.cs", text, StringComparison.Ordinal);
				Assert.DoesNotContain("[Name search]", text, StringComparison.Ordinal);
				Assert.DoesNotContain("[Empty selection]", text, StringComparison.Ordinal);
			}

			// paths selects paths that already exist, so a bare name that lives deeper misses. The
			// response kept its warning and now also names the form that finds the file.
			Assert.Contains("[Warning DPX-SELECTION-PATH-MISSING]", pathBareName, StringComparison.Ordinal);
			Assert.Contains(NameSearchNotice, pathBareName, StringComparison.Ordinal);

			// A directory in paths already works and is left alone.
			Assert.Contains("Helper.cs", pathDirectory, StringComparison.Ordinal);
			Assert.DoesNotContain("[Name search]", pathDirectory, StringComparison.Ordinal);
			Assert.DoesNotContain("[Warning DPX-SELECTION-PATH-MISSING]", pathDirectory, StringComparison.Ordinal);
		}
	}

	[Fact]
	public async Task RealProcessLeavesAContentSearchThatSimplyFoundNothingUnchanged()
	{
		using var workspace = new TemporaryDirectory();
		await using var server = await StartNameSearchProjectAsync(workspace);
		{
			var found = await SearchAsync(server, "sealed");
			var missed = await SearchAsync(server, "absentphrase");

			// A pattern that carries neither a separator nor an extension is a content search that
			// found nothing, and it keeps the response it has always had.
			Assert.Contains("[No matches]", missed, StringComparison.Ordinal);
			Assert.DoesNotContain("[Name search]", missed, StringComparison.Ordinal);

			// A search that found something never carries the pointer either.
			Assert.Contains("src/App.cs:", found, StringComparison.Ordinal);
			Assert.DoesNotContain("[No matches]", found, StringComparison.Ordinal);
			Assert.DoesNotContain("[Name search]", found, StringComparison.Ordinal);

			// A selection that held no file to search already explains itself, and is left alone
			// even though the pattern reads like a file name.
			var nothingSelected = AllProcessText(await CallAsync(
				server,
				"search_project",
				new Dictionary<string, object?>
				{
					["pattern"] = "App.cs",
					["context_lines"] = 0,
					["include_patterns"] = new[] { "**/*.nothing" }
				}));
			Assert.Contains("[Empty selection] stage=patterns.", nothingSelected, StringComparison.Ordinal);
			Assert.DoesNotContain("[No matches]", nothingSelected, StringComparison.Ordinal);
			Assert.DoesNotContain("[Name search]", nothingSelected, StringComparison.Ordinal);
		}
	}

	private static async ValueTask<ActualMcpProcess> StartNameSearchProjectAsync(
		TemporaryDirectory workspace)
	{
		var project = workspace.CreateDirectory("name-search-project");
		workspace.WriteFile("name-search-project/Readme.md", "# Notes\n");
		workspace.WriteFile("name-search-project/src/App.cs", "namespace P;\n\npublic sealed class App;\n");
		workspace.WriteFile("name-search-project/src/Helper.cs", "namespace P;\n\npublic sealed class Helper;\n");
		workspace.WriteFile("name-search-project/src/deep/App.cs", "namespace P.Deep;\n\npublic sealed class App;\n");
		return await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
	}

	private static async ValueTask<string> SearchAsync(ActualMcpProcess server, string pattern) =>
		AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?> { ["pattern"] = pattern, ["context_lines"] = 0 }));

	private static async ValueTask<string> TreeAsync(
		ActualMcpProcess server,
		string argument,
		string[] values) =>
		AllProcessText(await CallAsync(
			server,
			"get_tree",
			new Dictionary<string, object?> { ["format"] = "text", [argument] = values }));
}
