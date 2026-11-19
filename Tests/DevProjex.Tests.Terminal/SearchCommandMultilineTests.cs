namespace DevProjex.Tests.Terminal;

public sealed class SearchCommandMultilineTests
{
	[Fact]
	public async Task RegexPatternSpanningTwoLinesMatchesOnceInJson()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/package.json", """
			{
			  "exports": {
			    ".": {
			      "import": "./index.js"
			    }
			  }
			}
			""");

		using var document = await SearchAsync(
			workspace,
			project,
			"\"exports\": \\{\\n\\s*\"\\.\": \\{",
			SearchMode.Regex);

		var boundary = document.RootElement.GetProperty("searchBoundary");
		Assert.True(boundary.GetProperty("complete").GetBoolean());
		Assert.Equal(1, boundary.GetProperty("encounteredMatches").GetInt32());
		var match = Assert.Single(document.RootElement.GetProperty("matches").EnumerateArray());
		Assert.Equal("package.json", match.GetProperty("path").GetString());
		Assert.Equal(2, match.GetProperty("line").GetInt32());
	}

	[Fact]
	public async Task LineFeedPatternMatchesCrlfFileAsLfFile()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var path = Path.Combine(project, "notes.txt");
		File.WriteAllText(path, "first\r\nneedle\r\nthird\r\n");

		using var document = await SearchAsync(workspace, project, "needle\\nthird", SearchMode.Regex);

		var boundary = document.RootElement.GetProperty("searchBoundary");
		Assert.True(boundary.GetProperty("complete").GetBoolean());
		var match = Assert.Single(document.RootElement.GetProperty("matches").EnumerateArray());
		Assert.Equal("notes.txt", match.GetProperty("path").GetString());
		Assert.Equal(2, match.GetProperty("line").GetInt32());
	}

	[Fact]
	public async Task TextFormatReportsTheMultilineNoticeAndTheMatchedRange()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/package.json", """
			{
			  "exports": {
			    ".": {
			      "import": "./index.js"
			    }
			  }
			}
			""");

		var text = await RenderAsync(
			workspace,
			project,
			"\"exports\": \\{\\n\\s*\"\\.\": \\{",
			SearchMode.Regex,
			SearchOutputFormat.Text);

		Assert.Contains(
			"[Search] multi-line pattern: a match may span lines and counts once at its first line.",
			text,
			StringComparison.Ordinal);
		Assert.Contains("2:", text, StringComparison.Ordinal);
		Assert.Contains("3:", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task OrdinaryPatternKeepsPerLineMatchingAndNoMultilineNotice()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/notes.txt", "needle one\nneedle two\n");

		var text = await RenderAsync(workspace, project, "needle", SearchMode.Text, SearchOutputFormat.Text);
		using var document = await SearchAsync(workspace, project, "needle", SearchMode.Text);

		Assert.DoesNotContain("[Search] multi-line pattern", text, StringComparison.Ordinal);
		var boundary = document.RootElement.GetProperty("searchBoundary");
		Assert.Equal(2, boundary.GetProperty("encounteredMatches").GetInt32());
	}

	private static async Task<JsonDocument> SearchAsync(
		TemporaryDirectory workspace,
		string project,
		string pattern,
		SearchMode mode)
	{
		var payload = await RenderAsync(workspace, project, pattern, mode, SearchOutputFormat.Json);
		return JsonDocument.Parse(payload);
	}

	private static async Task<string> RenderAsync(
		TemporaryDirectory workspace,
		string project,
		string pattern,
		SearchMode mode,
		SearchOutputFormat format)
	{
		using var services = new TerminalServiceFactory(
			() => workspace.CreateDirectory("app-data")).Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			ProjectSelectionSpec.Standard,
			includeOutputMetrics: false,
			cancellationToken: TestContext.Current.CancellationToken);
		var request = new SearchCommandRequest(
			project,
			pattern,
			plan.Selection,
			mode,
			MaximumResults: 10,
			SearchBodyCharacters: 0,
			Format: format,
			OutputPath: null,
			Output: new TerminalOutputOptions());
		return await new SearchCommandHandler(services, new TestTerminalEnvironment())
			.RenderSearchForPlanAsync(
				plan,
				request,
				maximumInspectedBytes: 1_024 * 1_024,
				cancellationToken: TestContext.Current.CancellationToken);
	}
}
