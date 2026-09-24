using System.Diagnostics;
using System.Security.Cryptography;
using DevProjex.Application.Diagnostics;
using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit;

[Collection("AvaloniaUI")]
public sealed class TreeBuilderInventoryHotPathTests(ITestOutputHelper output)
{
	[AvaloniaFact(Timeout = 180_000)]
	[Trait("Category", "LocalPerformance")]
	public async Task MeasureFrozenGuiInventoryBuilderAndPresentationSeparately()
	{
		var requestedRoot = Environment.GetEnvironmentVariable("DEVPROJEX_GUI_BENCHMARK_ROOT");
		Assert.SkipWhen(string.IsNullOrWhiteSpace(requestedRoot), "Set DEVPROJEX_GUI_BENCHMARK_ROOT for read-only profiling.");
		var projectRoot = Path.GetFullPath(requestedRoot!);
		Assert.True(Directory.Exists(projectRoot));
		var cancellationToken = TestContext.Current.CancellationToken;
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		var ignoreRules = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();
		var viewModel = new MainWindowViewModel(localization, new HelpContentProvider()) { IsProjectLoaded = true };
		using var coordinator = new SelectionSyncCoordinator(
			viewModel,
			new ScanOptionsUseCase(new FileSystemScanner()),
			new FilterOptionSelectionService(),
			new IgnoreOptionsService(localization),
			(path, selected, roots) => ignoreRules.Build(path, selected, roots),
			(path, roots) => ignoreRules.GetIgnoreOptionsAvailability(path, roots) with { ShowAdvancedCounts = true },
			_ => false,
			() => projectRoot,
			buildIgnoreRulesWithCancellation: (path, selected, roots, token) =>
				ignoreRules.BuildWithCancellation(path, selected, roots, token),
			getIgnoreOptionsAvailabilityWithCancellation: (path, roots, token) =>
				ignoreRules.GetIgnoreOptionsAvailabilityWithCancellation(path, roots, token) with { ShowAdvancedCounts = true },
			gitScopePathProvider: new GitScopePathProvider());
		coordinator.ResetProjectProfileSelections(projectRoot);
		ignoreRules.RefreshDiscoveryCaches(projectRoot);
		coordinator.InvalidateFileSystemCaches();
		var snapshot = await coordinator.BuildProjectSelectionSnapshotAsync(projectRoot, cancellationToken);
		Assert.NotNull(snapshot);
		Assert.NotNull(snapshot.TreeInventory);
		Assert.False(snapshot.HadScanFailure);
		var inventory = snapshot.TreeInventory;
		var selectedRoots = snapshot.RootOptions!
			.Where(static option => option.IsChecked)
			.Select(static option => option.Name)
			.ToHashSet(ProjectTreePathIdentity.CanonicalComparer);
		var options = new TreeFilterOptions(
			snapshot.EffectiveExtensionOptions.Where(static option => option.IsChecked)
				.Select(static option => option.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
			selectedRoots,
			ProjectLoadIgnoreRulesResolver.Resolve(snapshot, selected =>
				ignoreRules.BuildWithCancellation(projectRoot, selected, selectedRoots, cancellationToken)));
		Assert.True(coordinator.ApplyProjectSelectionSnapshot(projectRoot, snapshot));
		Assert.True(ProjectTreeInventoryReuseScope.Create(projectRoot, options, supportsHiddenDotFolderVariants: true)
			.CanProject(projectRoot, options));

		var builder = new TreeBuilder();
		var presenter = new TreeNodePresentationService(localization, new IconMapper());
		var useCase = new BuildTreeUseCase(builder, presenter);
		var request = new BuildTreeRequest(projectRoot, options);
		// One inventory and option set isolate projection from discovery and matcher construction.
		var expected = await Task.Run(() => useCase.ExecuteWithInventory(request, inventory, cancellationToken), cancellationToken);
		Assert.Same(inventory, expected.Inventory);
		var expectedShape = Flatten(expected.Tree.Root, projectRoot);
		var expectedPaths = expected.Tree.OrderedFilePaths!.ToArray();
		var samples = new List<PhaseSample>();
		for (var run = 0; run < 10; run++)
		{
			var projected = await MeasureAsync("builder", run, () => builder.Build(inventory, options, cancellationToken));
			var presented = await MeasureAsync("presenter", run,
				() => presenter.BuildWithFilePathsWithCancellation(projected.Root, cancellationToken));
			var combined = await MeasureAsync("combined", run,
				() => useCase.ExecuteWithInventory(request, inventory, cancellationToken));
			Assert.Equal(expectedShape, Flatten(presented.Root, projectRoot));
			Assert.Equal(expectedShape, Flatten(combined.Tree.Root, projectRoot));
			Assert.Equal(expectedPaths, presented.OrderedFilePaths);
			Assert.Equal(expectedPaths, combined.Tree.OrderedFilePaths);
			Assert.Equal(expected.Tree.RootAccessDenied, projected.RootAccessDenied);
			Assert.Equal(expected.Tree.HadAccessDenied, projected.HadAccessDenied);
			Assert.Equal(expected.Tree.HadScanFailure, projected.HadScanFailure);
			Assert.Equal(expected.Tree.RootAccessDenied, combined.Tree.RootAccessDenied);
			Assert.Equal(expected.Tree.HadAccessDenied, combined.Tree.HadAccessDenied);
			Assert.Equal(expected.Tree.HadScanFailure, combined.Tree.HadScanFailure);
			Assert.Same(inventory, combined.Inventory);
		}

		output.WriteLine(JsonSerializer.Serialize(new
		{
			Scenario = "frozen-gui-inventory-projection",
			InventoryEntries = inventory.Entries.Count,
			Files = expectedPaths.Length,
			TreeSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(expectedShape)))),
			GitMode = options.IgnoreRules.GitFilteringMode.ToString(),
			IgnoreOptions = snapshot.EffectiveIgnoreOptions.Select(static option => option.ToString()).ToArray(),
			AllowedRoots = selectedRoots.Order(StringComparer.Ordinal).ToArray(),
			AllowedExtensions = options.AllowedExtensions.Order(StringComparer.Ordinal).ToArray(),
			GitScopes = inventory.DiscoveredGitIgnoreMatchers.Count,
			TrackedIndexes = inventory.DiscoveredGitTrackedPathIndexes.Count,
			Phases = samples.GroupBy(static sample => sample.Phase).Select(group => new
			{
				Phase = group.Key,
				MedianMilliseconds = group.Select(static sample => sample.Milliseconds).Order().ElementAt(4),
				MedianAllocatedBytes = group.Select(static sample => sample.AllocatedBytes).Order().ElementAt(4),
				Samples = group.ToArray()
			}).ToArray()
		}));

		async Task<T> MeasureAsync<T>(string phase, int run, Func<T> action)
		{
			return await Task.Run(() =>
			{
				using var ignoreMeasurement = IgnorePipelineDiagnostics.BeginMeasurement();
				using var contentMeasurement = ContentPipelineDiagnostics.BeginMeasurement();
				var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
				var started = Stopwatch.GetTimestamp();
				var result = action();
				var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
				var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
				var ignore = ignoreMeasurement.Capture();
				var content = contentMeasurement.Capture();
				Assert.Equal(0, ignore.DirectoryEnumerations);
				Assert.Equal(0, ignore.FileEnumerations);
				Assert.Equal(0, ignore.CombinedEntryEnumerations);
				Assert.Equal(0, ignore.GitIgnoreSourceReadRequests);
				Assert.Equal(0, ignore.GitIgnoreLoadExecutions);
				Assert.Equal(0, content.FullFileReads);
				if (run > 0)
					samples.Add(new PhaseSample(phase, run, milliseconds, allocatedBytes, ignore.RootFactsRequests, ignore.RootFactsBuilds));
				return result;
			}, cancellationToken);
		}
	}

	private static NodeShape[] Flatten(TreeNodeDescriptor root, string projectRoot)
	{
		var shapes = new List<NodeShape>();
		var pending = new Stack<TreeNodeDescriptor>();
		pending.Push(root);
		while (pending.TryPop(out var node))
		{
			shapes.Add(new NodeShape(Path.GetRelativePath(projectRoot, node.FullPath), node.DisplayName,
				node.IsDirectory, node.IsAccessDenied, node.IconKey, node.Children.Count));
			for (var child = node.Children.Count - 1; child >= 0; child--)
				pending.Push(node.Children[child]);
		}
		return shapes.ToArray();
	}

	private sealed record NodeShape(string RelativePath, string Name, bool IsDirectory, bool IsAccessDenied, string IconKey, int Children);
	private sealed record PhaseSample(string Phase, int Run, double Milliseconds, long AllocatedBytes, long RootFactsRequests, long RootFactsBuilds);
}
