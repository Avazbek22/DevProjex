using System.Text.RegularExpressions;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.ProjectProfiles;
using DevProjex.Kernel.Models;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Theory]
	[InlineData(".js")]
	[InlineData(".jsx")]
	[InlineData(".mjs")]
	[InlineData(".cjs")]
	[InlineData(".ts")]
	[InlineData(".tsx")]
	[InlineData(".mts")]
	[InlineData(".cts")]
	public async Task RealProcessTestBlocksAppearInScalarAndBatchOutlinesWithoutBecomingSymbols(string extension)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("blocks-project");
		var path = "sample" + extension;
		var source = LargeTestBlocks("outlineSuiteMarker", "outlineFirstMarker", "outlineLastMarker");
		workspace.WriteFile("blocks-project/" + path, source);
		workspace.WriteFile("blocks-project/helper.js", "export const helper = 1;");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("blocks-data"));
		var scalar = await CallAsync(server, "get_file", new() { ["path"] = path });
		Assert.NotEqual(true, scalar.IsError);
		AssertTestBlockBudgetSlack(scalar);
		var scalarBody = BatchData(scalar);
		Assert.Contains("Structure:", scalarBody, StringComparison.Ordinal);
		Assert.Contains("\"outlineSuiteMarker\" (describe)", scalarBody, StringComparison.Ordinal);
		Assert.Contains("\"outlineFirstMarker\" (test)", scalarBody, StringComparison.Ordinal);
		Assert.Contains("\"outlineLastMarker\" (test)", scalarBody, StringComparison.Ordinal);
		Assert.DoesNotContain("padding-0399", scalarBody, StringComparison.Ordinal);
		var batched = await CallAsync(server, "get_file", new() { ["requests"] = new[] { new { path } } });
		Assert.NotEqual(true, batched.IsError);
		Assert.Contains("1.1 — structure — large file,", BatchData(batched), StringComparison.Ordinal);
		Assert.Contains("\"outlineLastMarker\" (test)", BatchData(batched), StringComparison.Ordinal);
		AssertTestBlockBudgetSlack(batched);
		var repeated = await CallAsync(server, "get_file", new() { ["requests"] = new[] { new { path } } });
		Assert.Equal(BatchData(batched), BatchData(repeated));
		var unknown = await CallAsync(server, "get_file", new() { ["path"] = path, ["symbol"] = "outlineSuiteMarker" });
		Assert.True(unknown.IsError);
		Assert.Contains("matches no declaration in this file", AllProcessText(unknown), StringComparison.Ordinal);
		var symbolSearch = await CallAsync(server, "search_project", new()
		{
			["pattern"] = "outlineSuiteMarker",
			["symbols"] = true,
			["paths"] = new[] { path }
		});
		Assert.NotEqual(true, symbolSearch.IsError);
		Assert.Contains("matches written=0", AllProcessText(symbolSearch), StringComparison.Ordinal);
		Assert.DoesNotContain("Declarations found", BatchData(symbolSearch), StringComparison.Ordinal);
		Assert.DoesNotContain("Best declaration body", BatchData(symbolSearch), StringComparison.Ordinal);
		var declared = await CallAsync(server, "get_file", new() { ["path"] = path, ["symbol"] = "actualSymbol" });
		Assert.NotEqual(true, declared.IsError);
		Assert.Contains("function actualSymbol()", BatchData(declared), StringComparison.Ordinal);
		for (var index = 0; index < 3; index++)
		{
			var ranged = await CallAsync(server, "get_file", new() { ["path"] = path, ["start_line"] = 10 + index, ["end_line"] = 11 + index });
			Assert.NotEqual(true, ranged.IsError);
			Assert.DoesNotContain("Structure:", AllProcessText(ranged), StringComparison.Ordinal);
		}
	}

	[Fact]
	public async Task RealProcessTestOnlyStructurePreservesANoRoomFile()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("fallback-blocks-project");
		workspace.WriteFile("fallback-blocks-project/first.txt", string.Join('\n', Enumerable.Repeat(new string('x', 100), 450)));
		var source = "test('small-one', () => {});\n" +
			string.Join('\n', Enumerable.Repeat("// " + new string('x', 170), 120)) + "\ntest('small-two', () => {});";
		workspace.WriteFile("fallback-blocks-project/small.spec.ts", source);
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("fallback-data"));
		var result = await CallAsync(server, "get_file", new() { ["paths"] = new[] { "first.txt", "small.spec.ts" } });
		Assert.NotEqual(true, result.IsError);
		var body = BatchData(result);
		Assert.Contains("1.1 — ok", body, StringComparison.Ordinal);
		Assert.Contains("2.1 — structure — no room left in this response, 122 lines", body, StringComparison.Ordinal);
		Assert.Contains("File: small.spec.ts\nRequests: 2.1\nStatus: structure\nLines: none of 122\nStructure:", body, StringComparison.Ordinal);
		Assert.Contains("\"small-one\" (test)", body, StringComparison.Ordinal);
		Assert.Contains("\"small-two\" (test)", body, StringComparison.Ordinal);
		Assert.DoesNotContain("2.1 — partial", body, StringComparison.Ordinal);
		AssertTestBlockBudgetSlack(result);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RealProcessTestBlockTitlesUseOnlyProtectedReturnedText(bool hidePrivateData)
	{
		const string manual = "opaque-suite-value";
		const string marker = "TEST_BLOCK_TRUST_SENTINEL";
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("protected-blocks-project");
		var data = workspace.CreateDirectory("protected-blocks-data");
		var path = "suite-" + marker + ".js";
		workspace.WriteFile("protected-blocks-project/" + path, LargeTestBlocks(manual, Secret, PrivateEmail));
		workspace.WriteFile("protected-blocks-project/helper.js", "export const helper = 1;");
		var store = new ProjectProfileStore(() => data);
		store.SaveProfile(project, new ProjectSelectionProfile([], [".js"],
			hidePrivateData ? [IgnoreOptionId.HidePrivateData] : [], SelectedPaths: null));
		Assert.True(MarkedSecretValueNormalizer.TryCreate(manual, out var normalized, out _));
		Assert.True((await store.AddMarkAsync(project, new MarkedSecretProfileEntry(normalized.Hash, null, normalized.Length),
			TestContext.Current.CancellationToken)).Succeeded);
		await using var server = await ActualMcpProcess.StartAsync(project, data, arguments: ["--live"]);
		foreach (var batch in new[] { false, true })
		{
			var result = await CallAsync(server, "get_file", batch
				? new() { ["requests"] = new[] { new { path } } }
				: new() { ["path"] = path });
			Assert.NotEqual(true, result.IsError);
			AssertTestBlockBudgetSlack(result);
			var text = AllProcessText(result);
			var body = BatchData(result);
			Assert.Contains("Structure:", body, StringComparison.Ordinal);
			Assert.Contains("(describe)", body, StringComparison.Ordinal);
			Assert.Contains("(test)", body, StringComparison.Ordinal);
			Assert.Contains("DEVPROJEX_REDACTED[", body, StringComparison.Ordinal);
			Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
			Assert.DoesNotContain(manual, text, StringComparison.Ordinal);
			if (hidePrivateData) Assert.DoesNotContain(PrivateEmail, text, StringComparison.Ordinal);
			var trusted = Regex.Replace(text, @"<untrusted-data-(?<nonce>[a-f0-9]{24})>\n[\s\S]*?\n</untrusted-data-\k<nonce>>", string.Empty,
				RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
			Assert.DoesNotContain(marker, trusted, StringComparison.Ordinal);
		}
	}

	private static void AssertTestBlockBudgetSlack(ModelContextProtocol.Protocol.CallToolResult result)
	{
		var normalized = AllProcessText(result).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
		foreach (var newline in new[] { "\n", "\r\n" })
		{
			var measured = normalized.Replace("\n", newline, StringComparison.Ordinal).Length;
			Assert.True(50_000 - measured >= 100, measured.ToString());
		}
	}

	private static string LargeTestBlocks(string suite, string first, string last)
	{
		var lines = new List<string>
		{
			"function actualSymbol() { return 1; }",
			"import { helper } from './helper.js';",
			"describe('" + suite + "', () => {",
			"  test('" + first + "', () => {});"
		};
		lines.AddRange(Enumerable.Range(0, 400).Select(index => "  // padding-" + index.ToString("D4") + " " + new string('x', 90)));
		lines.Add("  test('" + last + "', () => {});");
		lines.Add("});");
		return string.Join('\n', lines);
	}
}
