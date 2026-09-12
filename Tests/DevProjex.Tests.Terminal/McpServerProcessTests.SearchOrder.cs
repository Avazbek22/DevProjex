namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessShowsADeclarationBeforeAMentionWhenMaxResultsWithholds()
	{
		using var workspace = new TemporaryDirectory();
		var project = await StartOrderProjectAsync(workspace);
		await using var server = project.Server;

		// Path order puts results/ first, so before this the slice was two rows of a generated
		// report and the declaration was withheld.
		var slice = Normalize(AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = "GetEffectiveLevel",
				["context_lines"] = 0,
				["max_results"] = 2
			})));

		var declaration = slice.IndexOf("src/Core/LevelOverrideMap.cs", StringComparison.Ordinal);
		var report = slice.IndexOf("results/report.md", StringComparison.Ordinal);
		Assert.True(declaration >= 0, slice);
		Assert.True(
			report < 0 || declaration < report,
			$"The generated report still precedes the declaration:\n{slice}");
		Assert.Contains("public void GetEffectiveLevel(", slice, StringComparison.Ordinal);

		// The order in force is named, in a constant that carries nothing from the project.
		Assert.Contains(
			"[Search order] bounded evidence priority; canonical path and line break ties.",
			slice,
			StringComparison.Ordinal);

		// What was not shown is still counted and still reachable.
		Assert.Contains("[Search observed] matches=5 · matching-files=4 within inspected sources", slice, StringComparison.Ordinal);
		Assert.Contains("[3 additional observed matches not shown", slice, StringComparison.Ordinal);
		Assert.Contains("[Search stored] pack_id=", slice, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessOrdersTheSameQueryTheSameWayEveryTime()
	{
		using var workspace = new TemporaryDirectory();
		var project = await StartOrderProjectAsync(workspace);
		await using var server = project.Server;

		var arguments = new Dictionary<string, object?>
		{
			["pattern"] = "GetEffectiveLevel",
			["context_lines"] = 0,
			["max_results"] = 2
		};
		// Compare the ordered body, not the trailer: the once-per-session notices deliberately
		// differ between a first response and a later one.
		var first = SpotlightBody(Normalize(AllProcessText(
			await CallAsync(server, "search_project", arguments))));
		var second = SpotlightBody(Normalize(AllProcessText(
			await CallAsync(server, "search_project", arguments))));

		Assert.Equal(first, second);
		Assert.Contains("src/Core/LevelOverrideMap.cs", first, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessUsesStableEvidenceOrderEvenWhenEveryMatchFits()
	{
		using var workspace = new TemporaryDirectory();
		var project = await StartOrderProjectAsync(workspace);
		await using var server = project.Server;

		var complete = Normalize(AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = "GetEffectiveLevel",
				["context_lines"] = 0
			})));

		// Arrival order is not evidence: the same stable rule applies even when every match fits,
		// otherwise raising max_results would silently put an early repeated report first again.
		var declaration = complete.IndexOf("src/Core/LevelOverrideMap.cs", StringComparison.Ordinal);
		var report = complete.IndexOf("results/report.md", StringComparison.Ordinal);
		Assert.True(declaration >= 0 && report > declaration, complete);
		Assert.Contains("[Search order] bounded evidence priority", complete, StringComparison.Ordinal);
		Assert.Contains("[Search boundary] complete · sources inspected=4/4 · matches retained=5/5 · " +
		                "matches written=5", complete, StringComparison.Ordinal);
		Assert.DoesNotContain("[Search stored]", complete, StringComparison.Ordinal);
		Assert.DoesNotContain("additional matches", complete, StringComparison.Ordinal);
	}

	private static async ValueTask<(ActualMcpProcess Server, string Root)> StartOrderProjectAsync(
		TemporaryDirectory workspace)
	{
		var project = workspace.CreateDirectory("order-project");
		// A committed generated report that mentions the term, and the source that declares it.
		workspace.WriteFile(
			"order-project/results/report.md",
			"# Benchmark\n\n| Method | Mean |\n| LevelOverrideMap_GetEffectiveLevel | 1,745 ns |\n| LevelOverrideMap_GetEffectiveLevel | 193 ns |\n");
		workspace.WriteFile(
			"order-project/src/Core/LevelOverrideMap.cs",
			"namespace Core;\n\npublic sealed class LevelOverrideMap\n{\n\tpublic void GetEffectiveLevel(string context)\n\t{\n\t}\n}\n");
		workspace.WriteFile(
			"order-project/src/Core/Logger.cs",
			"namespace Core;\n\npublic sealed class Logger\n{\n\tpublic void Write(LevelOverrideMap map)\n\t{\n\t\tmap.GetEffectiveLevel(\"x\");\n\t}\n}\n");
		workspace.WriteFile(
			"order-project/test/LevelOverrideMapTests.cs",
			"namespace Tests;\n\npublic sealed class LevelOverrideMapTests\n{\n\tpublic void Reads(Core.LevelOverrideMap map)\n\t{\n\t\tmap.GetEffectiveLevel(\"y\");\n\t}\n}\n");
		var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		return (server, project);
	}
}
