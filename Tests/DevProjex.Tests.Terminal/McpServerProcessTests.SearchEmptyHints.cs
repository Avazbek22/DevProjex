namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	private const string SearchPathPatternNotice =
		"[Search] the pattern looks like a path, but search_project matches file content; find files by name with get_tree include_patterns.";
	private const string SearchPerLinePatternNotice =
		"[Search] matching is per line, so a pattern with \\n cannot match; search for each line separately.";

	[Fact]
	public async Task RealProcessKeepsMissingPathGuidanceWhenOtherRequestedFilesMatch()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("search-missing-path");
		workspace.WriteFile("search-missing-path/plain.log", "needle\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		var response = await CallAsync(server, "search_project", new Dictionary<string, object?>
		{
			["pattern"] = "needle",
			["paths"] = new[] { "missing.log", "plain.log" }
		});

		Assert.NotEqual(true, response.IsError);
		var text = AllProcessText(response);
		Assert.Contains("matches written=1", text, StringComparison.Ordinal);
		Assert.Contains(NameSearchNotice, text, StringComparison.Ordinal);
		Assert.DoesNotContain(SearchPathPatternNotice, text, StringComparison.Ordinal);
		Assert.DoesNotContain(SearchPerLinePatternNotice, text, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(@"def a\nfoo", true, false)]
	[InlineData("def a\nfoo", true, false)]
	[InlineData("^tests/client/", false, true)]
	[InlineData("ordinaryAbsent", false, false)]
	[InlineData("tests/(client|server)/", false, false)]
	[InlineData("thing.verylongextension", false, true)]
	[InlineData(@"file\.cs$", false, true)]
	[InlineData("^Zq9HintMarker/never/", false, true)]
	public async Task RealProcessExplainsOnlyAnEmptyPerLineOrPathPattern(string pattern, bool perLine, bool path)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("search-hints");
		workspace.WriteFile("search-hints/plain.log", "needle\nfoo/bar\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		var response = await CallAsync(server, "search_project", new Dictionary<string, object?> { ["pattern"] = pattern });

		Assert.NotEqual(true, response.IsError);
		var trusted = OutsideSelectionTrustedText(AllProcessText(response));
		Assert.Equal(perLine, trusted.Contains(SearchPerLinePatternNotice, StringComparison.Ordinal));
		Assert.Equal(path, trusted.Contains(SearchPathPatternNotice, StringComparison.Ordinal));
		Assert.DoesNotContain("[Name search]", trusted, StringComparison.Ordinal);
		Assert.DoesNotContain("Zq9HintMarker", trusted, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("^foo/bar$")]
	[InlineData(@"needle|\n")]
	[InlineData("needle|\n")]
	public async Task RealProcessAddsNoEmptyPatternHintWhenAnAlternativeMatches(string pattern)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("search-hits");
		workspace.WriteFile("search-hits/plain.log", "needle\nfoo/bar\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		var response = await CallAsync(server, "search_project", new Dictionary<string, object?> { ["pattern"] = pattern });

		Assert.NotEqual(true, response.IsError);
		var text = AllProcessText(response);
		Assert.Contains("matches written=1", text, StringComparison.Ordinal);
		Assert.DoesNotContain(SearchPerLinePatternNotice, text, StringComparison.Ordinal);
		Assert.DoesNotContain(SearchPathPatternNotice, text, StringComparison.Ordinal);
	}
}
