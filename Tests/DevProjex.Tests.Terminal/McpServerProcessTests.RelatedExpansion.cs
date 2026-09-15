namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessPacksASeedWithItsResolvedNeighbourhoodInOneCall()
	{
		using var workspace = new TemporaryDirectory();
		await using var server = await StartExpansionProjectAsync(workspace);

		var oneHop = await PackAsync(server, Expansion(["src/a.ts"], hops: 1, direction: "dependencies"));
		var twoHops = await PackAsync(server, Expansion(["src/a.ts"], hops: 2, direction: "dependencies"));

		// One hop reaches what the seed imports and stops there.
		Assert.Contains(
			"[Expanded] seeds=1 · hop1=+1 · hop2=+0 · seeds-without-facts=0.",
			oneHop,
			StringComparison.Ordinal);
		Assert.Contains("export const a", oneHop, StringComparison.Ordinal);
		Assert.Contains("export const b", oneHop, StringComparison.Ordinal);
		Assert.DoesNotContain("export const c", oneHop, StringComparison.Ordinal);
		Assert.DoesNotContain("export const unrelated", oneHop, StringComparison.Ordinal);

		// Two hops reach the whole chain, and still nothing outside it.
		Assert.Contains(
			"[Expanded] seeds=1 · hop1=+1 · hop2=+1 · seeds-without-facts=0.",
			twoHops,
			StringComparison.Ordinal);
		Assert.Contains("export const c", twoHops, StringComparison.Ordinal);
		Assert.DoesNotContain("export const unrelated", twoHops, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessExpansionNeverReachesOutsideTheEffectiveSelection()
	{
		using var workspace = new TemporaryDirectory();
		await using var server = await StartExpansionProjectAsync(workspace);

		var arguments = Expansion(["src/a.ts"], hops: 2, direction: "dependencies");
		arguments["exclude_patterns"] = new[] { "**/b.ts" };
		var excludedIntermediate = await PackAsync(server, arguments);

		// b.ts is outside the effective selection, so the edge into it does not exist for the
		// expansion and c.ts is never linked through it. An excluded file cannot be a bridge.
		Assert.Contains(
			"[Expanded] seeds=1 · hop1=+0 · hop2=+0 · seeds-without-facts=0.",
			excludedIntermediate,
			StringComparison.Ordinal);
		Assert.Contains("export const a", excludedIntermediate, StringComparison.Ordinal);
		Assert.DoesNotContain("export const b", excludedIntermediate, StringComparison.Ordinal);
		Assert.DoesNotContain("export const c", excludedIntermediate, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessExpansionComposesWithFocusAndRejectsASeedItDidNotAdmit()
	{
		using var workspace = new TemporaryDirectory();
		await using var server = await StartExpansionProjectAsync(workspace);

		var admitted = Expansion(["src/a.ts"], hops: 2, direction: "dependencies");
		admitted["rank"] = "importance";
		admitted["max_tokens"] = 20_000;
		admitted["focus"] = new[] { "src/c.ts" };
		var withFocus = await PackAsync(server, admitted);

		var rejected = Expansion(["src/a.ts"], hops: 2, direction: "dependencies");
		rejected["rank"] = "importance";
		rejected["max_tokens"] = 20_000;
		rejected["focus"] = new[] { "src/unrelated.ts" };
		var focusOutside = await CallAsync(server, "pack_context", rejected);

		// A focus seed the expansion admitted is ordinary focus ranking.
		Assert.Contains("export const c", withFocus, StringComparison.Ordinal);
		Assert.Contains("[Expanded] seeds=1", withFocus, StringComparison.Ordinal);

		// One the expansion did not admit is simply a path outside the selection, and gets the
		// error every unselected path gets, naming the filters that decided it.
		Assert.True(focusOutside.IsError);
		var failure = AllProcessText(focusOutside);
		Assert.Contains("DPX-MCP-PATH-NOT-FOUND", failure, StringComparison.Ordinal);
		Assert.Contains("exclusions:", failure, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessRejectsAnExpansionSeedThatIsNotASelectedFile()
	{
		using var workspace = new TemporaryDirectory();
		await using var server = await StartExpansionProjectAsync(workspace);

		var directorySeed = await CallAsync(
			server,
			"pack_context",
			Expansion(["src"], hops: 1, direction: "both"));
		var absentSeed = await CallAsync(
			server,
			"pack_context",
			Expansion(["src/missing.ts"], hops: 1, direction: "both"));
		var badHops = await CallAsync(
			server,
			"pack_context",
			Expansion(["src/a.ts"], hops: 3, direction: "both"));

		Assert.True(directorySeed.IsError);
		Assert.True(absentSeed.IsError);
		Assert.True(badHops.IsError);
		Assert.Contains(
			"'expand_related' 'hops' must be 1 or 2.",
			AllProcessText(badHops),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessLeavesAPackWithoutExpansionExactlyAsItWas()
	{
		using var workspace = new TemporaryDirectory();
		await using var server = await StartExpansionProjectAsync(workspace);

		var plain = await PackAsync(
			server,
			new Dictionary<string, object?> { ["view"] = "content", ["format"] = "text" });

		// Every file the filters admit, and not one word about expansion.
		Assert.Contains("export const a", plain, StringComparison.Ordinal);
		Assert.Contains("export const unrelated", plain, StringComparison.Ordinal);
		Assert.DoesNotContain("[Expanded]", plain, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessStopsAnExpansionAtItsPublishedFileLimit()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("expansion-limit-project");
		workspace.WriteFile(
			"expansion-limit-project/tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}\n");
		workspace.WriteFile("expansion-limit-project/src/hub.ts", "export const hub = 1;\n");
		for (var index = 0; index < 460; index++)
		{
			workspace.WriteFile(
				$"expansion-limit-project/src/leaf-{index:D4}.ts",
				$"import {{ hub }} from './hub.js'; export const leaf{index} = hub;\n");
		}
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var arguments = Expansion(["src/hub.ts"], hops: 1, direction: "dependents");
		arguments["view"] = "tree";
		var packed = await PackAsync(server, arguments);

		// 460 dependents cannot all be packed, and the response names the constant that stopped it
		// rather than quietly returning a prefix.
		Assert.Contains("[Expanded] seeds=1 · hop1=+399", packed, StringComparison.Ordinal);
		Assert.Contains(
			"stopped at the 400-file expansion limit, so the neighbourhood is incomplete.",
			packed,
			StringComparison.Ordinal);
	}

	private static Dictionary<string, object?> Expansion(string[] seeds, int hops, string direction) =>
		new()
		{
			["view"] = "content",
			["format"] = "text",
			["expand_related"] = new Dictionary<string, object?>
			{
				["seeds"] = seeds,
				["hops"] = hops,
				["direction"] = direction
			}
		};

	private static async ValueTask<string> PackAsync(
		ActualMcpProcess server,
		Dictionary<string, object?> arguments) =>
		AllProcessText(await CallAsync(server, "pack_context", arguments));

	private static async ValueTask<ActualMcpProcess> StartExpansionProjectAsync(
		TemporaryDirectory workspace)
	{
		var project = workspace.CreateDirectory("expansion-project");
		workspace.WriteFile(
			"expansion-project/tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}\n");
		workspace.WriteFile(
			"expansion-project/src/a.ts",
			"import { b } from './b.js';\nexport const a = b;\n");
		workspace.WriteFile(
			"expansion-project/src/b.ts",
			"import { c } from './c.js';\nexport const b = c;\n");
		workspace.WriteFile("expansion-project/src/c.ts", "export const c = 1;\n");
		workspace.WriteFile("expansion-project/src/unrelated.ts", "export const unrelated = 2;\n");
		return await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
	}
}
