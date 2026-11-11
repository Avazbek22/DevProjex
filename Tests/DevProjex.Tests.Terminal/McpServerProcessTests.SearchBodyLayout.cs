using System.Text;
using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessPreservesTheExactNameMatchAtTheEndOfTheSearchSlice()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = new StringBuilder("sealed class Sample\n{\n    string Configure()\n    {\n");
		for (var index = 0; index < 100; index++)
			source.Append("        var value").Append(index.ToString("D3"))
				.Append(" = \"Needle ").Append(new string('x', 105)).Append("\";\n");
		source.Append("        return value000;\n    }\n    string Needle() => \"Needle ")
			.Append(new string('y', 1_000)).Append("\";\n}\n");
		workspace.WriteFile("project/Sample.cs", source.ToString());
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		var text = Normalize(AllProcessText(await CallAsync(server, "search_project", new Dictionary<string, object?>
		{
			["pattern"] = "Needle",
			["context_lines"] = 0,
			["max_results"] = 200
		})));

		Assert.Contains("107:    string Needle()", text, StringComparison.Ordinal);
		Assert.Contains("Sample.Needle 107-107", text, StringComparison.Ordinal);
		Assert.Contains("matches written=101", text, StringComparison.Ordinal);
		Assert.DoesNotContain("symbol\":\"Sample.Configure\"", text, StringComparison.Ordinal);
		Assert.InRange(SpotlightBody(text).Length, 1, 16_000);
	}

	[Fact]
	public async Task RealProcessKeepsEveryMatchingFileWhenTheBodyWouldNeedItsSpace()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Target.cs", "sealed class Target\n{\n    string Needle() => \"Needle " +
			new string('y', 1_000) + "\";\n}\n");
		for (var index = 0; index < 25; index++)
			workspace.WriteFile($"project/file{index:D2}.txt", "Needle " + new string('x', 550) + "\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		var text = Normalize(AllProcessText(await CallAsync(server, "search_project", new Dictionary<string, object?>
		{
			["pattern"] = "Needle",
			["context_lines"] = 0,
			["max_results"] = 200
		})));

		for (var index = 0; index < 25; index++)
			Assert.Contains($"file{index:D2}.txt\n1:Needle ", text, StringComparison.Ordinal);
		Assert.Contains("3:    string Needle()", text, StringComparison.Ordinal);
		Assert.Contains("matches written=26", text, StringComparison.Ordinal);
		Assert.DoesNotContain("[Search stored]", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Best declaration body", text, StringComparison.Ordinal);
		Assert.InRange(SpotlightBody(text).Length, 1, 16_000);
	}

	[Fact]
	public async Task RealProcessPrintsOverlappingDeclarationContextOnlyOnce()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Target.cs", "sealed class Target\n{\n    string Read()\n    {\n" +
			"        var before = \"context-before\";\n        var marker = \"needle\";\n" +
			"        return before + marker;\n    }\n}\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		var text = Normalize(AllProcessText(await CallAsync(server, "search_project", new Dictionary<string, object?>
		{
			["pattern"] = "needle",
			["context_lines"] = 3
		})));

		Assert.Contains("6:        var marker", text, StringComparison.Ordinal);
		Assert.Contains("symbol\":\"Target.Read\"", text, StringComparison.Ordinal);
		Assert.Equal(1, SpotlightBody(text).Split("var before =", StringSplitOptions.None).Length - 1);
		Assert.Equal(1, SpotlightBody(text).Split("return before + marker;", StringSplitOptions.None).Length - 1);
		Assert.Contains("matches written=1", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessReservedDistributionKeepsEveryMatchingFileAndTheStoredWindows()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Target.cs", "sealed class Target\n{\n    string Needle() => \"Needle " +
			new string('y', 1_000) + "\";\n}\n");
		for (var index = 0; index < 25; index++)
			workspace.WriteFile($"project/file{index:D2}.txt", string.Concat(Enumerable.Range(1, 2)
				.Select(line => $"Needle file-{index:D2}-line-{line} {new string('x', 525)}\n")));
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		var (text, page) = await SearchAndReadAsync(server);
		await using var withoutBody = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("without-body-data"),
			["--search-body-chars", "off"]);
		var (withoutBodyText, withoutBodyPage) = await SearchAndReadAsync(withoutBody);

		var expectedMatches = new[] { "3:    string Needle() => \"Needle " + new string('y', 1_000) + "\";" }
			.Concat(Enumerable.Range(0, 25).Select(index => $"1:Needle file-{index:D2}-line-1 {new string('x', 525)}"))
			.ToArray();
		Assert.Equal(expectedMatches, WrittenMatches(text));
		Assert.Equal(expectedMatches, WrittenMatches(withoutBodyText));
		for (var index = 0; index < 25; index++)
			Assert.Contains($"file{index:D2}.txt\n1:Needle ", text, StringComparison.Ordinal);
		Assert.Contains("[Search observed] matches=51 · matching-files=26", text, StringComparison.Ordinal);
		Assert.Contains("matches retained=51/51 · matches written=26", text, StringComparison.Ordinal);
		Assert.Contains("[25 additional observed matches not shown.]", text, StringComparison.Ordinal);

		var expectedDistribution = "Withheld matches by file:\n" + string.Join('\n', Enumerable.Range(0, 20)
			.Select(index => $"1 file{index:D2}.txt")) + "\nand 5 more file(s)\n5 matches in those files";
		Assert.Equal(expectedDistribution, Distribution(text));
		Assert.Equal(expectedDistribution, Distribution(withoutBodyText));
		var listedCounts = Regex.Matches(Distribution(text), @"(?m)^(\d+) file\d{2}\.txt$",
			RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)).Sum(static match => int.Parse(match.Groups[1].Value));
		var remainder = Regex.Match(Distribution(text), @"(\d+) matches in those files",
			RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
		Assert.True(remainder.Success, text);
		Assert.Equal(51, WrittenMatches(text).Length + listedCounts + int.Parse(remainder.Groups[1].Value));

		// Reserving the distribution changes inline placement, while the original stored windows stay unchanged.
		var expectedStored = new StringBuilder();
		for (var index = 1; index < 25; index++)
		{
			expectedStored.Append($"file{index:D2}.txt\n");
			for (var line = 1; line <= 2; line++)
				expectedStored.Append($"{line}:Needle file-{index:D2}-line-{line} {new string('x', 525)}\n");
		}
		Assert.Equal(expectedStored.ToString(), ExtractSpotlightBody(page));
		Assert.Equal(expectedStored.ToString(), ExtractSpotlightBody(withoutBodyPage));
		Assert.Contains("matches=48 · files=24; retained match windows only", text, StringComparison.Ordinal);
		Assert.DoesNotContain("[Showing lines", page, StringComparison.Ordinal);
		Assert.DoesNotContain("2:Needle file-00-line-2", text + page, StringComparison.Ordinal);
		Assert.InRange(ExtractSpotlightBody(text).Length, 1, 16_000);

		static string[] WrittenMatches(string response) => Regex.Matches(ExtractSpotlightBody(response), @"(?m)^\d+:.*$",
			RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)).Select(static match => match.Value).ToArray();

		static string Distribution(string response)
		{
			var body = ExtractSpotlightBody(response);
			return body[body.IndexOf("Withheld matches by file:", StringComparison.Ordinal)..].TrimEnd('\n');
		}

		static async Task<(string Text, string Page)> SearchAndReadAsync(ActualMcpProcess process)
		{
			var result = await CallAsync(process, "search_project", new Dictionary<string, object?>
			{
				["pattern"] = "Needle",
				["context_lines"] = 0,
				["max_results"] = 200
			});
			Assert.NotEqual(true, result.IsError);
			var response = Normalize(AllProcessText(result));
			var stored = Regex.Match(response, @"\[Search stored\] pack_id=([0-9a-f]+)", RegexOptions.CultureInvariant,
				TimeSpan.FromSeconds(2));
			Assert.True(stored.Success, response);
			var read = await CallAsync(process, "read_pack", new Dictionary<string, object?> { ["pack_id"] = stored.Groups[1].Value });
			Assert.NotEqual(true, read.IsError);
			return (response, Normalize(AllProcessText(read)));
		}
	}
}
