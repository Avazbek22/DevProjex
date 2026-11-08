using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessOffersOneReadableBatchForMatchedDeclarationsAndContext()
	{
		const string marker = "Zq9BatchMarkerName";
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("search-batch");
		foreach (var name in new[] { marker, "Other" })
			workspace.WriteFile($"search-batch/{name}.cs",
				$"namespace Demo;\nclass {name}\n{{\n public void Run()\n {{\n  System.Console.WriteLine(\"batchNeedle\");\n }}\n}}\n");
		workspace.WriteFile("search-batch/plain.log", "first\nbefore\nbatchNeedle\nafter\nlast\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		var response = await CallAsync(server, "search_project", new Dictionary<string, object?>
		{
			["pattern"] = "batchNeedle",
			["context_lines"] = 1
		});
		Assert.NotEqual(true, response.IsError);
		var text = AllProcessText(response);
		var body = SpotlightBody(Normalize(text));
		const string prefix = "Read these in one call: get_file ";
		var line = body.TrimEnd().Split('\n')[^1];
		Assert.StartsWith(prefix, line, StringComparison.Ordinal);
		using var json = JsonDocument.Parse(line[prefix.Length..]);
		var requests = json.RootElement.GetProperty("requests").EnumerateArray().ToArray();
		Assert.Equal(3, requests.Length);
		Assert.Equal(new[] { "Other.cs", marker + ".cs", "plain.log" }.Order(StringComparer.Ordinal),
			requests.Select(item => item.GetProperty("path").GetString()).Order(StringComparer.Ordinal));
		var offsets = requests.Select(item => body.IndexOf(item.GetProperty("path").GetString() + "\n", StringComparison.Ordinal)).ToArray();
		Assert.Equal(offsets.Order(), offsets);
		foreach (var request in requests)
		{
			var path = request.GetProperty("path").GetString()!;
			var range = Assert.Single(request.GetProperty("ranges").EnumerateArray());
			Assert.Equal(path.EndsWith(".cs", StringComparison.Ordinal) ? 4 : 2, range.GetProperty("start_line").GetInt32());
			Assert.Equal(path.EndsWith(".cs", StringComparison.Ordinal) ? 7 : 4, range.GetProperty("end_line").GetInt32());
		}
		Assert.DoesNotContain(marker, OutsideSelectionTrustedText(text), StringComparison.Ordinal);
		var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(line[prefix.Length..])!;
		var read = await CallAsync(server, "get_file", arguments);
		Assert.NotEqual(true, read.IsError);
		var readText = Normalize(AllProcessText(read));
		var sections = System.Text.RegularExpressions.Regex.Matches(readText,
			@"File: ([^\n]+)\n(?:(?!File: )[^\n]*\n)*?Lines: (\d+)-(\d+) of",
			System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2));
		Assert.Equal(3, sections.Count);
		foreach (var request in requests)
		{
			var range = request.GetProperty("ranges")[0];
			Assert.Contains(sections.Cast<System.Text.RegularExpressions.Match>(), section =>
				section.Groups[1].Value == request.GetProperty("path").GetString() &&
				section.Groups[2].Value == range.GetProperty("start_line").GetInt32().ToString() &&
				section.Groups[3].Value == range.GetProperty("end_line").GetInt32().ToString());
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RealProcessOmitsTheBatchReadWhenAtMostOneFileMatches(bool matches)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("single-search");
		workspace.WriteFile("single-search/one.log", "oneFileNeedle\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		var response = await CallAsync(server, "search_project", new Dictionary<string, object?>
		{
			["pattern"] = matches ? "oneFileNeedle" : "absentNeedle"
		});
		Assert.NotEqual(true, response.IsError);
		Assert.DoesNotContain("Read these in one call:", AllProcessText(response), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessBatchReadKeepsTheProjectAndDelegatedExclusions()
	{
		using var workspace = new TemporaryDirectory();
		var first = workspace.CreateDirectory("first");
		var second = workspace.CreateDirectory("second");
		workspace.WriteFile("second/visible.log", "batchScopeNeedle\n");
		workspace.WriteFile("second/.hidden.log", "batchScopeNeedle\n");
		await using var server = await ActualMcpProcess.StartAsync(first, workspace.CreateDirectory("data"),
			["--root", second, "--allow-agent-exclusions", "--exclude", "dot-files"]);
		var response = await CallAsync(server, "search_project", new Dictionary<string, object?>
		{
			["project"] = second,
			["pattern"] = "batchScopeNeedle",
			["exclusions"] = Array.Empty<string>()
		});
		Assert.NotEqual(true, response.IsError);
		var line = SpotlightBody(Normalize(AllProcessText(response))).TrimEnd().Split('\n')[^1];
		const string prefix = "Read these in one call: get_file ";
		Assert.StartsWith(prefix, line, StringComparison.Ordinal);
		var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(line[prefix.Length..])!;
		Assert.True(arguments.ContainsKey("project"));
		Assert.True(arguments.ContainsKey("exclusions"));
		var read = await CallAsync(server, "get_file", arguments);
		Assert.NotEqual(true, read.IsError);
		Assert.Contains("File: .hidden.log", AllProcessText(read), StringComparison.Ordinal);
		Assert.Contains("File: visible.log", AllProcessText(read), StringComparison.Ordinal);
	}
}
