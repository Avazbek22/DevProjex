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
	public async Task RankingPreservesCompressedAndRedactedPayloadsAndPlaceholderIdentities()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile(
			"project/A.cs",
			"class A { const string Token = \"ghp_a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL\"; void Run() { int hidden = 1; } }\n");
		workspace.WriteFile(
			"project/B.cs",
			"class B { const string Token = \"ghp_Q7wE9rT2yU4iO6pA8sD0fG1hJ3kL5zX7cV9b\"; void Run() { int hidden = 2; } }\n");
		using var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: [],
				HideSecrets: true,
				CompressCode: true),
			cancellationToken: TestContext.Current.CancellationToken);
		var ranking = CreateRanking(plan, "B.cs", "A.cs");
		var focused = CreateFocusRanking(plan, "B.cs", "A.cs");

		var ordinary = await WriteJsonAsync(services.ContextDocumentService, plan, ranking: null);
		var ranked = await WriteJsonAsync(services.ContextDocumentService, plan, ranking);
		var focusRanked = await WriteJsonAsync(services.ContextDocumentService, plan, focused);

		Assert.Equal(ordinary.OrderBy(static pair => pair.Key), ranked.OrderBy(static pair => pair.Key));
		Assert.Equal(ordinary.OrderBy(static pair => pair.Key), focusRanked.OrderBy(static pair => pair.Key));
		Assert.All(ranked.Values, content =>
		{
			Assert.Contains("DEVPROJEX_REDACTED", content, StringComparison.Ordinal);
			Assert.DoesNotContain("ghp_", content, StringComparison.Ordinal);
		});
	}

	[Fact]
	public async Task FocusSeedIsConsideredFirstButAnOversizedSeedIsSkipped()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/A.txt", "aaaa");
		workspace.WriteFile("project/B.txt", "bbbbbbbbbbbbbbbbbbbb");
		var (service, plan) = await CreateContextAsync(workspace, project);
		var ranking = CreateFocusRanking(plan, "B.txt", "A.txt");

		var result = await WriteWithReportAsync(service, plan, ranking, maximumTokens: 1);

		Assert.DoesNotContain("B.txt:", result.Content, StringComparison.Ordinal);
		Assert.Contains("A.txt:", result.Content, StringComparison.Ordinal);
		var seed = result.Report.TokenBudget!.RankedSkippedFiles!.First();
		Assert.Equal("B.txt", seed.Path);
		Assert.Equal(1, seed.Priority);
		Assert.Equal(0, seed.Hop);
		Assert.Equal(2, seed.BaseImportancePriority);
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
	public async Task BudgetMetadataUsesFileIdentityAfterRankingOrderIsReconciled()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/A.txt", "aaaaaaaa");
		workspace.WriteFile("project/B.txt", "bbbbbbbb");
		workspace.WriteFile("project/C.txt", "cccccccc");
		var (service, plan) = await CreateContextAsync(workspace, project);
		var outside = Path.Combine(project, "outside.txt");
		var raw = CreateRanking(
			project,
			[
				(outside, "outside.txt"),
				(Path.Combine(project, "B.txt"), "B.txt"),
				(Path.Combine(project, "B.txt"), "B.txt"),
				(Path.Combine(project, "C.txt"), "C.txt")
			]);
		var bVia = new FocusRankingVia("Seed.txt", FocusRankingRelation.DependencyOf);
		var cVia = new FocusRankingVia("B.txt", FocusRankingRelation.DependentOf);
		var entries = new[]
		{
			raw.Entries[0] with { Priority = 91, Hop = 9, BaseImportancePriority = 90 },
			raw.Entries[1] with { Priority = 92, Hop = 1, BaseImportancePriority = 12, Via = bVia },
			raw.Entries[2] with { Priority = 93, Hop = 8, BaseImportancePriority = 88 },
			raw.Entries[3] with { Priority = 94, Hop = 2, BaseImportancePriority = 23, Via = cVia }
		};
		var ranking = raw with { Entries = entries, TopEntries = entries };

		var result = await WriteWithReportAsync(service, plan, ranking, maximumTokens: 1);

		Assert.DoesNotContain("outside.txt", result.Content, StringComparison.Ordinal);
		Assert.DoesNotContain("A.txt:", result.Content, StringComparison.Ordinal);
		Assert.DoesNotContain("B.txt:", result.Content, StringComparison.Ordinal);
		Assert.DoesNotContain("C.txt:", result.Content, StringComparison.Ordinal);
		var skipped = result.Report.TokenBudget!.RankedSkippedFiles!;
		Assert.Collection(
			skipped,
			file =>
			{
				Assert.Equal("B.txt", file.Path);
				Assert.Equal(1, file.Priority);
				Assert.Equal(1, file.Hop);
				Assert.Equal(12, file.BaseImportancePriority);
				Assert.Equal(bVia, file.Via);
				Assert.Equal(1, file.RemainingEstimatedTokens);
			},
			file =>
			{
				Assert.Equal("C.txt", file.Path);
				Assert.Equal(2, file.Priority);
				Assert.Equal(2, file.Hop);
				Assert.Equal(23, file.BaseImportancePriority);
				Assert.Equal(cVia, file.Via);
				Assert.Equal(1, file.RemainingEstimatedTokens);
			},
			file =>
			{
				Assert.Equal("A.txt", file.Path);
				Assert.Equal(3, file.Priority);
				Assert.Null(file.Hop);
				Assert.Null(file.BaseImportancePriority);
				Assert.Null(file.Via);
				Assert.Equal(1, file.RemainingEstimatedTokens);
			});
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
			maximumEstimatedTokens: 1,
			ranking: ranking);

		using var json = JsonDocument.Parse(destination.ToArray());
		var report = json.RootElement.GetProperty("ranking");
		Assert.Equal(ImportanceRankingService.AlgorithmId, report.GetProperty("algorithm").GetString());
		Assert.Equal("pagerank", report.GetProperty("graphVariant").GetString());
		Assert.Equal(2, report.GetProperty("top").GetArrayLength());
		Assert.Equal("B.txt", report.GetProperty("top")[0].GetProperty("path").GetString());
		var skipped = Assert.Single(report.GetProperty("skipped").EnumerateArray());
		Assert.Equal("A.txt", skipped.GetProperty("path").GetString());
		Assert.Equal(2, skipped.GetProperty("priority").GetInt32());
		Assert.Equal("does not fit the remaining budget", skipped.GetProperty("reason").GetString());
		Assert.False(report.TryGetProperty("focus", out _));
		Assert.False(report.GetProperty("top")[0].TryGetProperty("hop", out _));
	}

	[Fact]
	public async Task JsonFocusReportContainsSeedsHopsParentsAndFinalPriorities()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/A.txt", "alpha");
		workspace.WriteFile("project/B.txt", "beta");
		workspace.WriteFile("project/C.txt", "charlie");
		var (service, plan) = await CreateContextAsync(workspace, project);
		var baseRanking = CreateRanking(plan, "A.txt", "B.txt", "C.txt");
		var byPath = baseRanking.Entries.ToDictionary(static entry => entry.Path, StringComparer.Ordinal);
		var entries = new[]
		{
			byPath["B.txt"] with { Priority = 1, BaseImportancePriority = 2, Hop = 0, IsFocusSeed = true },
			byPath["A.txt"] with
			{
				Priority = 2,
				BaseImportancePriority = 1,
				Hop = 1,
				Via = new FocusRankingVia("B.txt", FocusRankingRelation.DependentOf)
			},
			byPath["C.txt"] with { Priority = 3, BaseImportancePriority = 3 }
		};
		var ranking = baseRanking with
		{
			Algorithm = "focus-v1",
			Entries = entries,
			TopEntries = entries,
			Focus = new FocusRankingSummary(
				"focus-v1",
				"importance-v1",
				[new FocusRankingSeed("./B.txt", "B.txt", FocusSeedState.Resolved)],
				new SortedDictionary<int, int> { [0] = 1, [1] = 1 },
				0,
				1,
				1)
		};
		await using var destination = new MemoryStream();

		await service.WriteCompleteWithReportAsync(
			plan,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Json,
			destination,
			TestContext.Current.CancellationToken,
			maximumEstimatedTokens: 2,
			ranking: ranking);

		using var json = JsonDocument.Parse(destination.ToArray());
		var report = json.RootElement.GetProperty("ranking");
		Assert.Equal("focus-v1", report.GetProperty("algorithm").GetString());
		var focus = report.GetProperty("focus");
		Assert.Equal("importance-v1", focus.GetProperty("withinHop").GetString());
		Assert.Equal("./B.txt", focus.GetProperty("seeds")[0].GetProperty("requested").GetString());
		Assert.Equal("resolved", focus.GetProperty("seeds")[0].GetProperty("state").GetString());
		Assert.Equal(1, focus.GetProperty("hops").GetProperty("1").GetInt32());
		Assert.Equal(1, focus.GetProperty("maxHop").GetInt32());
		Assert.Equal(1, focus.GetProperty("unreachable").GetInt32());
		var parented = report.GetProperty("top")[1];
		Assert.Equal(1, parented.GetProperty("hop").GetInt32());
		Assert.Equal(1, parented.GetProperty("baseImportancePriority").GetInt32());
		Assert.Equal("B.txt", parented.GetProperty("via").GetProperty("path").GetString());
		Assert.Equal("dependent-of", parented.GetProperty("via").GetProperty("relation").GetString());
		var skipped = report.GetProperty("skipped")[0];
		Assert.Equal(2, skipped.GetProperty("priority").GetInt32());
		Assert.Equal(1, skipped.GetProperty("hop").GetInt32());
		Assert.Equal(1, skipped.GetProperty("baseImportancePriority").GetInt32());
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

	private static async Task<IReadOnlyDictionary<string, string>> WriteJsonAsync(
		ProjectContextDocumentService service,
		ProjectContextPlan plan,
		ImportanceRankingReport? ranking)
	{
		await using var destination = new MemoryStream();
		await service.WriteCompleteWithReportAsync(
			plan,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Json,
			destination,
			TestContext.Current.CancellationToken,
			ranking: ranking);
		using var document = JsonDocument.Parse(destination.ToArray());
		return document.RootElement.GetProperty("files").EnumerateArray().ToDictionary(
			static file => file.GetProperty("path").GetString()!,
			static file => file.GetProperty("content").GetString()!,
			StringComparer.Ordinal);
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

	private static ImportanceRankingReport CreateFocusRanking(
		ProjectContextPlan plan,
		params string[] focusOrder)
	{
		var importanceOrder = focusOrder.Reverse().ToArray();
		var importance = CreateRanking(plan, importanceOrder);
		var basePriorities = importance.Entries.ToDictionary(
			static entry => entry.Path,
			static entry => entry.Priority,
			StringComparer.Ordinal);
		var entries = focusOrder.Select((path, index) => importance.Entries
			.Single(entry => entry.Path == path) with
			{
				Priority = index + 1,
				BaseImportancePriority = basePriorities[path],
				Hop = index,
				IsFocusSeed = index == 0,
				Via = index == 0
					? null
					: new FocusRankingVia(focusOrder[index - 1], FocusRankingRelation.LinkedWith)
			}).ToArray();
		return importance with
		{
			Algorithm = "focus-v1",
			Entries = entries,
			TopEntries = entries,
			Focus = new FocusRankingSummary(
				"focus-v1",
				"importance-v1",
				[new FocusRankingSeed(focusOrder[0], focusOrder[0], FocusSeedState.Resolved)],
				entries.ToDictionary(static entry => entry.Hop!.Value, static _ => 1),
				0,
				entries.Length - 1,
				0)
		};
	}
}
