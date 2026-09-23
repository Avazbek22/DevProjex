using DevProjex.Application.Diagnostics;

namespace DevProjex.Tests.Terminal;

public sealed class SearchCommandBoundedReadTests
{
	[Fact]
	public async Task RawSearchDoesNotReadFileThatGrewBeyondInspectionBudget()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = workspace.WriteFile("project/notes.txt", "needle");
		using var services = new TerminalServiceFactory(
			() => workspace.CreateDirectory("app-data")).Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			ProjectSelectionSpec.Standard,
			includeOutputMetrics: false,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Single(plan.IncludedFiles);

		File.WriteAllText(source, "needle" + new string('x', 256));
		var request = new SearchCommandRequest(
			project,
			"needle",
			ProjectSelectionSpec.Standard,
			SearchMode.Text,
			MaximumResults: 10,
			SearchBodyCharacters: 0,
			Format: SearchOutputFormat.Json,
			OutputPath: null,
			Output: new TerminalOutputOptions());
		using var measurement = ContentPipelineDiagnostics.BeginMeasurement();
		var payload = await new SearchCommandHandler(
				services,
				new TestTerminalEnvironment())
			.RenderSearchForPlanAsync(
					plan,
					request,
					maximumInspectedBytes: 128,
					cancellationToken: TestContext.Current.CancellationToken);
		var counters = measurement.Capture();
		using var document = JsonDocument.Parse(payload);
		var boundary = document.RootElement.GetProperty("searchBoundary");
		Assert.False(boundary.GetProperty("complete").GetBoolean());
		Assert.Equal(0, boundary.GetProperty("inspectedSources").GetInt32());
		Assert.Contains("inspection-bytes", boundary.GetProperty("limits").EnumerateArray()
			.Select(static limit => limit.GetString()));
		Assert.Empty(document.RootElement.GetProperty("matches").EnumerateArray());
		Assert.Equal(0, counters.FullFileReads);
	}

	[Fact]
	public async Task RawSearchChargesActualReadBytesAgainstFollowingFiles()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var first = workspace.WriteFile("project/a.txt", "first");
		var second = workspace.WriteFile("project/b.txt", "second");
		using var services = new TerminalServiceFactory(
			() => workspace.CreateDirectory("app-data")).Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			ProjectSelectionSpec.Standard,
			includeOutputMetrics: false,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Equal(2, plan.IncludedFiles.Count);

		File.WriteAllText(first, "first" + new string('x', 95));
		File.WriteAllText(second, "second" + new string('x', 75));
		var request = new SearchCommandRequest(
			project,
			"second",
			ProjectSelectionSpec.Standard,
			SearchMode.Text,
			MaximumResults: 10,
			SearchBodyCharacters: 0,
			Format: SearchOutputFormat.Json,
			OutputPath: null,
			Output: new TerminalOutputOptions());
		using var measurement = ContentPipelineDiagnostics.BeginMeasurement();
		var payload = await new SearchCommandHandler(
				services,
				new TestTerminalEnvironment())
			.RenderSearchForPlanAsync(
					plan,
					request,
					maximumInspectedBytes: 128,
					cancellationToken: TestContext.Current.CancellationToken);
		var counters = measurement.Capture();
		using var document = JsonDocument.Parse(payload);
		var boundary = document.RootElement.GetProperty("searchBoundary");
		Assert.False(boundary.GetProperty("complete").GetBoolean());
		Assert.Equal(1, boundary.GetProperty("inspectedSources").GetInt32());
		Assert.Contains("inspection-bytes", boundary.GetProperty("limits").EnumerateArray()
			.Select(static limit => limit.GetString()));
		Assert.Empty(document.RootElement.GetProperty("matches").EnumerateArray());
		Assert.Equal(1, counters.FullFileReads);
		Assert.Equal(100, counters.FullFileReadBytes);
	}
}
