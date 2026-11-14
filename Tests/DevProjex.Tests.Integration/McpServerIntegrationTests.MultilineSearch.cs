using DevProjex.Mcp;

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
	public async Task SymbolSearchFallsBackToTextForAMultilineRegularExpression()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "Service.cs"), "public sealed class Service\n{\n}\n");
		async Task<string> SearchAsync(bool symbols)
		{
			await using var server = await McpTestServer.StartAsync(project, workspace.Path);
			var arguments = new Dictionary<string, object?> { ["pattern"] = "class Service\\n\\{" };
			if (symbols) arguments["symbols"] = true;
			var result = await server.CallAsync("search_project", arguments);
			Assert.NotEqual(true, result.IsError);
			var text = Normalize(Text(result));
			AssertMultilineNotices(text, ExtractSpotlightBody(text));
			return Normalize(WithoutFenceNonce(result));
		}
		var text = await SearchAsync(false);
		var fallback = await SearchAsync(true);
		Assert.Contains("matches written=1", text, StringComparison.Ordinal);
		Assert.Contains(McpSearchSymbols.RegularExpressionFallbackNotice, fallback, StringComparison.Ordinal);
		Assert.Equal(text, fallback.Replace(McpSearchSymbols.RegularExpressionFallbackNotice + "\n", string.Empty, StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("open\\nclose")]
	[InlineData("open\\r\\nclose")]
	[InlineData("(?s)open$")]
	[InlineData("(?s)^open$\\n^close$")]
	public async Task SearchMatchesLineBreaksInACrlfFileWithTheRightLineRange(string pattern)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "crlf.txt"), "alpha\r\nopen\r\nclose\r\nend\r\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = pattern,
			["context_lines"] = 0
		});
		var text = Normalize(Text(result));
		var body = ExtractSpotlightBody(text);

		Assert.NotEqual(true, result.IsError);
		Assert.Contains("crlf.txt\n2:open", body, StringComparison.Ordinal);
		Assert.DoesNotContain("\r", body, StringComparison.Ordinal);
		Assert.DoesNotContain("1:alpha", body, StringComparison.Ordinal);
		Assert.DoesNotContain("4:end", body, StringComparison.Ordinal);
		Assert.Contains("matches written=1", text, StringComparison.Ordinal);
		if (pattern.Contains("close", StringComparison.Ordinal))
			Assert.Contains("2:open\n3:close", body, StringComparison.Ordinal);
		else
			Assert.DoesNotContain("3:close", body, StringComparison.Ordinal);
		AssertMultilineNotices(text, body);
	}

	[Fact]
	public async Task SearchKeepsASecretProtectedInACrlfFile()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(
			Path.Combine(project, "settings.txt"),
			$"start\r\nopen\r\nclose\r\ntoken = \"{Secret}\"\r\nfinish\r\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var acrossSecret = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = "close[\\s\\S]*?finish",
			["context_lines"] = 0
		});
		var beforeSecret = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = "close\\ntoken = ",
			["context_lines"] = 0
		});
		var acrossText = Normalize(Text(acrossSecret));
		var beforeText = Normalize(Text(beforeSecret));
		var beforeBody = ExtractSpotlightBody(beforeText);

		Assert.Contains("matches written=0", acrossText, StringComparison.Ordinal);
		Assert.DoesNotContain(Secret, acrossText, StringComparison.Ordinal);
		Assert.Contains("3:close\n4:token = \"DEVPROJEX_REDACTED[", beforeBody, StringComparison.Ordinal);
		Assert.Contains("matches written=1", beforeText, StringComparison.Ordinal);
		Assert.DoesNotContain(Secret, beforeText, StringComparison.Ordinal);
	}

	private static void AssertMultilineNotices(string text, string body)
	{
		var trusted = text.Replace(body, string.Empty, StringComparison.Ordinal);
		Assert.Contains(MultilineSearchNotice, trusted, StringComparison.Ordinal);
		Assert.DoesNotContain(MultilineSearchNotice, body, StringComparison.Ordinal);
		Assert.DoesNotContain(PerLineSearchNotice, text, StringComparison.Ordinal);
		// Paths and project text stay inside the untrusted block.
		foreach (var projectText in new[] { "package.json", "list.txt", "blocks.txt", "crlf.txt", "exports", "BEGIN", "open 1" })
			Assert.DoesNotContain(projectText, trusted, StringComparison.Ordinal);
	}

	private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}
