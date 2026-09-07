using DevProjex.Application.Context;
using DevProjex.Application.Dependencies;
using DevProjex.Application.Ranking;
using DevProjex.Infrastructure.Git;
using DevProjex.Kernel.Models;
using DevProjex.Terminal.Execution;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace DevProjex.RankingEval;

internal sealed class EvaluationRunner(
	EvaluationRegistry registry,
	string registryPath,
	string productRoot,
	string workspace)
{
	private static readonly string[] ExpectedOrders =
	[
		"current",
		"importance-v1",
		"seed-first",
		"focus-v1",
		"focus-ppr",
		"directed-from-seed"
	];

	public async Task<EvaluationRunResult> RunAsync(CancellationToken cancellationToken)
	{
		ValidateRegistry();
		Directory.CreateDirectory(workspace);
		var productSha = RunGit(productRoot, "rev-parse", "HEAD").Trim();
		var repositories = new List<RepositoryEvaluationResult>(registry.Repositories.Count);
		foreach (var repository in registry.Repositories)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Console.Error.WriteLine($"[eval] preparing {repository.Id} at {repository.Commit}");
			var root = EnsurePinnedClone(repository);
			var evaluation = await EvaluateRepositoryAsync(repository, root, cancellationToken)
				.ConfigureAwait(false);
			var performance = await MeasurePerformanceAsync(repository, root, cancellationToken)
				.ConfigureAwait(false);
			repositories.Add(evaluation with { Performance = performance });
		}
		var criterion = EvaluateCriterion(repositories);
		return new EvaluationRunResult(
			registry.Protocol,
			DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
			productSha,
			productSha,
			repositories,
			criterion);
	}

	private async Task<RepositoryEvaluationResult> EvaluateRepositoryAsync(
		RegistryRepository repository,
		string root,
		CancellationToken cancellationToken)
	{
		var appData = Path.Combine(workspace, "evaluation-data", repository.Id);
		Directory.CreateDirectory(appData);
		using var services = new TerminalServiceFactory(() => appData).Create(AppLanguage.En);
		var plan = await services.ContextFactory
			.BuildAsync(root, ProjectSelectionSpec.Standard, cancellationToken: cancellationToken)
			.ConfigureAwait(false);
		if (plan.HasErrors)
			throw new InvalidOperationException($"The standard selection failed for {repository.Id}.");
		var catalog = await BuildContentCatalogAsync(services, plan, cancellationToken).ConfigureAwait(false);
		var fullByRelative = plan.IncludedFiles.ToDictionary(
			path => PortableRelative(plan.SourceRoot, path),
			Path.GetFullPath,
			StringComparer.Ordinal);
		foreach (var task in repository.Tasks)
		{
			if (!fullByRelative.ContainsKey(task.Seed))
				throw new InvalidDataException($"Registered seed is outside the standard selection: {repository.Id}/{task.Id}/{task.Seed}");
			foreach (var required in task.SufficientSets.SelectMany(static set => set))
			{
				if (!catalog.CharacterCounts.ContainsKey(required))
				{
					var matching = catalog.CharacterCounts.Keys
						.Where(path => path.EndsWith(Path.GetFileName(required), StringComparison.Ordinal))
						.Take(5);
					throw new InvalidDataException(
						$"Registered required path is outside the standard selection: {repository.Id}/{task.Id}/{required}; " +
						$"serialized candidates: {string.Join(", ", matching)}");
				}
			}
		}

		var ranking = new ImportanceRankingService(
			services.DependencyFactsEngine,
			new ProjectGitHistoryReader());
		var importance = await ranking.RankAsync(
			plan.SourceRoot,
			plan.IncludedFiles,
			cancellationToken).ConfigureAwait(false);
		var importanceOrder = importance.Entries.Select(static entry => entry.Path).ToArray();
		var importancePriority = importance.Entries.ToDictionary(
			static entry => entry.Path,
			static entry => entry.Priority,
			StringComparer.Ordinal);
		var cells = new List<EvaluationCellResult>(repository.Tasks.Count * registry.Selection.Budgets.Count);
		foreach (var task in repository.Tasks)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Console.Error.WriteLine($"[eval] {repository.Id}: {task.Id}");
			var seedRequest = new FocusRankingSeedRequest(task.Seed, fullByRelative[task.Seed]);
			var seedFirst = await ranking.RankAsync(
				plan.SourceRoot,
				plan.IncludedFiles,
				new FocusRankingRequest([seedRequest], FocusRankingStrategy.SeedFirst),
				cancellationToken: cancellationToken).ConfigureAwait(false);
			var focus = await ranking.RankAsync(
				plan.SourceRoot,
				plan.IncludedFiles,
				new FocusRankingRequest([seedRequest]),
				cancellationToken: cancellationToken).ConfigureAwait(false);
			var ppr = await ranking.RankAsync(
				plan.SourceRoot,
				plan.IncludedFiles,
				new FocusRankingRequest([seedRequest], FocusRankingStrategy.PersonalizedPageRank),
				cancellationToken: cancellationToken).ConfigureAwait(false);
			var pprAvailable = ppr.Focus!.Seeds.Any(static seed =>
				seed.State is FocusSeedState.Resolved or FocusSeedState.NoResolvedNeighbors);
			var related = await services.DependencyFactsEngine.FindRelatedAsync(
				plan.SourceRoot,
				plan.IncludedFiles,
				[task.Seed],
				DependencyDirection.Both,
				cancellationToken: cancellationToken).ConfigureAwait(false);
			var directPaths = related.Seeds.Single().Dependencies
				.Concat(related.Seeds.Single().Dependents)
				.Select(static file => file.Path)
				.Where(catalog.CharacterCounts.ContainsKey)
				.Distinct(StringComparer.Ordinal)
				.OrderBy(path => importancePriority.GetValueOrDefault(path, int.MaxValue))
				.ThenBy(static path => path, StringComparer.Ordinal)
				.ToArray();
			var directed = new[] { task.Seed }
				.Concat(directPaths.Where(path => !path.Equals(task.Seed, StringComparison.Ordinal)))
				.ToArray();
			var orders = new Dictionary<string, (bool Available, IReadOnlyList<string> Paths)>(StringComparer.Ordinal)
			{
				["current"] = (true, catalog.CurrentOrder),
				["importance-v1"] = (true, importanceOrder),
				["seed-first"] = (true, seedFirst.Entries.Select(static entry => entry.Path).ToArray()),
				["focus-v1"] = (true, focus.Entries.Select(static entry => entry.Path).ToArray()),
				["focus-ppr"] = (pprAvailable, ppr.Entries.Select(static entry => entry.Path).ToArray()),
				["directed-from-seed"] = (true, directed)
			};

			foreach (var budget in registry.Selection.Budgets)
			{
				var seedPass = GreedyAdmission.Run([task.Seed], catalog.CharacterCounts, budget);
				var orderResults = new Dictionary<string, OrderEvaluationResult>(StringComparer.Ordinal);
				foreach (var orderId in ExpectedOrders)
				{
					var order = orders[orderId];
					if (!order.Available)
					{
						var unavailableMetric = EvaluationMetrics.Calculate(
							task.SufficientSets,
							[task.Seed],
							new HashSet<string>(StringComparer.Ordinal),
							seedPass.Admitted,
							catalog.TokenCosts,
							budget,
							seedPass.RemainingTokens);
						orderResults[orderId] = new OrderEvaluationResult(
							false,
							null,
							null,
							null,
							unavailableMetric.Oracle,
							unavailableMetric.OracleAfterSeeds);
						continue;
					}
					var admitted = GreedyAdmission.Run(order.Paths, catalog.CharacterCounts, budget);
					var metric = EvaluationMetrics.Calculate(
						task.SufficientSets,
						[task.Seed],
						admitted.Admitted,
						seedPass.Admitted,
						catalog.TokenCosts,
						budget,
						seedPass.RemainingTokens);
					orderResults[orderId] = new OrderEvaluationResult(
						true,
						metric.RecallNew,
						metric.AllRequired,
						metric.IrrelevantTokenShare,
						metric.Oracle,
						metric.OracleAfterSeeds);
				}
				cells.Add(new EvaluationCellResult(task.Id, budget, orderResults));
			}
		}
		return new RepositoryEvaluationResult(repository.Id, repository.Commit, plan.IncludedFiles.Count, cells, []);
	}

	private async Task<IReadOnlyList<PerformanceResult>> MeasurePerformanceAsync(
		RegistryRepository repository,
		string root,
		CancellationToken cancellationToken)
	{
		var results = new List<PerformanceResult>(4);
		foreach (var mode in new[] { "cold", "warm" })
		{
			foreach (var order in new[] { "importance-v1", "focus-v1" })
			{
				var elapsed = new List<double>(registry.Performance.Repetitions);
				var workingSets = new List<long>(registry.Performance.Repetitions);
				for (var repetition = 0; repetition < registry.Performance.Repetitions; repetition++)
				{
					cancellationToken.ThrowIfCancellationRequested();
					Console.Error.WriteLine(
						$"[eval] {repository.Id}: performance {mode}/{order} {repetition + 1}/{registry.Performance.Repetitions}");
					var data = Path.Combine(workspace, "measure-data", repository.Id, mode, order, repetition.ToString(CultureInfo.InvariantCulture));
					Directory.CreateDirectory(data);
					var measured = await RunMeasureProcessAsync(
						repository.Id,
						root,
						data,
						mode,
						order,
						cancellationToken).ConfigureAwait(false);
					elapsed.Add(measured.ElapsedMilliseconds);
					workingSets.Add(measured.PeakWorkingSetBytes);
					Directory.Delete(data, recursive: true);
				}
				results.Add(new PerformanceResult(
					mode,
					order,
					registry.Performance.Repetitions,
					Median(elapsed),
					(long)Median(workingSets.Select(static value => (double)value).ToArray()),
					elapsed,
					workingSets));
			}
		}
		return results;
	}

	private async Task<MeasureOneResult> RunMeasureProcessAsync(
		string repositoryId,
		string root,
		string data,
		string mode,
		string order,
		CancellationToken cancellationToken)
	{
		var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Evaluator process path is unavailable.");
		var start = new ProcessStartInfo(executable)
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
			start.ArgumentList.Add(typeof(Program).Assembly.Location);
		foreach (var argument in new[]
		         {
			         "measure-one", "--registry", registryPath, "--repository", repositoryId,
			         "--root", root, "--data", data, "--mode", mode, "--order", order
		         })
			start.ArgumentList.Add(argument);
		using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start evaluator child process.");
		var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
		await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
		var output = await outputTask.ConfigureAwait(false);
		var error = await errorTask.ConfigureAwait(false);
		if (process.ExitCode != 0)
			throw new InvalidOperationException($"Evaluator child failed ({repositoryId}/{mode}/{order}): {error}");
		var result = JsonSerializer.Deserialize<MeasureOneResult>(output, EvaluationRegistry.JsonOptions) ??
		             throw new InvalidDataException("Evaluator child returned no measurement.");
		return result with { PeakWorkingSetBytes = Math.Max(result.PeakWorkingSetBytes, process.PeakWorkingSet64) };
	}

	internal static async Task<MeasureOneResult> MeasureOneAsync(
		EvaluationRegistry registry,
		string repositoryId,
		string root,
		string data,
		string mode,
		string order,
		CancellationToken cancellationToken)
	{
		var repository = registry.Repositories.Single(item => item.Id.Equals(repositoryId, StringComparison.Ordinal));
		using var services = new TerminalServiceFactory(() => data).Create(AppLanguage.En);
		var plan = await services.ContextFactory
			.BuildAsync(root, ProjectSelectionSpec.Standard, cancellationToken: cancellationToken)
			.ConfigureAwait(false);
		var ranking = new ImportanceRankingService(services.DependencyFactsEngine, new ProjectGitHistoryReader());
		var seeds = repository.Tasks.Select(task => new FocusRankingSeedRequest(
			task.Seed,
			plan.IncludedFiles.Single(path => PortableRelative(plan.SourceRoot, path).Equals(task.Seed, StringComparison.Ordinal)))).ToArray();
		async Task RankAsync()
		{
			if (order == "importance-v1")
				_ = await ranking.RankAsync(plan.SourceRoot, plan.IncludedFiles, cancellationToken).ConfigureAwait(false);
			else
				_ = await ranking.RankAsync(
					plan.SourceRoot,
					plan.IncludedFiles,
					new FocusRankingRequest(seeds),
					cancellationToken: cancellationToken).ConfigureAwait(false);
		}
		if (mode == "warm")
			await RankAsync().ConfigureAwait(false);
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		var stopwatch = Stopwatch.StartNew();
		await RankAsync().ConfigureAwait(false);
		stopwatch.Stop();
		var process = Process.GetCurrentProcess();
		process.Refresh();
		return new MeasureOneResult(stopwatch.Elapsed.TotalMilliseconds, process.PeakWorkingSet64);
	}

	private static async Task<ContentCatalog> BuildContentCatalogAsync(
		TerminalServices services,
		ProjectContextPlan plan,
		CancellationToken cancellationToken)
	{
		await using var destination = new MemoryStream();
		_ = await services.ContextDocumentService.WriteCompleteWithReportAsync(
			plan,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Json,
			destination,
			cancellationToken,
			useSourceMappedStructuredPaths: true).ConfigureAwait(false);
		destination.Position = 0;
		using var document = await JsonDocument.ParseAsync(destination, cancellationToken: cancellationToken).ConfigureAwait(false);
		var order = new List<string>();
		var characters = new Dictionary<string, int>(StringComparer.Ordinal);
		var tokens = new Dictionary<string, long>(StringComparer.Ordinal);
		foreach (var file in document.RootElement.GetProperty("files").EnumerateArray())
		{
			var serializedPath = file.GetProperty("path").GetString() ??
			                     throw new InvalidDataException("Context file has no path.");
			var path = Path.IsPathFullyQualified(serializedPath)
				? PortableRelative(plan.SourceRoot, serializedPath)
				: serializedPath.Replace('\\', '/');
			var count = file.GetProperty("content").ValueKind == JsonValueKind.String
				? file.GetProperty("content").GetString()!.Length
				: 0;
			order.Add(path);
			characters.Add(path, count);
			tokens.Add(path, (count + 3L) / 4L);
		}
		return new ContentCatalog(order, characters, tokens);
	}

	private ReleaseCriterionResult EvaluateCriterion(IReadOnlyList<RepositoryEvaluationResult> repositories)
	{
		var eligible = repositories.SelectMany(repository => repository.Cells.Select(cell => (repository, cell)))
			.Where(item => item.cell.Orders["seed-first"].OracleAfterSeeds &&
			               item.cell.Orders["seed-first"].RecallNew is not null &&
			               item.cell.Orders["focus-v1"].RecallNew is not null)
			.ToArray();
		var overallDelta = Mean(eligible.Select(item =>
			item.cell.Orders["focus-v1"].RecallNew!.Value - item.cell.Orders["seed-first"].RecallNew!.Value));
		var repositoryDelta = repositories.ToDictionary(
			static repository => repository.Id,
			repository => Mean(eligible.Where(item => item.repository.Id == repository.Id).Select(item =>
				item.cell.Orders["focus-v1"].RecallNew!.Value - item.cell.Orders["seed-first"].RecallNew!.Value)),
			StringComparer.Ordinal);
		var regressions = eligible.Count(item =>
			item.cell.Orders["focus-v1"].AllRequired == false &&
			item.cell.Orders["seed-first"].AllRequired == true);
		var warmMilliseconds = new Dictionary<string, double>(StringComparer.Ordinal);
		var warmPercent = new Dictionary<string, double>(StringComparer.Ordinal);
		var memoryGrowth = new Dictionary<string, double>(StringComparer.Ordinal);
		foreach (var repository in repositories)
		{
			var warmImportance = Performance(repository, "warm", "importance-v1");
			var warmFocus = Performance(repository, "warm", "focus-v1");
			var incremental = warmFocus.MedianElapsedMilliseconds - warmImportance.MedianElapsedMilliseconds;
			warmMilliseconds[repository.Id] = incremental;
			warmPercent[repository.Id] = warmImportance.MedianElapsedMilliseconds <= 0
				? 0
				: incremental / warmImportance.MedianElapsedMilliseconds * 100;
			memoryGrowth[repository.Id] = new[] { "cold", "warm" }.Max(mode =>
			{
				var importance = Performance(repository, mode, "importance-v1").MedianPeakWorkingSetBytes;
				var focus = Performance(repository, mode, "focus-v1").MedianPeakWorkingSetBytes;
				return importance <= 0 ? 0 : (double)(focus - importance) / importance * 100;
			});
		}
		var recallPassed = overallDelta >= 0.10 && repositoryDelta.Values.All(static value => value >= 0);
		var allRequiredPassed = regressions == 0;
		var timePassed = repositories.All(repository =>
		{
			var baseline = Performance(repository, "warm", "importance-v1").MedianElapsedMilliseconds;
			return warmMilliseconds[repository.Id] <= Math.Max(baseline * 0.05, 100);
		});
		var memoryPassed = memoryGrowth.Values.All(static value => value <= 10);
		return new ReleaseCriterionResult(
			eligible.Length,
			overallDelta,
			repositoryDelta,
			regressions,
			warmMilliseconds,
			warmPercent,
			memoryGrowth,
			recallPassed,
			allRequiredPassed,
			timePassed,
			memoryPassed,
			recallPassed && allRequiredPassed && timePassed && memoryPassed);
	}

	private string EnsurePinnedClone(RegistryRepository repository)
	{
		var root = Path.Combine(workspace, "corpora", repository.Id);
		if (!Directory.Exists(Path.Combine(root, ".git")))
		{
			Directory.CreateDirectory(Path.GetDirectoryName(root)!);
			RunGit(workspace, "clone", "--quiet", "--no-checkout", repository.Url, root);
		}
		RunGit(root, "checkout", "--quiet", "--detach", repository.Commit);
		var actual = RunGit(root, "rev-parse", "HEAD").Trim();
		if (!actual.Equals(repository.Commit, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException($"Pinned commit mismatch for {repository.Id}: {actual}");
		return root;
	}

	private void ValidateRegistry()
	{
		if (!registry.Orders.Select(static order => order.Id).SequenceEqual(ExpectedOrders, StringComparer.Ordinal))
			throw new InvalidDataException("Registry order list does not match the six pre-registered comparators.");
		if (registry.Repositories.Count != 3 || registry.Repositories.Sum(static repository => repository.Tasks.Count) != 15)
			throw new InvalidDataException("Registry must contain three repositories and fifteen tasks.");
		if (registry.Repositories.SelectMany(static repository => repository.Tasks).Any(static task => string.IsNullOrWhiteSpace(task.Seed)))
			throw new InvalidDataException("Every task must have exactly one non-empty seed.");
		if (registry.Performance.Repetitions < 5)
			throw new InvalidDataException("Performance measurement requires at least five repetitions.");
	}

	private static PerformanceResult Performance(RepositoryEvaluationResult repository, string mode, string order) =>
		repository.Performance.Single(result => result.Mode == mode && result.Order == order);

	private static double Mean(IEnumerable<double> values)
	{
		var array = values.ToArray();
		return array.Length == 0 ? 0 : array.Average();
	}

	private static double Median(IReadOnlyList<double> values)
	{
		var sorted = values.Order().ToArray();
		if (sorted.Length == 0)
			return 0;
		return sorted.Length % 2 == 1
			? sorted[sorted.Length / 2]
			: (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
	}

	private static string RunGit(string workingDirectory, params string[] arguments)
	{
		var start = new ProcessStartInfo("git")
		{
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		foreach (var argument in arguments)
			start.ArgumentList.Add(argument);
		using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
		var output = process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		process.WaitForExit();
		if (process.ExitCode != 0)
			throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
		return output;
	}

	private static string PortableRelative(string root, string path) =>
		Path.GetRelativePath(root, path).Replace('\\', '/');
}
