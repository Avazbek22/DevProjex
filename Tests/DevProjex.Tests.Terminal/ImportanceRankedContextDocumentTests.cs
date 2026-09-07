using DevProjex.Application.Ranking;

namespace DevProjex.Tests.Terminal;

public sealed class ImportanceRankedContextDocumentTests
{
	[Fact]
	public async Task RankingChangesDocumentOrderWithoutChangingFilePayloads()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/A.txt", "alpha\n");
		workspace.WriteFile("project/B.txt", "beta\n");
		var (service, plan) = await CreateContextAsync(workspace, project);
		var ranking = CreateRanking(plan, "B.txt", "A.txt");

		var ordinary = await WriteAsync(service, plan, ranking: null);
		var ranked = await WriteAsync(service, plan, ranking);

		Assert.True(ordinary.IndexOf("A.txt:", StringComparison.Ordinal) < ordinary.IndexOf("B.txt:", StringComparison.Ordinal));
		Assert.True(ranked.IndexOf("B.txt:", StringComparison.Ordinal) < ranked.IndexOf("A.txt:", StringComparison.Ordinal));
		Assert.Contains($"A.txt:{Environment.NewLine}{Environment.NewLine}alpha", ordinary, StringComparison.Ordinal);
		Assert.Contains($"A.txt:{Environment.NewLine}{Environment.NewLine}alpha", ranked, StringComparison.Ordinal);
		Assert.Contains($"B.txt:{Environment.NewLine}{Environment.NewLine}beta", ordinary, StringComparison.Ordinal);
		Assert.Contains($"B.txt:{Environment.NewLine}{Environment.NewLine}beta", ranked, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RankingControlsGreedyAdmissionAndRecordsPriorityAtSkipTime()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/A.txt", "aaaaaaaaaaaa");
		workspace.WriteFile("project/B.txt", "bbbbbbbbbbbb");
		var (service, plan) = await CreateContextAsync(workspace, project);
		var ranking = CreateRanking(plan, "B.txt", "A.txt");

		var ordinary = await WriteWithReportAsync(service, plan, ranking: null, maximumTokens: 3);
		var ranked = await WriteWithReportAsync(service, plan, ranking, maximumTokens: 3);

		Assert.Contains("A.txt:", ordinary.Content, StringComparison.Ordinal);
		Assert.DoesNotContain("B.txt:", ordinary.Content, StringComparison.Ordinal);
		Assert.Contains("B.txt:", ranked.Content, StringComparison.Ordinal);
		Assert.DoesNotContain("A.txt:", ranked.Content, StringComparison.Ordinal);
		var skipped = Assert.Single(ranked.Report.TokenBudget!.LargestSkippedFiles);
		Assert.Equal("A.txt", skipped.Path);
		Assert.Equal(2, skipped.Priority);
		Assert.Equal(0, skipped.RemainingEstimatedTokens);
	}

	[Fact]
	public async Task RankingNeverAddsFilesOutsideEffectiveSelection()
	{
		using var workspace = new TemporaryDirectory();
		var root = workspace.CreateDirectory("selection");
		workspace.WriteFile("selection/A.cs", "class A;");
		workspace.WriteFile("selection/B.cs", "class B;");
		var (service, plan) = await CreateContextAsync(workspace, root);
		var outside = Path.Combine(root, "excluded.cs");
		var ranking = CreateRanking(
			root,
			[(outside, "excluded.cs"), (Path.Combine(root, "B.cs"), "B.cs")]);

		var content = await WriteAsync(service, plan, ranking);

		Assert.True(content.IndexOf("B.cs:", StringComparison.Ordinal) < content.IndexOf("A.cs:", StringComparison.Ordinal));
		Assert.DoesNotContain("excluded.cs", content, StringComparison.Ordinal);
	}

	[Fact]
	public async Task JsonDocumentContainsBoundedRankingReport()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/A.txt", "alpha");
		workspace.WriteFile("project/B.txt", "beta");
		var (service, plan) = await CreateContextAsync(workspace, project);
		var ranking = CreateRanking(plan, "B.txt", "A.txt");
		await using var destination = new MemoryStream();

		await service.WriteCompleteWithReportAsync(
			plan,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Json,
			destination,
			TestContext.Current.CancellationToken,
			ranking: ranking);

		using var json = JsonDocument.Parse(destination.ToArray());
		var report = json.RootElement.GetProperty("ranking");
		Assert.Equal(ImportanceRankingService.AlgorithmId, report.GetProperty("algorithm").GetString());
		Assert.Equal(2, report.GetProperty("top").GetArrayLength());
		Assert.Equal("B.txt", report.GetProperty("top")[0].GetProperty("path").GetString());
	}

	private static async Task<(ProjectContextDocumentService Service, ProjectContextPlan Plan)> CreateContextAsync(
		TemporaryDirectory workspace,
		string project)
	{
		var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("app-data-" + Guid.NewGuid().ToString("N")))
			.Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			new ProjectSelectionSpec(GitMode: GitFilteringMode.None, Exclusions: []),
			cancellationToken: TestContext.Current.CancellationToken);
		return (services.ContextDocumentService, plan);
	}

	private static async Task<string> WriteAsync(
		ProjectContextDocumentService service,
		ProjectContextPlan plan,
		ImportanceRankingReport? ranking)
	{
		var result = await WriteWithReportAsync(service, plan, ranking, maximumTokens: null);
		return result.Content;
	}

	private static async Task<(string Content, ProjectContextWriteResult Report)> WriteWithReportAsync(
		ProjectContextDocumentService service,
		ProjectContextPlan plan,
		ImportanceRankingReport? ranking,
		long? maximumTokens)
	{
		await using var destination = new MemoryStream();
		var report = await service.WriteCompleteWithReportAsync(
			plan,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Text,
			destination,
			TestContext.Current.CancellationToken,
			maximumEstimatedTokens: maximumTokens,
			ranking: ranking);
		return (Encoding.UTF8.GetString(destination.ToArray()), report);
	}

	private static ImportanceRankingReport CreateRanking(
		ProjectContextPlan plan,
		params string[] relativePaths) =>
		CreateRanking(
			plan.SourceRoot,
			relativePaths.Select(path => (Path.Combine(plan.SourceRoot, path), path)).ToArray());

	private static ImportanceRankingReport CreateRanking(
		string root,
		IReadOnlyList<(string FullPath, string Path)> paths)
	{
		var entries = paths.Select((path, index) => new ImportanceRankingEntry(
			path.FullPath,
			path.Path,
			index + 1,
			1d - index * 0.1,
			paths.Count - index,
			index,
			1,
			1,
			ImportanceFileRole.Source,
			true,
			true)).ToArray();
		return new ImportanceRankingReport(
			ImportanceRankingService.AlgorithmId,
			entries,
			entries.Take(10).ToArray(),
			paths.Count,
			paths.Count,
			0,
			1,
			200,
			1,
			ProjectGitHistoryUnavailableReason.None,
			false,
			ImportanceRankingService.GraphVariant);
	}
}
