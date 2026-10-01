using System.Text.RegularExpressions;
using DevProjex.Application.Ranking;

namespace DevProjex.Tests.Terminal;

public sealed partial class TerminalWorkspaceGrepRevealAndBudgetTests
{
	private const string GithubToken = "ghp_a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL";
	private const long SmallBudget = 1_500;

	[Fact]
	public async Task GrepReturnsTheCliMatchesWithDeclarationsForTheCurrentSelection()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateSearchProject(workspace);
		var (controller, services) = CreateController(workspace);
		using var state = await OpenAsync(controller, project);
		var plan = await controller.BuildCurrentPlanAsync(state, TestContext.Current.CancellationToken);

		var result = await controller.SearchProjectAsync(
			plan,
			"needle",
			SearchMode.Text,
			SearchCommandHandler.DefaultMaximumResults,
			TestContext.Current.CancellationToken);
		var output = TerminalProjectSearchOutput.Format(result, services.Localization);

		var match = Assert.Single(result.Matches);
		Assert.Equal("src/App.cs", match.Path);
		Assert.Equal(5, match.Line);
		Assert.Equal("Demo.Sample.Run", match.Declaration);
		Assert.StartsWith(
			services.Localization.Format("Terminal.Tui.Command.Grep.Result.Summary", "needle", 1, 1),
			output,
			StringComparison.Ordinal);
		Assert.Contains("src/App.cs:5 — Demo.Sample.Run", output, StringComparison.Ordinal);
		Assert.Contains("  \"needle value\";", output, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GrepShowsTabIndentedMatchesWithoutEscapedIndentation()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateSearchProject(workspace);
		workspace.WriteFile("project/src/Tabbed.cs", "class Tabbed\n{\n\t\tstring marker = \"tabbed\tmarker\";\n}\n");
		var (controller, services) = CreateController(workspace);
		using var state = await OpenAsync(controller, project);
		var plan = await controller.BuildCurrentPlanAsync(state, TestContext.Current.CancellationToken);

		var result = await SearchAsync(controller, plan, "tabbed", SearchMode.Text);
		var output = TerminalProjectSearchOutput.Format(result, services.Localization);

		Assert.Contains("\n  string marker = \"tabbed\\tmarker\";", output, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GrepKeepsTheCliTextRegexAndSymbolModesDistinct()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateSearchProject(workspace);
		var (controller, _) = CreateController(workspace);
		using var state = await OpenAsync(controller, project);
		var plan = await controller.BuildCurrentPlanAsync(state, TestContext.Current.CancellationToken);

		var literal = await SearchAsync(controller, plan, "needle.*value", SearchMode.Text);
		var regex = await SearchAsync(controller, plan, "needle.*value", SearchMode.Regex);
		var symbol = await SearchAsync(controller, plan, "Run", SearchMode.Symbols);

		Assert.Empty(literal.Matches);
		Assert.Single(regex.Matches);
		Assert.Single(symbol.Matches);
	}

	[Fact]
	public async Task GrepWithoutMatchesSaysSoAndInvalidRegexIsReported()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateSearchProject(workspace);
		var (controller, services) = CreateController(workspace);
		using var state = await OpenAsync(controller, project);
		var plan = await controller.BuildCurrentPlanAsync(state, TestContext.Current.CancellationToken);

		var empty = await SearchAsync(controller, plan, "absent-token", SearchMode.Text);
		var exception = await Assert.ThrowsAsync<SearchCommandException>(() =>
			SearchAsync(controller, plan, "([", SearchMode.Regex));

		Assert.Empty(empty.Matches);
		Assert.Equal(
			services.Localization.Format(
				"Terminal.Tui.Command.Grep.Result.None",
				"absent-token",
				empty.Boundary.InspectedSources),
			TerminalProjectSearchOutput.Format(empty, services.Localization));
		Assert.Equal("DPX-TUI-GREP-PATTERN", exception.Code);
	}

	[Fact]
	public async Task GrepHonorsTheResultLimitAndReportsTheShownShare()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile(
			"project/notes.txt",
			string.Join('\n', Enumerable.Range(1, 5).Select(static index => $"marker line {index}")));
		var (controller, services) = CreateController(workspace);
		using var state = await OpenAsync(controller, project);
		var plan = await controller.BuildCurrentPlanAsync(state, TestContext.Current.CancellationToken);

		var result = await controller.SearchProjectAsync(
			plan,
			"marker",
			SearchMode.Text,
			maximumResults: 2,
			TestContext.Current.CancellationToken);
		var output = TerminalProjectSearchOutput.Format(result, services.Localization);

		Assert.Equal(2, result.Matches.Count);
		Assert.Equal(5, result.Boundary.EncounteredMatches);
		Assert.Contains(
			services.Localization.Format("Terminal.Tui.Command.Grep.Result.Shown", 2, 5),
			output,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task GrepSearchesRedactedContentWhenSecretsAreHidden()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile(
			"project/src/Secrets.cs",
			$"class Secrets {{ const string Token = \"{GithubToken}\"; }}\n");
		var (controller, _) = CreateController(workspace);
		using var state = await OpenAsync(controller, project);

		await ApplyHideSecretsAsync(controller, state, hideSecrets: false);
		var visible = await SearchAsync(
			controller,
			await controller.BuildCurrentPlanAsync(state, TestContext.Current.CancellationToken),
			GithubToken[..12],
			SearchMode.Text);
		await ApplyHideSecretsAsync(controller, state, hideSecrets: true);
		var hidden = await SearchAsync(
			controller,
			await controller.BuildCurrentPlanAsync(state, TestContext.Current.CancellationToken),
			GithubToken[..12],
			SearchMode.Text);

		Assert.Single(visible.Matches);
		Assert.Empty(hidden.Matches);
	}

	[Fact]
	public async Task CopyWithMaxTokensAdmitsFewerFilesThanTheUnbudgetedCopy()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateBudgetProject(workspace);
		var (controller, _) = CreateController(workspace);
		using var state = await OpenAsync(controller, project);

		var full = await controller.BuildCopyPayloadWithBudgetAsync(
			state,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Json,
			budget: null,
			TestContext.Current.CancellationToken);
		var budgeted = await controller.BuildCopyPayloadWithBudgetAsync(
			state,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Json,
			new TerminalContextBudget(SmallBudget, Rank: null),
			TestContext.Current.CancellationToken);

		Assert.Null(full.TokenBudget);
		var report = Assert.IsType<ProjectContextTokenBudgetReport>(budgeted.TokenBudget);
		Assert.Equal(SmallBudget, report.MaximumEstimatedTokens);
		Assert.True(report.SkippedFileCount > 0);
		Assert.True(report.IncludedEstimatedTokens <= SmallBudget);
		Assert.NotNull(full.Payload);
		Assert.NotNull(budgeted.Payload);
		Assert.True(budgeted.Payload.Length < full.Payload.Length);
		using var document = JsonDocument.Parse(budgeted.Payload);
		Assert.Equal(3, CountMarkers(full.Payload));
		Assert.InRange(CountMarkers(budgeted.Payload), 1, 2);
	}

	[Fact]
	public async Task ExportContextWithBudgetAndRankShowsTheBudgetAndWritesOnlyAdmittedFiles()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateBudgetProject(workspace);
		var (controller, services) = CreateController(workspace);
		using var state = await OpenAsync(controller, project);
		var destination = Path.Combine(workspace.CreateDirectory("output"), "context.md");
		var budget = new TerminalContextBudget(SmallBudget, ProjectContextRank.Importance);

		var unbudgeted = await controller.PrepareContextExportAsync(
			state,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Markdown,
			destination,
			overwrite: false,
			TestContext.Current.CancellationToken);
		var summary = await controller.PrepareContextExportAsync(
			state,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Markdown,
			destination,
			overwrite: false,
			TestContext.Current.CancellationToken,
			budget: budget);
		var summaryText = new TerminalWorkspace(services, new TestTerminalEnvironment())
			.BuildExportSummaryText(summary);
		var written = await controller.ExportContextAsync(
			state,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Markdown,
			destination,
			overwrite: false,
			TestContext.Current.CancellationToken,
			budget: budget);
		var exported = await File.ReadAllTextAsync(written, TestContext.Current.CancellationToken);
		var command = TerminalWorkspaceController.BuildEquivalentContextCommand(
			state,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Markdown,
			destination,
			dryRun: true,
			budget);

		Assert.Null(unbudgeted.TokenBudget);
		Assert.Null(unbudgeted.Rank);
		var report = Assert.IsType<ProjectContextTokenBudgetReport>(summary.TokenBudget);
		Assert.True(report.SkippedFileCount > 0);
		Assert.Equal(ProjectContextRank.Importance, summary.Rank);
		Assert.True(summary.EstimatedTokens < unbudgeted.EstimatedTokens);
		Assert.Contains(services.Localization["Terminal.Tui.Export.TokenBudget"], summaryText, StringComparison.Ordinal);
		Assert.Contains(
			TerminalContextBudget.FormatReport(report, services.Localization),
			summaryText,
			StringComparison.Ordinal);
		Assert.Contains(services.Localization["Terminal.Option.Rank"], summaryText, StringComparison.Ordinal);
		Assert.Equal(report.IncludedFileCount, CountMarkers(exported));
		var arguments = string.Join(' ', ArgumentVectorRegex().Matches(command).Select(static match => match.Groups[1].Value));
		Assert.Contains("--max-tokens 1500", arguments, StringComparison.Ordinal);
		Assert.Contains("--rank importance", arguments, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RevealExpandsAnExistingPathLeavesAHidingFilterAndRejectsAMissingPath()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var deep = workspace.WriteFile("project/src/Nested/Deep.cs", "class Deep { }");
		workspace.WriteFile("project/README.md", "# Project");
		var (controller, _) = CreateController(workspace);
		using var state = await OpenAsync(controller, project);

		var row = TerminalWorkspaceSession.RevealTreePathInState(state, "src/Nested/Deep.cs", out var cleared);
		var missing = TerminalWorkspaceSession.RevealTreePathInState(state, "src/Missing.cs", out var missingCleared);
		state.ApplyTreeFilter("README");
		var filteredRow = TerminalWorkspaceSession.RevealTreePathInState(
			state,
			"src/Nested/Deep.cs",
			out var filterCleared);

		Assert.True(row >= 0);
		Assert.True(filteredRow >= 0);
		Assert.Equal(Path.GetFullPath(deep), Path.GetFullPath(state.VisibleRows[filteredRow].Node.FullPath));
		Assert.False(cleared);
		Assert.Equal(-1, missing);
		Assert.False(missingCleared);
		Assert.True(filterCleared);
		Assert.False(state.HasTreeFilter);
	}

	private static async Task<SearchCommandHandler.SearchResult> SearchAsync(
		TerminalWorkspaceController controller,
		ProjectContextPlan plan,
		string pattern,
		SearchMode mode) =>
		await controller.SearchProjectAsync(
			plan,
			pattern,
			mode,
			SearchCommandHandler.DefaultMaximumResults,
			TestContext.Current.CancellationToken);

	private static async Task ApplyHideSecretsAsync(
		TerminalWorkspaceController controller,
		TerminalWorkspaceState state,
		bool hideSecrets)
	{
		var result = await controller.BuildSettingsPlanAsync(
			state.Plan,
			state.BuildSelection() with { HideSecrets = hideSecrets },
			state.ExtensionOptionStates,
			state.BuildSelectedItemRelativePaths(),
			state.PathOptionStates,
			TestContext.Current.CancellationToken);
		controller.ApplySettingsPlan(state, result);
	}

	private static (TerminalWorkspaceController Controller, TerminalServices Services) CreateController(
		TemporaryDirectory workspace)
	{
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		return (new TerminalWorkspaceController(services, new TestTerminalEnvironment()), services);
	}

	private static Task<TerminalWorkspaceState> OpenAsync(
		TerminalWorkspaceController controller,
		string project) =>
		controller.OpenAsync(
			project,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);

	private static string CreateSearchProject(TemporaryDirectory workspace)
	{
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/App.cs", """
			namespace Demo;
			public sealed class Sample
			{
			    public string Run() =>
			        "needle value";
			    public string Call() => Run();
			}
			""");
		return project;
	}

	private static string CreateBudgetProject(TemporaryDirectory workspace)
	{
		var project = workspace.CreateDirectory("project");
		foreach (var name in new[] { "Alpha", "Beta", "Gamma" })
		{
			var body = string.Join(
				'\n',
				Enumerable.Range(0, 60).Select(index => $"    // {name} budget filler line {index:D2} ........."));
			workspace.WriteFile(
				$"project/src/{name}.cs",
				$"// BUDGET-MARKER-{name}\ninternal sealed class {name}\n{{\n{body}\n}}\n");
		}
		return project;
	}

	[GeneratedRegex(@"argv\[\d+\] = ""([^""]*)""", RegexOptions.CultureInvariant)]
	private static partial Regex ArgumentVectorRegex();

	private static int CountMarkers(string text) =>
		new[] { "Alpha", "Beta", "Gamma" }
			.Count(name => text.Contains($"BUDGET-MARKER-{name}", StringComparison.Ordinal));
}
