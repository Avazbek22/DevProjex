using System.Globalization;
using DevProjex.Application.Context;
using DevProjex.Infrastructure.ProjectProfiles;
using DevProjex.Mcp;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	[Fact]
	public async Task BatchStructureAndNormalizationKeepEveryStatusInsideTheLiveEnvelopeBudget()
	{
		const string marker = "ZzCallShapeMarker";
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var lineBreak = OperatingSystem.IsWindows() ? string.Empty : "\nsegment";
		var folder = marker + lineBreak;
		var large = folder + "/large.md";
		var small = folder + "/small.md";
		var plain = folder + "/plain.txt";
		var ranged = folder + "/ranged.txt";
		var symbolFile = folder + "/symbol.cs";
		var extra = folder + "/extra.txt";
		WriteProjectFile(project, large, CallShapeMarkdown(500, 100));
		WriteProjectFile(project, small, CallShapeMarkdown(220, 100));
		WriteProjectFile(project, plain, string.Join('\n', Enumerable.Repeat(new string('p', 100), 420)));
		WriteProjectFile(project, ranged, string.Join('\n', Enumerable.Repeat(new string('r', 100), 1_000)));
		WriteProjectFile(project, symbolFile, $"public class {marker} {{ public void Run() {{ }} }}");
		WriteProjectFile(project, extra, "extra-marker");
		var store = new ProjectProfileStore(() => Path.Combine(workspace.Path, "app-data"));
		store.SaveProfile(project, new ProjectSelectionProfile([], [".txt", ".md", ".cs"], [], SelectedPaths: null));
		await using var server = await McpTestServer.StartAsync(project, workspace.Path, live: true);
		var arguments = new Dictionary<string, object?>
		{
			["requests"] = new object[]
			{
				new { path = large }, new { path = plain }, new { path = small },
				new { path = ranged, ranges = new[] { new { start_line = 1, end_line = 800 } } },
				new { path = small, ranges = new[] { new { start_line = 9_000, end_line = 9_001 } } },
				new { path = folder + "/missing.txt" },
				new { path = symbolFile, symbol = marker + ".Run" },
				new { path = symbolFile, symbol = marker + ".Absent" }
			},
			["path"] = extra,
			["paths"] = new[] { "./" + extra }
		};
		var result = await server.CallAsync("get_file", arguments);
		var text = AllText(result);
		var body = ExtractSpotlightBody(text);
		Assert.NotEqual(true, result.IsError);
		Assert.True(text.Length <= 50_000, text.Length.ToString(CultureInfo.InvariantCulture));
		Assert.Contains("1.1 — structure — large file, 500 lines", body, StringComparison.Ordinal);
		Assert.Contains("2.1 — ok", body, StringComparison.Ordinal);
		Assert.Contains("3.1 — structure — no room left in this response, 220 lines", body, StringComparison.Ordinal);
		Assert.Contains("Status: structure\nLines: none of 220\nStructure:", body, StringComparison.Ordinal);
		Assert.Contains("4.1 — partial", body, StringComparison.Ordinal);
		Assert.Contains("5.1 — not-returned", body, StringComparison.Ordinal);
		Assert.Contains("requested lines 9000-9001; valid lines 1-220", body, StringComparison.Ordinal);
		Assert.Contains("6.1 — unavailable", body, StringComparison.Ordinal);
		Assert.Contains("7.1 — ok", body, StringComparison.Ordinal);
		Assert.Contains("8.1 — unavailable — symbol matches no declaration in this file", body, StringComparison.Ordinal);
		Assert.Contains("9.1 — ok", body, StringComparison.Ordinal);
		Assert.Contains("[Read as] path and requests were combined; 1 file(s) from path are read whole.", text, StringComparison.Ordinal);
		Assert.Contains("[Batch partial] requests=4.1;", text, StringComparison.Ordinal);
		AssertMarkerOnlyInsideUntrustedData(result, marker, "batch delivery");
		Assert.DoesNotContain(marker, TrustedText(result), StringComparison.Ordinal);
		await server.CallAsync("get_file", arguments);
		var stabilized = await server.CallAsync("get_file", arguments);
		var repeated = await server.CallAsync("get_file", arguments);
		Assert.Equal(ExtractSpotlightBody(AllText(stabilized)), ExtractSpotlightBody(AllText(repeated)));
		for (var read = 0; read < 3; read++)
		{
			var selected = await server.CallAsync("get_file", new Dictionary<string, object?>
			{
				["path"] = large, ["start_line"] = 200, ["end_line"] = 201
			});
			Assert.DoesNotContain("Structure:", AllText(selected), StringComparison.Ordinal);
		}
	}

	[Fact]
	public async Task BatchCoverageKeepsFilteredFilesUnavailableAndDoesNotBroadenTheLiveFocusForRelations()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "parts/a.ts", "import { b } from './b'; export const a = b;");
		WriteProjectFile(project, "parts/b.ts", "export const b = 1;");
		WriteProjectFile(project, "parts/hidden.md", "hidden-body-marker");
		WriteProjectFile(project, "parts/nested/not-a-seed.ts", "export const nested = 1;");
		var store = new ProjectProfileStore(() => Path.Combine(workspace.Path, "app-data"));
		store.SaveProfile(project, new ProjectSelectionProfile([], [".ts"], [],
			ExtensionStates: new Dictionary<string, bool> { [".ts"] = true, [".md"] = false },
			SelectedPaths: ["parts/a.ts"]));
		await using var server = await McpTestServer.StartAsync(project, workspace.Path, live: true);
		var batch = await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			["requests"] = new[] { new { path = "parts/a.ts" }, new { path = "parts/b.ts" }, new { path = "parts/hidden.md" } }
		});
		Assert.NotEqual(true, batch.IsError);
		Assert.Contains("2.1 — ok", AllText(batch), StringComparison.Ordinal);
		Assert.Contains("3.1 — unavailable", AllText(batch), StringComparison.Ordinal);
		Assert.DoesNotContain("hidden-body-marker", AllText(batch), StringComparison.Ordinal);
		var related = await server.CallAsync("related_files", new Dictionary<string, object?> { ["path"] = "parts" });
		Assert.NotEqual(true, related.IsError);
		Assert.Contains("[Read as] 1 folder(s) were replaced by the 1 file(s) directly inside them as seeds.", AllText(related), StringComparison.Ordinal);
		Assert.DoesNotContain("Seed: parts/b.ts", AllText(related), StringComparison.Ordinal);
		Assert.DoesNotContain("not-a-seed.ts", AllText(related), StringComparison.Ordinal);
	}

	[Fact]
	public async Task LiveEnvelopePressureBuildsFallbackFromTheCapturedSnapshot()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "big.md", "# One\n" + new string('x', 19_993) + "\n# Two\n" + new string('y', 19_993));
		WriteProjectFile(project, "small.txt", "small-content-marker");
		var store = new ProjectProfileStore(() => Path.Combine(workspace.Path, "app-data"));
		var profile = new ProjectSelectionProfile([], [".md", ".txt"], [],
			ExtensionStates: new Dictionary<string, bool> { [".md"] = true, [".txt"] = true }, SelectedPaths: null);
		store.SaveProfile(project, profile);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path, live: true);
		var initial = await server.CallAsync("get_tree");
		Assert.NotEqual(true, initial.IsError);
		var frontier = Enumerable.Range(0, 5).Select(index => $"missing-{index}/" +
			string.Join('/', Enumerable.Repeat(new string('p', 99), 20))).ToArray();
		store.SaveProfile(project, profile with { SelectedPaths = frontier });
		var result = await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			["requests"] = new[] { new { path = "big.md" }, new { path = "small.txt" } }
		});
		var text = AllText(result);
		var body = ExtractSpotlightBody(text);
		Assert.NotEqual(true, result.IsError);
		Assert.True(text.Length <= 50_000, text.Length.ToString(CultureInfo.InvariantCulture));
		Assert.Contains("Live context changed paths since revision 1:", text, StringComparison.Ordinal);
		Assert.Contains(frontier[0], text, StringComparison.Ordinal);
		Assert.Contains(frontier[4], text, StringComparison.Ordinal);
		Assert.Contains("1.1 — structure — no room left in this response, 4 lines", body, StringComparison.Ordinal);
		Assert.Contains("File: big.md\nRequests: 1.1\nStatus: structure\nLines: none of 4\nStructure:", body, StringComparison.Ordinal);
		Assert.Contains("2.1 — ok", body, StringComparison.Ordinal);
		Assert.Contains("small-content-marker", body, StringComparison.Ordinal);
		Assert.DoesNotContain(" — not-returned", body, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CombinedUnavailableAliasesKeepOneCanonicalRequest(bool exists)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "visible.ts", "export const visible = 1;");
		if (exists) WriteProjectFile(project, "hidden.md", "hidden-content-marker");
		var path = exists ? "hidden.md" : "missing.md";
		var store = new ProjectProfileStore(() => Path.Combine(workspace.Path, "app-data"));
		store.SaveProfile(project, new ProjectSelectionProfile([], [".ts"], [],
			ExtensionStates: new Dictionary<string, bool> { [".ts"] = true, [".md"] = false }, SelectedPaths: null));
		await using var server = await McpTestServer.StartAsync(project, workspace.Path, live: true);
		var result = await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			["requests"] = new[] { new { path } }, ["path"] = Path.Combine(project, path)
		});
		var text = AllText(result);
		Assert.NotEqual(true, result.IsError);
		Assert.Contains("[Batch read] ok=0 · structure=0 · partial=0 · not-returned=0 · unavailable=1.", text, StringComparison.Ordinal);
		Assert.Contains("1.1 — unavailable", text, StringComparison.Ordinal);
		Assert.DoesNotContain("2.1 —", text, StringComparison.Ordinal);
		Assert.DoesNotContain("[Read as]", text, StringComparison.Ordinal);
		Assert.DoesNotContain("hidden-content-marker", text, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("path")]
	[InlineData("paths")]
	public async Task ListedScalarReadsPreserveColumnsAndExplicitRanges(string form)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "Value.txt", "first\nαβγ second\nthird");
		async Task<string> ReadAsync(object value, string argument)
		{
			await using var server = await McpTestServer.StartAsync(project, workspace.Path);
			var result = await server.CallAsync("get_file", new Dictionary<string, object?>
			{
				[argument] = value, ["start_line"] = "2", ["end_line"] = "2", ["start_column"] = "2"
			});
			Assert.NotEqual(true, result.IsError);
			return WithoutFenceNonce(result);
		}
		var scalar = await ReadAsync("Value.txt", "path");
		Assert.Contains("βγ second", scalar, StringComparison.Ordinal);
		Assert.Equal(scalar, await ReadAsync(new[] { "Value.txt" }, form));
	}

	[Theory]
	[InlineData("path")]
	[InlineData("paths")]
	public async Task ListedCommonRangesReadAllSixteenFilesInRequestOrder(string form)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var paths = Enumerable.Range(0, 16).Select(index => $"File{index:D2}.txt").Reverse().ToArray();
		foreach (var path in paths) WriteProjectFile(project, path, $"omit-{path}\nkeep-{path}\nlast-{path}");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);
		var result = await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			[form] = paths, ["start_line"] = 2, ["end_line"] = 3
		});
		var text = AllText(result);
		var body = ExtractSpotlightBody(text);
		Assert.NotEqual(true, result.IsError);
		Assert.Contains("[Batch read] ok=16 · structure=0 · partial=0 · not-returned=0 · unavailable=0.", text, StringComparison.Ordinal);
		Assert.Contains("[Read as] start_line and end_line were applied to each of the 16 files.", text, StringComparison.Ordinal);
		Assert.Equal(paths, Regex.Matches(body, @"(?m)^File: ([^\n]+)$").Select(match => match.Groups[1].Value));
		Assert.Equal(16, Regex.Matches(body, "Lines: 2-3 of 3").Count);
		Assert.DoesNotContain("omit-", body, StringComparison.Ordinal);
		var explicitRanges = await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			["requests"] = paths.Select(path => new { path, ranges = new[] { new { start_line = 2, end_line = 3 } } }).ToArray()
		});
		Assert.NotEqual(true, explicitRanges.IsError);
		Assert.Equal(body, ExtractSpotlightBody(AllText(explicitRanges)));
	}

	[Theory]
	[InlineData("needle.*")]
	[InlineData("decode(?:URI|URIComponent)")]
	[InlineData("HTTPException\\(")]
	public async Task SymbolsRegularExpressionFallbackMatchesTextSearchExceptForItsNotice(string pattern)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "calls.txt", "needleOne\nneedleTwo\ndecodeURI(value)\ndecodeURIComponent(value)\nHTTPException(400)\n");
		async Task<string> SearchAsync(bool symbols)
		{
			await using var server = await McpTestServer.StartAsync(project, workspace.Path);
			var result = await server.CallAsync("search_project", new Dictionary<string, object?>
			{
				["pattern"] = pattern, ["symbols"] = symbols, ["context_lines"] = 0
			});
			Assert.NotEqual(true, result.IsError);
			return WithoutFenceNonce(result).Replace("\r\n", "\n", StringComparison.Ordinal);
		}
		var regular = await SearchAsync(false);
		var fallback = await SearchAsync(true);
		Assert.Contains(McpSearchSymbols.RegularExpressionFallbackNotice, fallback, StringComparison.Ordinal);
		Assert.Equal(regular, fallback.Replace(McpSearchSymbols.RegularExpressionFallbackNotice + "\n", string.Empty, StringComparison.Ordinal));
	}

	[Fact]
	public async Task WithheldDistributionStaysInsideItsReservedSpaceAndAccountsForEveryMissingMatch()
	{
		const string marker = "ZzWithheldMarker";
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		for (var file = 0; file < 30; file++)
			WriteProjectFile(project, marker + file.ToString("D2", CultureInfo.InvariantCulture) + ".txt",
				string.Join('\n', Enumerable.Repeat(marker + new string('x', 160), 4)));
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);
		var result = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = marker, ["context_lines"] = 0, ["max_results"] = 200
		});
		var text = AllText(result);
		var body = ExtractSpotlightBody(text);
		Assert.NotEqual(true, result.IsError);
		Assert.Contains("Withheld matches by file:", body, StringComparison.Ordinal);
		Assert.True(body.Length <= 16_000, body.Length.ToString(CultureInfo.InvariantCulture));
		var distribution = body[body.IndexOf("Withheld matches by file:", StringComparison.Ordinal)..];
		Assert.True(distribution.Length <= 2_000, distribution.Length.ToString(CultureInfo.InvariantCulture));
		var missing = int.Parse(Regex.Match(text, @"\[(\d+) additional observed matches not shown\.").Groups[1].Value, CultureInfo.InvariantCulture);
		var visibleCounts = Regex.Matches(distribution, @"(?m)^(\d+) [^\n]+\.txt$")
			.Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Sum();
		var remaining = Regex.Match(distribution, @"(?m)^(\d+) matches in those files$");
		if (remaining.Success) visibleCounts += int.Parse(remaining.Groups[1].Value, CultureInfo.InvariantCulture);
		Assert.Equal(missing, visibleCounts);
		Assert.DoesNotContain("[Next read] Call read_pack", text, StringComparison.Ordinal);
		Assert.Contains("the rest are counted per file under \"Withheld matches by file\".", text, StringComparison.Ordinal);
		AssertMarkerOnlyInsideUntrustedData(result, marker, "search distribution");
		var fallback = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = marker + ".*", ["symbols"] = true, ["max_results"] = 1
		});
		Assert.NotEqual(true, fallback.IsError);
		Assert.Contains(McpSearchSymbols.RegularExpressionFallbackNotice, AllText(fallback), StringComparison.Ordinal);
		AssertMarkerOnlyInsideUntrustedData(fallback, marker, "regex symbol fallback");
		var limited = AllText(fallback);
		var limitedBody = ExtractSpotlightBody(limited);
		var limitedDistribution = limitedBody[limitedBody.IndexOf("Withheld matches by file:", StringComparison.Ordinal)..];
		Assert.Contains("and 10 more file(s)", limitedDistribution, StringComparison.Ordinal);
		var limitedCounts = Regex.Matches(limitedDistribution, @"(?m)^(\d+) [^\n]+\.txt$");
		Assert.Equal(20, limitedCounts.Count);
		var counted = limitedCounts.Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Sum();
		var tail = Regex.Match(limitedDistribution, @"(?m)^(\d+) matches in those files$");
		Assert.True(tail.Success, limitedDistribution);
		counted += int.Parse(tail.Groups[1].Value, CultureInfo.InvariantCulture);
		Assert.Equal(119, counted);
	}

	[Fact]
	public async Task WithheldReservationDoesNotChangeRetainedSearchWindows()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "matches.txt", string.Join('\n', Enumerable.Range(1, 1_000)
			.Select(line => $"needle-{line:D4} {new string('x', 80)}")));
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);
		async Task<string> StoredPageAsync(int maximumResults)
		{
			var search = await server.CallAsync("search_project", new Dictionary<string, object?>
			{
				["pattern"] = "needle", ["context_lines"] = 0, ["max_results"] = maximumResults
			});
			var text = AllText(search);
			Assert.NotEqual(true, search.IsError);
			Assert.Contains("Withheld matches by file:", text, StringComparison.Ordinal);
			Assert.Contains("the rest are counted per file under \"Withheld matches by file\".", text, StringComparison.Ordinal);
			Assert.Single(Regex.Matches(text, "Call read_pack with this pack_id\\."));
			Assert.DoesNotContain("[Next read] Call read_pack", text, StringComparison.Ordinal);
			var identifier = Regex.Match(text, @"\[Search stored\] pack_id=([^ ·\r\n]+)");
			Assert.True(identifier.Success, text);
			var page = await server.CallAsync("read_pack", new Dictionary<string, object?>
			{
				["pack_id"] = identifier.Groups[1].Value
			});
			Assert.NotEqual(true, page.IsError);
			return ExtractSpotlightBody(AllText(page));
		}
		var narrow = await StoredPageAsync(1);
		var wide = await StoredPageAsync(200);
		Assert.Contains("needle-0001", narrow, StringComparison.Ordinal);
		Assert.Equal(narrow, wide);
	}

	[Theory]
	[InlineData("missing")]
	[InlineData("hidden")]
	[InlineData("automatic")]
	[InlineData("manual")]
	[InlineData("locked")]
	public async Task ReferencedExportsRequireReadableUnprotectedConfigurationsWithinSelection(string failure)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		WriteProjectFile(project, "src/entry.ts", "export const entry = 1;");
		WriteProjectFile(project, "package.json", """{"exports":{".":{"types":"./dist/entry.d.ts","import":"./dist/entry.js"}}}""");
		WriteProjectFile(project, "tsconfig.json", """{"files":[],"references":[{"path":"./tsconfig.build.json"}]}""");
		const string configuration = """{"extends":"./tsconfig.base.json","compilerOptions":{"rootDir":"src","outDir":"dist"},"include":["src/**/*.ts"]}""";
		WriteProjectFile(project, "tsconfig.build.json", configuration);
		WriteProjectFile(project, "tsconfig.base.json", """{/* a child's explicit roots override the base */"compilerOptions":{"outDir":"${configDir}/dist"},}""");
		await using (var baseline = await McpTestServer.StartAsync(project, workspace.Path))
		{
			var available = await baseline.CallAsync("get_file", new Dictionary<string, object?> { ["path"] = "src/entry.ts" });
			Assert.NotEqual(true, available.IsError);
			Assert.Contains("Exported by: package.json \".\"", AllText(available), StringComparison.Ordinal);
		}
		FileStream? held = null;
		try
		{
			if (failure == "missing") File.Delete(Path.Combine(project, "tsconfig.build.json"));
			else if (failure == "hidden") WriteProjectFile(project, ".gitignore", "tsconfig.build.json\n");
			else if (failure == "automatic")
				WriteProjectFile(project, "tsconfig.build.json", configuration[..^1] + ",\"token\":\"" + Secret + "\"}");
			else if (failure == "manual")
			{
				const string protectedValue = "opaque-configuration-value";
				var source = configuration[..^1] + ",\"value\":\"" + protectedValue + "\"}";
				WriteProjectFile(project, "tsconfig.build.json", source);
				var dataRoot = Path.Combine(workspace.Path, "app-data");
				var store = new ProjectProfileStore(() => dataRoot);
				store.SaveProfile(project, new ProjectSelectionProfile([], [".json", ".ts"], [], SelectedPaths: null));
				await AddPersistentMarkAsync(store, dataRoot, project, "tsconfig.build.json", source.IndexOf(protectedValue, StringComparison.Ordinal), protectedValue);
			}
			else held = new FileStream(Path.Combine(project, "tsconfig.build.json"), FileMode.Open, FileAccess.Read, FileShare.None);
			await using var guarded = await McpTestServer.StartAsync(project, workspace.Path, live: failure == "manual");
			foreach (var tool in new[] { "get_file", "related_files" })
			{
				var result = await guarded.CallAsync(tool, new Dictionary<string, object?> { ["path"] = "src/entry.ts" });
				Assert.NotEqual(true, result.IsError);
				Assert.DoesNotContain("Exported by:", AllText(result), StringComparison.Ordinal);
				Assert.DoesNotContain("package export", AllText(result), StringComparison.Ordinal);
				Assert.DoesNotContain(Secret, AllText(result), StringComparison.Ordinal);
			}
		}
		finally { held?.Dispose(); }
	}

	private static string CallShapeMarkdown(int lines, int width) => string.Join('\n', Enumerable.Range(0, lines)
		.Select(index => index == 0 ? "# First" : index == lines / 2 ? "# Second" : new string('x', width)));
}
