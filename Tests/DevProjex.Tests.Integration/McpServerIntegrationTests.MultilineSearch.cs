namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	private const string MultilineSearchNotice =
		"[Search] multi-line pattern: a match may span lines and counts once at its first line.";
	private const string PerLineSearchNotice =
		"[Search] matching is per line, so a pattern with \\n cannot match; search for each line separately.";

	private const string PackageJson =
		"{\n" +
		"  \"name\": \"demo\",\n" +
		"  \"exports\": {\n" +
		"    \".\": \"./dist/index.js\",\n" +
		"    \"./middleware\": \"./dist/middleware.js\"\n" +
		"  },\n" +
		"  \"license\": \"MIT\"\n" +
		"}\n";

	[Fact]
	public async Task SearchMatchesAPatternAcrossTwoLinesAsOneMatchWithItsLineRange()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "package.json"), PackageJson);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = "\"exports\": \\{\\n\\s*\"\\.\"",
			["context_lines"] = 1
		});
		var text = Normalize(Text(result));
		var body = ExtractSpotlightBody(text);

		Assert.NotEqual(true, result.IsError);
		Assert.Contains(
			"package.json\n" +
			"2-  \"name\": \"demo\",\n" +
			"3:  \"exports\": {\n" +
			"4:    \".\": \"./dist/index.js\",\n" +
			"5-    \"./middleware\": \"./dist/middleware.js\"",
			body,
			StringComparison.Ordinal);
		Assert.Contains("matches written=1", text, StringComparison.Ordinal);
		Assert.Contains("[Search boundary] complete", text, StringComparison.Ordinal);
		AssertMultilineNotices(text, body);
	}

	[Theory]
	[InlineData("(?s)\"exports\".*?middleware\"")]
	[InlineData("\"exports\"[\\s\\S]*?middleware\"")]
	[InlineData("\"exports\"[\\w\\W]*?middleware\"")]
	[InlineData("(?is:\"EXPORTS\".*?middleware\")")]
	public async Task SearchSwitchesToWholeFileMatchingForLineBreakCoveringPatterns(string pattern)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "package.json"), PackageJson);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = pattern,
			["context_lines"] = 0
		});
		var text = Normalize(Text(result));
		var body = ExtractSpotlightBody(text);

		Assert.NotEqual(true, result.IsError);
		Assert.Contains(
			"package.json\n" +
			"3:  \"exports\": {\n" +
			"4:    \".\": \"./dist/index.js\",\n" +
			"5:    \"./middleware\": \"./dist/middleware.js\"",
			body,
			StringComparison.Ordinal);
		Assert.DoesNotContain("6:", body, StringComparison.Ordinal);
		Assert.Contains("matches written=1", text, StringComparison.Ordinal);
		AssertMultilineNotices(text, body);
	}

	[Fact]
	public async Task SearchKeepsPerLineMatchingForAPatternWithoutLineBreakTriggers()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "package.json"), PackageJson);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = "\"exports\".*middleware",
			["context_lines"] = 0
		});
		var text = Normalize(Text(result));

		Assert.NotEqual(true, result.IsError);
		Assert.Contains("matches written=0", text, StringComparison.Ordinal);
		Assert.DoesNotContain(MultilineSearchNotice, text, StringComparison.Ordinal);
		Assert.DoesNotContain(PerLineSearchNotice, text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SearchShowsTwentyLinesOfALongerMatchAndCountsTheRest()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var lines = new List<string> { "before", "BEGIN" };
		lines.AddRange(Enumerable.Range(1, 28).Select(static index => $"item {index}"));
		lines.Add("END");
		lines.Add("after");
		File.WriteAllText(Path.Combine(project, "list.txt"), string.Join('\n', lines) + "\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = "BEGIN[\\s\\S]*?END",
			["context_lines"] = 1
		});
		var text = Normalize(Text(result));
		var body = ExtractSpotlightBody(text);

		Assert.NotEqual(true, result.IsError);
		// BEGIN is line 2 and END line 31: lines 2-21 are shown, 22-31 are counted.
		Assert.Contains("1-before\n2:BEGIN\n3:item 1\n", body, StringComparison.Ordinal);
		Assert.Contains("21:item 19\n(+10 more matched lines)\n32-after", body, StringComparison.Ordinal);
		Assert.DoesNotContain("22:", body, StringComparison.Ordinal);
		Assert.DoesNotContain("31:END", body, StringComparison.Ordinal);
		Assert.Contains("matches written=1", text, StringComparison.Ordinal);
		AssertMultilineNotices(text, body);
	}

	[Fact]
	public async Task SearchCountsEachMultilineMatchOnceAndReportsPartialAtTheResultLimit()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var content = string.Concat(Enumerable.Range(1, 5).Select(static index => $"open {index}\nbody\nclose\n\nfiller\n"));
		File.WriteAllText(Path.Combine(project, "blocks.txt"), content);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = "open \\d\\nbody\\nclose",
			["context_lines"] = 0,
			["max_results"] = 2
		});
		var text = Normalize(Text(result));
		var body = ExtractSpotlightBody(text);

		Assert.NotEqual(true, result.IsError);
		Assert.Contains("1:open 1\n2:body\n3:close", body, StringComparison.Ordinal);
		Assert.Contains("6:open 2\n7:body\n8:close", body, StringComparison.Ordinal);
		Assert.DoesNotContain("open 3", body, StringComparison.Ordinal);
		Assert.Contains("[Search boundary] partial", text, StringComparison.Ordinal);
		Assert.Contains("matches retained=5/5", text, StringComparison.Ordinal);
		Assert.Contains("matches written=2", text, StringComparison.Ordinal);
		Assert.Contains("[3 additional observed matches not shown.]", text, StringComparison.Ordinal);
		Assert.Contains("[Search observed] matches=5 · matching-files=1", text, StringComparison.Ordinal);
		AssertMultilineNotices(text, body);
	}

	[Fact]
	public async Task SearchNeverMatchesAcrossARedactedSecretAndShowsContextProtected()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(
			Path.Combine(project, "settings.txt"),
			$"start\nopen\nclose\ntoken = \"{Secret}\"\nfinish\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var acrossSecret = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = "close[\\s\\S]*?finish",
			["context_lines"] = 0
		});
		var nearSecret = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = "open\\nclose",
			["context_lines"] = 1
		});
		var acrossText = Normalize(Text(acrossSecret));
		var nearText = Normalize(Text(nearSecret));
		var nearBody = ExtractSpotlightBody(nearText);

		Assert.Contains("matches written=0", acrossText, StringComparison.Ordinal);
		Assert.DoesNotContain(Secret, acrossText, StringComparison.Ordinal);
		Assert.Contains("1-start\n2:open\n3:close\n4-token = \"DEVPROJEX_REDACTED[", nearBody, StringComparison.Ordinal);
		Assert.Contains("matches written=1", nearText, StringComparison.Ordinal);
		Assert.DoesNotContain(Secret, nearText, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SymbolSearchKeepsRefusingAMultilinePattern()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "Service.cs"), "public sealed class Service\n{\n}\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = "class Service\\n\\{",
			["symbols"] = true
		});

		Assert.True(result.IsError);
		Assert.DoesNotContain(MultilineSearchNotice, AllText(result), StringComparison.Ordinal);
	}

	private static void AssertMultilineNotices(string text, string body)
	{
		var trusted = text.Replace(body, string.Empty, StringComparison.Ordinal);
		Assert.Contains(MultilineSearchNotice, trusted, StringComparison.Ordinal);
		Assert.DoesNotContain(MultilineSearchNotice, body, StringComparison.Ordinal);
		Assert.DoesNotContain(PerLineSearchNotice, text, StringComparison.Ordinal);
		// Paths and project text stay inside the untrusted block.
		foreach (var projectText in new[] { "package.json", "list.txt", "blocks.txt", "exports", "BEGIN", "open 1" })
			Assert.DoesNotContain(projectText, trusted, StringComparison.Ordinal);
	}

	private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}
