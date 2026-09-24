using System.Collections.Concurrent;
using System.Diagnostics;
using DevProjex.Application.Diagnostics;
using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit;

[Collection("AvaloniaUI")]
public sealed class ProjectScopeDiscoveryRootFactsMeasurementTests(ITestOutputHelper output)
{
	[AvaloniaFact(Timeout = 180_000)]
	[Trait("Category", "LocalPerformance")]
	public async Task MeasureActualGuiRootFactsBuildsAndWorkspaceOverlap()
	{
		var requestedRoot = Environment.GetEnvironmentVariable("DEVPROJEX_GUI_BENCHMARK_ROOT");
		Assert.SkipWhen(string.IsNullOrWhiteSpace(requestedRoot), "Set DEVPROJEX_GUI_BENCHMARK_ROOT for read-only profiling.");
		var projectRoot = Path.GetFullPath(requestedRoot!);
		Assert.True(Directory.Exists(projectRoot));
		var cancellationToken = TestContext.Current.CancellationToken;
		// Call the real builder through the existing injected-provider seam without a second cache or diagnostic counter.
		var buildFacts = typeof(ProjectRootFactsProvider)
			.GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Static)!
			.CreateDelegate<Func<string, CancellationToken, ProjectRootFacts>>();
		var probe = new RunProbe();
		var provider = new ProjectRootFactsProvider(null, 256, null, path =>
		{
			var currentProbe = probe;
			var phase = currentProbe.Phase;
			var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
			var started = Stopwatch.GetTimestamp();
			var facts = buildFacts(path, cancellationToken);
			var finished = Stopwatch.GetTimestamp();
			var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
			currentProbe.Facts.Add(new FactsRead(path, phase, started, finished, allocated,
				facts.Exists && facts.IsAccessible, facts.Files.Count + facts.Directories.Count));
			return facts;
		});
		var smartIgnore = new SmartIgnoreService(
		[
			new CommonSmartIgnoreRule(), new FrontendArtifactsIgnoreRule(), new DotNetArtifactsIgnoreRule(),
			new PythonArtifactsIgnoreRule(), new JvmArtifactsIgnoreRule(), new RustArtifactsIgnoreRule(),
			new GoArtifactsIgnoreRule(), new PhpArtifactsIgnoreRule(), new RubyArtifactsIgnoreRule(),
			new SwiftArtifactsIgnoreRule(), new DartArtifactsIgnoreRule()
		], provider);
		var ignoreRules = new IgnoreRulesService(smartIgnore,
			pathComparisonSemanticsResolver: GitConfigPathComparisonSemanticsResolver.Instance);
		var scanner = new FileSystemScanner((point, path) => probe.Enumerations.Add(new EnumerationRead(point, path)));
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		for (var run = 0; run < 8; run++)
		{
			probe = new RunProbe();
			var viewModel = new MainWindowViewModel(localization, new HelpContentProvider()) { IsProjectLoaded = true };
			using var coordinator = new SelectionSyncCoordinator(
				viewModel,
				new ScanOptionsUseCase(scanner),
				new FilterOptionSelectionService(),
				new IgnoreOptionsService(localization),
				(path, selected, roots) => MeasureCallback("build", roots, () => ignoreRules.Build(path, selected, roots)),
				(path, roots) => MeasureCallback("availability", roots, () =>
					ignoreRules.GetIgnoreOptionsAvailability(path, roots) with { ShowAdvancedCounts = true }),
				_ => false,
				() => projectRoot,
				buildIgnoreRulesWithCancellation: (path, selected, roots, token) => MeasureCallback("build", roots, () =>
					ignoreRules.BuildWithCancellation(path, selected, roots, token)),
				getIgnoreOptionsAvailabilityWithCancellation: (path, roots, token) => MeasureCallback("availability", roots, () =>
					ignoreRules.GetIgnoreOptionsAvailabilityWithCancellation(path, roots, token) with { ShowAdvancedCounts = true }),
				gitScopePathProvider: new GitScopePathProvider());
			coordinator.ResetProjectProfileSelections(projectRoot);
			ignoreRules.RefreshDiscoveryCaches(projectRoot);
			coordinator.InvalidateFileSystemCaches();
			using var diagnostics = IgnorePipelineDiagnostics.BeginMeasurement();
			var started = Stopwatch.GetTimestamp();
			var snapshot = await coordinator.BuildProjectSelectionSnapshotAsync(projectRoot, cancellationToken);
			var selectionMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
			Assert.NotNull(snapshot);
			Assert.NotNull(snapshot.TreeInventory);
			Assert.False(snapshot.HadScanFailure);
			var facts = probe.Facts.OrderBy(static read => read.Started).ToArray();
			var enumerations = probe.Enumerations.ToArray();
			var factsByPath = facts.GroupBy(static read => read.Path, PathComparer.Default).ToArray();
			var repeatedReads = factsByPath.SelectMany(static group => group.Skip(1)).ToArray();
			var enumeratedPaths = enumerations.Where(static entry => Path.IsPathFullyQualified(entry.Path))
				.Select(static entry => entry.Path).ToHashSet(PathComparer.Default);
			var overlap = factsByPath.Where(group => enumeratedPaths.Contains(group.Key)).ToArray();
			var counters = diagnostics.Capture();
			Assert.Equal(counters.RootFactsBuilds, facts.LongLength);
			Assert.Equal(1, counters.WorkspaceScans);
			output.WriteLine(JsonSerializer.Serialize(new
			{
				Run = run,
				Phase = run == 0 ? "first" : "warm-filesystem-refreshed-discovery",
				SelectionMilliseconds = selectionMilliseconds,
				InventoryEntries = snapshot.TreeInventory.Entries.Count,
				RootFactsBuilds = facts.Length,
				RootFactsUniquePaths = factsByPath.Length,
				RootFactsRepeatedBuilds = repeatedReads.Length,
				RootFactsRepeatedPaths = factsByPath.Count(static group => group.Count() > 1),
				SuccessfulFactsEnumerations = facts.Count(static read => read.Enumerated),
				FactsWorkMilliseconds = WorkMilliseconds(facts),
				FactsWallCoveredMilliseconds = WallCoveredMilliseconds(facts),
				RepeatedFactsWorkMilliseconds = WorkMilliseconds(repeatedReads),
				RepeatedFactsWallCoveredMilliseconds = WallCoveredMilliseconds(repeatedReads),
				FactsAllocatedBytes = facts.Sum(static read => read.AllocatedBytes),
				RepeatedFactsAllocatedBytes = repeatedReads.Sum(static read => read.AllocatedBytes),
				ScannerEnumerationHooks = enumerations.Length,
				ScannerUniqueEnumeratedPaths = enumeratedPaths.Count,
				FactsAndScannerOverlapPaths = overlap.Length,
				FactsBuildsOnScannerPaths = overlap.Sum(static group => group.Count()),
				ScannerHookKinds = enumerations.GroupBy(static read => read.Point).Select(group => new
				{
					Point = group.Key.ToString(),
					Count = group.Count(),
					UniquePaths = group.Select(static read => read.Path).Distinct(PathComparer.Default).Count()
				}).ToArray(),
				Callbacks = probe.Callbacks.ToArray(),
				FactsByPhase = facts.GroupBy(static read => read.Phase).Select(group => new
				{
					Phase = group.Key,
					Builds = group.Count(),
					UniquePaths = group.Select(static read => read.Path).Distinct(PathComparer.Default).Count(),
					WorkMilliseconds = WorkMilliseconds(group),
					WallCoveredMilliseconds = WallCoveredMilliseconds(group),
					AllocatedBytes = group.Sum(static read => read.AllocatedBytes)
				}).ToArray(),
				RepeatedPaths = factsByPath.Where(static group => group.Count() > 1).Select(group => new
				{
					Path = Path.GetRelativePath(projectRoot, group.Key),
					Builds = group.Count(),
					Phases = group.Select(static read => read.Phase).ToArray(),
					WorkMilliseconds = WorkMilliseconds(group)
				}).OrderByDescending(static group => group.WorkMilliseconds).Take(16).ToArray(),
				Diagnostics = counters
			}));
		}

		T MeasureCallback<T>(string kind, IReadOnlyCollection<string>? roots, Func<T> callback)
		{
			var phase = $"{++probe.CallbackSequence}:{kind}:roots={roots?.Count ?? -1}";
			probe.Phase = phase;
			var started = Stopwatch.GetTimestamp();
			try { return callback(); }
			finally
			{
				probe.Callbacks.Add(new CallbackRead(phase, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
				probe.Phase = "scanner-or-presentation";
			}
		}
	}

	private static double WorkMilliseconds(IEnumerable<FactsRead> reads) =>
		reads.Sum(static read => Stopwatch.GetElapsedTime(read.Started, read.Finished).TotalMilliseconds);

	private static double WallCoveredMilliseconds(IEnumerable<FactsRead> reads)
	{
		long covered = 0, start = 0, end = 0;
		foreach (var read in reads.OrderBy(static read => read.Started))
		{
			if (read.Started > end)
			{
				covered += end - start;
				start = read.Started;
			}
			end = Math.Max(end, read.Finished);
		}
		return (covered + end - start) * 1000d / Stopwatch.Frequency;
	}

	private sealed class RunProbe
	{
		public string Phase = "scanner-or-presentation";
		public int CallbackSequence;
		public ConcurrentBag<FactsRead> Facts { get; } = [];
		public ConcurrentBag<EnumerationRead> Enumerations { get; } = [];
		public List<CallbackRead> Callbacks { get; } = [];
	}
	private sealed record FactsRead(string Path, string Phase, long Started, long Finished, long AllocatedBytes, bool Enumerated, int Entries);
	private sealed record EnumerationRead(FileSystemScanEnumerationPoint Point, string Path);
	private sealed record CallbackRead(string Phase, double Milliseconds);
}
