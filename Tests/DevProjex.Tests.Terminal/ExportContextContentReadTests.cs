using DevProjex.Application.Diagnostics;

namespace DevProjex.Tests.Terminal;

public sealed class ExportContextContentReadTests
{
	[Theory]
	[InlineData(ProjectContextDocumentFormat.Text, ProjectContextView.Tree, false)]
	[InlineData(ProjectContextDocumentFormat.Text, ProjectContextView.Content, false)]
	[InlineData(ProjectContextDocumentFormat.Text, ProjectContextView.TreeContent, false)]
	[InlineData(ProjectContextDocumentFormat.Markdown, ProjectContextView.Tree, false)]
	[InlineData(ProjectContextDocumentFormat.Markdown, ProjectContextView.Content, false)]
	[InlineData(ProjectContextDocumentFormat.Markdown, ProjectContextView.TreeContent, false)]
	[InlineData(ProjectContextDocumentFormat.Text, ProjectContextView.Content, true)]
	[InlineData(ProjectContextDocumentFormat.Text, ProjectContextView.TreeContent, true)]
	[InlineData(ProjectContextDocumentFormat.Markdown, ProjectContextView.Content, true)]
	[InlineData(ProjectContextDocumentFormat.Markdown, ProjectContextView.TreeContent, true)]
	public async Task TextualExportReadsOnlyTheContentNeededForItsDocument(
		ProjectContextDocumentFormat format,
		ProjectContextView view,
		bool stripBlankLines)
	{
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		workspace.WriteFile("A.txt", "first\n\npayload\r\n");
		workspace.WriteFile("nested/B.txt", "second\r\n\r\nзначение\n");
		var reference = await BuildReferenceAsync(workspace.Path, appData.Path, format, view, stripBlankLines);
		var environment = new TestTerminalEnvironment();
		using var measurement = ContentPipelineDiagnostics.BeginMeasurement();

		var exitCode = await RunAsync(workspace.Path, appData.Path, environment, format, view, stripBlankLines);
		var diagnostics = measurement.Capture();

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(reference.Output, environment.StandardOutput);
		Assert.Equal(reference.Error, environment.StandardError);
		Assert.Equal(
			(reference.Diagnostics.FullFileReads, reference.Diagnostics.FullFileReadBytes),
			(diagnostics.FullFileReads, diagnostics.FullFileReadBytes));
		if (view == ProjectContextView.Tree)
			Assert.Equal(0, diagnostics.FullFileReads);
	}

	[Theory]
	[InlineData(ProjectContextDocumentFormat.Text, false)]
	[InlineData(ProjectContextDocumentFormat.Text, true)]
	[InlineData(ProjectContextDocumentFormat.Markdown, false)]
	[InlineData(ProjectContextDocumentFormat.Markdown, true)]
	public async Task TokenBudgetAndTransformedBytesMatchTheFullyAnalyzedPlan(
		ProjectContextDocumentFormat format,
		bool stripBlankLines)
	{
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		workspace.WriteFile("A-large.txt", "too large for one token\n\n");
		workspace.WriteFile("B-small.txt", "b\n\n");
		var reference = await BuildReferenceAsync(
			workspace.Path,
			appData.Path,
			format,
			ProjectContextView.TreeContent,
			stripBlankLines,
			maximumEstimatedTokens: 1);
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace.Path,
			appData.Path,
			environment,
			format,
			ProjectContextView.TreeContent,
			stripBlankLines,
			maximumEstimatedTokens: 1);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(reference.Output, environment.StandardOutput);
		Assert.Equal(reference.Error, environment.StandardError);
		Assert.Contains("Included files: 1", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("Skipped files: 1", environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain("too large for one token", environment.StandardOutput, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(ProjectContextDocumentFormat.Json)]
	[InlineData(ProjectContextDocumentFormat.Xml)]
	public async Task StructuredExportKeepsItsPublishedContentMetrics(ProjectContextDocumentFormat format)
	{
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		const string source = "content metrics\n";
		workspace.WriteFile("A.txt", source);
		var reference = await BuildReferenceAsync(
			workspace.Path, appData.Path, format, ProjectContextView.TreeContent, stripBlankLines: false);
		var environment = new TestTerminalEnvironment();
		using var measurement = ContentPipelineDiagnostics.BeginMeasurement();

		var exitCode = await RunAsync(
			workspace.Path, appData.Path, environment, format, ProjectContextView.TreeContent, stripBlankLines: false);
		var diagnostics = measurement.Capture();

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(reference.Output, environment.StandardOutput);
		Assert.Empty(environment.StandardError);
		Assert.Equal(reference.Diagnostics.FullFileReads + 1, diagnostics.FullFileReads);
		Assert.Equal(reference.Diagnostics.FullFileReadBytes + Encoding.UTF8.GetByteCount(source), diagnostics.FullFileReadBytes);
	}

	[Theory]
	[InlineData(ProjectContextDocumentFormat.Text)]
	[InlineData(ProjectContextDocumentFormat.Markdown)]
	public async Task DryRunKeepsItsPublishedContentMetrics(ProjectContextDocumentFormat format)
	{
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		var path = workspace.WriteFile("A.txt", "dry-run metrics\n");
		var expected = await ProjectContentMetricsCalculator.CalculateAsync(
			new FileContentAnalyzer(), [path], TestContext.Current.CancellationToken);
		var environment = new TestTerminalEnvironment();
		using var measurement = ContentPipelineDiagnostics.BeginMeasurement();

		var exitCode = await RunAsync(
			workspace.Path, appData.Path, environment, format, ProjectContextView.Tree,
			stripBlankLines: false, dryRun: true);
		var diagnostics = measurement.Capture();

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains($"estimated tokens: {expected.Tokens}", environment.StandardError, StringComparison.Ordinal);
		Assert.Equal(1, diagnostics.FullFileReads);
		Assert.Equal(new FileInfo(path).Length, diagnostics.FullFileReadBytes);
	}

	private static async Task<ReferenceDocument> BuildReferenceAsync(
		string project,
		string appDataPath,
		ProjectContextDocumentFormat format,
		ProjectContextView view,
		bool stripBlankLines,
		long? maximumEstimatedTokens = null)
	{
		using var services = new TerminalServiceFactory(() => appDataPath).Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			ProjectSelectionSpec.Standard with
			{
				GitMode = GitFilteringMode.None,
				Exclusions = [],
				StripBlankLines = stripBlankLines
			},
			cancellationToken: TestContext.Current.CancellationToken);
		using var destination = new MemoryStream();
		using var error = new StringWriter();
		using var measurement = ContentPipelineDiagnostics.BeginMeasurement();
		var result = await services.ContextDocumentService.WriteCompleteWithReportAsync(
			plan,
			view,
			format,
			destination,
			TestContext.Current.CancellationToken,
			plain: true,
			useSourceMappedStructuredPaths: true,
			maximumEstimatedTokens: maximumEstimatedTokens);
		TokenBudgetOutput.Write(error, result.TokenBudget, services.Localization);
		return new ReferenceDocument(
			Encoding.UTF8.GetString(destination.ToArray()) + Environment.NewLine,
			error.ToString(),
			measurement.Capture());
	}

	private static Task<int> RunAsync(
		string project,
		string appDataPath,
		TestTerminalEnvironment environment,
		ProjectContextDocumentFormat format,
		ProjectContextView view,
		bool stripBlankLines,
		long? maximumEstimatedTokens = null,
		bool dryRun = false)
	{
		var arguments = new List<string>
		{
			"export", "context", project,
			"--view", view == ProjectContextView.TreeContent ? "tree-content" : view.ToString().ToLowerInvariant(),
			"--format", format.ToString().ToLowerInvariant(),
			"--git-mode", "none",
			"--exclude", "none",
			"--plain",
			"--progress", "never",
			"--language", "en",
			"-o", "-"
		};
		if (stripBlankLines)
			arguments.Add("--strip-blank-lines");
		if (maximumEstimatedTokens is { } limit)
			arguments.AddRange(["--max-tokens", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
		if (dryRun)
			arguments.Add("--dry-run");
		return new TerminalApplication(environment, new TerminalServiceFactory(() => appDataPath))
			.RunAsync(arguments, TestContext.Current.CancellationToken);
	}

	private sealed record ReferenceDocument(
		string Output,
		string Error,
		ContentPipelineDiagnosticSnapshot Diagnostics);
}
