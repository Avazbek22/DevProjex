using Avalonia.Controls;
using Avalonia.Threading;
using DevProjex.Avalonia.Services;
using DevProjex.Avalonia.Views;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class TreeSearchCoordinatorTests
{
	[Fact]
	public void UpdateSearchMatches_EmptyQuery_CollapsesDescendantsAndClearsMatches()
	{
		var (viewModel, treeView) = CreateContext();
		var root = CreateTree();
		viewModel.TreeNodes.Add(root);
		viewModel.SearchVisible = true;

		root.IsExpanded = true;
		root.Children[1].IsExpanded = true;

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = string.Empty;

		coordinator.UpdateSearchMatches();

		Assert.False(coordinator.HasMatches);
		Assert.False(root.Children[1].IsExpanded);
		Assert.False(root.Children[1].Children[0].IsExpanded);
		Assert.Equal("(0 / 0)", viewModel.SearchMatchSummaryText);
		Assert.False(viewModel.SearchMatchSummaryVisible);
	}

	[Fact]
	public void UpdateSearchMatches_EmptyAfterNoMatches_ReExpandsRootAndClearsSearchEffect()
	{
		var (viewModel, treeView) = CreateContext();
		var root = CreateTree();
		viewModel.TreeNodes.Add(root);

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);

		// Simulate "nonsense" query: search collapses branches including root.
		viewModel.SearchQuery = "___no_match___";
		coordinator.UpdateSearchMatches();
		Assert.False(root.IsExpanded);

		// Clear query: search impact must be fully removed.
		viewModel.SearchQuery = string.Empty;
		coordinator.UpdateSearchMatches();

		Assert.True(root.IsExpanded);
		Assert.False(coordinator.HasMatches);
		Assert.False(root.Children[1].IsExpanded);
		Assert.False(root.Children[1].Children[0].IsExpanded);
	}

	[AvaloniaFact]
	public async Task UpdateSearchMatches_FromWorkerThreadWithAvaloniaApplication_DoesNotReadThemeResourcesOffDispatcher()
	{
		var (viewModel, treeView) = CreateContext();
		var root = CreateTree();
		viewModel.TreeNodes.Add(root);

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "___no_match___";

		await Task.Run(() => coordinator.UpdateSearchMatches());

		Assert.False(coordinator.HasMatches);
		Assert.Equal("(0 / 0)", viewModel.SearchMatchSummaryText);
	}

	[AvaloniaFact]
	public async Task UpdateSearchMatchesAsync_BuildsDescriptorIndexOffDispatcher()
	{
		var dispatcherThreadId = Environment.CurrentManagedThreadId;
		var children = new BlockingDescriptorList(
		[
			CreateDescriptor("Alpha"),
			CreateDescriptor("Beta")
		]);
		var rootDescriptor = new TreeNodeDescriptor(
			"Root",
			"C:\\Root",
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "icon",
			Children: children);
		var root = new TreeNodeViewModel(rootDescriptor, null, null);
		var (viewModel, treeView) = CreateContext();
		viewModel.TreeNodes.Add(root);
		viewModel.SearchQuery = "missing";
		children.BlockReads();

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		var update = coordinator.UpdateSearchMatchesAsync();

		try
		{
			Assert.True(await children.WaitForBlockedReadAsync(TimeSpan.FromSeconds(5)));
			Assert.NotEqual(dispatcherThreadId, children.ReaderThreadId);
			Assert.True(viewModel.IsSearchInProgress);
		}
		finally
		{
			children.ReleaseReads();
		}

		await update;

		Assert.False(viewModel.IsSearchInProgress);
		Assert.False(coordinator.HasMatches);
	}

	[Fact]
	public void UpdateSearchMatches_WithSingleDeepMatch_SelectsNodeAndExpandsAncestors()
	{
		var (viewModel, treeView) = CreateContext();
		var root = CreateTree();
		viewModel.TreeNodes.Add(root);
		viewModel.SearchVisible = true;

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "delta";

		coordinator.UpdateSearchMatches();

		var delta = root.Children[1].Children[0];
		Assert.True(coordinator.HasMatches);
		Assert.True(root.IsExpanded);
		Assert.True(root.Children[1].IsExpanded);
		Assert.Same(delta, treeView.SelectedItem);
		Assert.True(delta.IsSelected);
		Assert.True(delta.IsCurrentSearchMatch);
		Assert.Equal("(1 / 1)", viewModel.SearchMatchSummaryText);
		Assert.True(viewModel.SearchMatchSummaryVisible);
	}

	[Fact]
	public void Navigate_CyclesForwardAndBackwardAcrossMatches()
	{
		var (viewModel, treeView) = CreateContext();
		var root = CreateTree();
		viewModel.TreeNodes.Add(root);

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "ta";

		coordinator.UpdateSearchMatches();
		var beta = root.Children[1];
		var delta = root.Children[1].Children[0];

		Assert.Same(beta, treeView.SelectedItem);
		Assert.Equal("(1 / 2)", viewModel.SearchMatchSummaryText);

		coordinator.Navigate(1);
		Assert.Same(delta, treeView.SelectedItem);
		Assert.Equal("(2 / 2)", viewModel.SearchMatchSummaryText);

		coordinator.Navigate(1);
		Assert.Same(beta, treeView.SelectedItem);
		Assert.Equal("(1 / 2)", viewModel.SearchMatchSummaryText);

		coordinator.Navigate(-1);
		Assert.Same(delta, treeView.SelectedItem);
		Assert.Equal("(2 / 2)", viewModel.SearchMatchSummaryText);
	}

	[AvaloniaFact]
	public async Task TryNavigateForCurrentQuery_FreshForwardQuery_SearchesOffDispatcherThenAdvances()
	{
		var (viewModel, treeView) = CreateContext();
		var root = CreateTree();
		viewModel.TreeNodes.Add(root);
		viewModel.SearchVisible = true;

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "ta";
		viewModel.SetSearchInProgress(true);

		Assert.True(coordinator.TryNavigateForCurrentQuery(1));
		Assert.Null(treeView.SelectedItem);
		await coordinator.WaitForImmediateNavigationAsync();
		Assert.Same(root.Children[1], treeView.SelectedItem);
		Assert.Equal("(1 / 2)", viewModel.SearchMatchSummaryText);

		Assert.True(coordinator.TryNavigateForCurrentQuery(1));
		Assert.Same(root.Children[1].Children[0], treeView.SelectedItem);
		Assert.Equal("(2 / 2)", viewModel.SearchMatchSummaryText);
	}

	[AvaloniaFact]
	public async Task TryNavigateForCurrentQuery_WhenQueryChanges_RefreshesMatchesWithoutSkippingFirstResult()
	{
		var (viewModel, treeView) = CreateContext();
		var root = CreateTree();
		viewModel.TreeNodes.Add(root);
		viewModel.SearchVisible = true;

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "delta";
		coordinator.UpdateSearchMatches();

		viewModel.SearchQuery = "ta";

		Assert.True(coordinator.TryNavigateForCurrentQuery(1));
		await coordinator.WaitForImmediateNavigationAsync();
		Assert.Same(root.Children[1], treeView.SelectedItem);
		Assert.Equal("(1 / 2)", viewModel.SearchMatchSummaryText);
	}

	[AvaloniaFact]
	public async Task TryNavigateForCurrentQuery_FreshBackwardQuery_WrapsToLastMatch()
	{
		var (viewModel, treeView) = CreateContext();
		var root = CreateTree();
		viewModel.TreeNodes.Add(root);
		viewModel.SearchVisible = true;

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "ta";

		Assert.True(coordinator.TryNavigateForCurrentQuery(-1));
		await coordinator.WaitForImmediateNavigationAsync();
		Assert.Same(root.Children[1].Children[0], treeView.SelectedItem);
		Assert.Equal("(2 / 2)", viewModel.SearchMatchSummaryText);
	}

	[AvaloniaFact]
	public async Task TryNavigateForCurrentQuery_WhenNoMatches_CompletesAcceptedBackgroundSearch()
	{
		var (viewModel, treeView) = CreateContext();
		var root = CreateTree();
		viewModel.TreeNodes.Add(root);
		viewModel.SearchVisible = true;

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "___no_match___";

		Assert.True(coordinator.TryNavigateForCurrentQuery(1));
		await coordinator.WaitForImmediateNavigationAsync();
		Assert.False(coordinator.HasMatches);
		Assert.Equal("(0 / 0)", viewModel.SearchMatchSummaryText);
		Assert.False(coordinator.TryNavigateForCurrentQuery(1));
	}

	[AvaloniaFact]
	public async Task TryNavigateForCurrentQueryAsync_WhenNoMatches_ReturnsNoMatchesAfterSearchCompletes()
	{
		var (viewModel, treeView) = CreateContext();
		viewModel.TreeNodes.Add(CreateTree());
		viewModel.SearchVisible = true;
		viewModel.SearchQuery = "___no_match___";

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);

		var result = await coordinator.TryNavigateForCurrentQueryAsync(1);

		Assert.Equal(TreeSearchCoordinator.NavigationResult.NoMatches, result);
		Assert.False(viewModel.IsSearchInProgress);
		Assert.Equal("(0 / 0)", viewModel.SearchMatchSummaryText);
	}

	[AvaloniaFact]
	public async Task TryNavigateForCurrentQueryAsync_RepeatedPendingRequestsShareCompletion()
	{
		var (viewModel, treeView) = CreateContext();
		var root = CreateTree();
		viewModel.TreeNodes.Add(root);
		viewModel.SearchVisible = true;
		viewModel.SearchQuery = "ta";
		viewModel.SetSearchInProgress(true);

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);

		var first = coordinator.TryNavigateForCurrentQueryAsync(1);
		var second = coordinator.TryNavigateForCurrentQueryAsync(1);

		Assert.Same(first, second);
		Assert.Equal(TreeSearchCoordinator.NavigationResult.Navigated, await second);
		Assert.Same(root.Children[1].Children[0], treeView.SelectedItem);
		Assert.Equal("(2 / 2)", viewModel.SearchMatchSummaryText);
	}

	[AvaloniaFact]
	public async Task TryNavigateForCurrentQueryAsync_QueryReplacementReturnsCanceled()
	{
		var (viewModel, treeView) = CreateContext();
		viewModel.TreeNodes.Add(CreateTree());
		viewModel.SearchVisible = true;
		viewModel.SearchQuery = "delta";
		viewModel.SetSearchInProgress(true);

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		var staleNavigation = coordinator.TryNavigateForCurrentQueryAsync(1);

		viewModel.SearchQuery = "missing";
		coordinator.OnSearchQueryChanged();

		Assert.Equal(TreeSearchCoordinator.NavigationResult.Canceled, await staleNavigation);
		coordinator.CancelPending();
	}

	[AvaloniaFact]
	public async Task NavigateSearchAsync_PendingNoMatchShowsExistingLocalizedToast()
	{
		const string noMatchesMessage = "No matching items.";
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Toast.NoMatches"] = noMatchesMessage
			}
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var viewModel = new MainWindowViewModel(localization, new HelpContentProvider())
		{
			SearchVisible = true,
			SearchQuery = "___no_match___"
		};
		viewModel.TreeNodes.Add(CreateTree());
		var treeView = new TreeView();
		var toastService = new RecordingToastService();

		using var controller = new SearchFilterInteractionController(
			new Window(),
			viewModel,
			treeView,
			new SearchBarView(),
			new Border(),
			new FilterBarView(),
			new Border(),
			SessionMetricsRecorder.Disabled,
			toastService,
			localization,
			static () => "C:\\Root",
			static () => null,
			static (_, _) => Task.FromResult(TreeRefreshOutcome.Skipped),
			static () => { },
			static () => false,
			static _ => Task.CompletedTask,
			static _ => { },
			static () => { });

		await controller.NavigateSearchAsync(1);

		var toast = Assert.Single(toastService.Items);
		Assert.Equal(noMatchesMessage, toast.Message);
	}

	[AvaloniaFact]
	public async Task TryNavigateForCurrentQuery_RepeatedPendingRequestsShareSearchAndPreserveOrder()
	{
		var (viewModel, treeView) = CreateContext();
		var root = CreateTree();
		viewModel.TreeNodes.Add(root);
		viewModel.SearchVisible = true;
		viewModel.SearchQuery = "ta";
		viewModel.SetSearchInProgress(true);

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);

		Assert.True(coordinator.TryNavigateForCurrentQuery(1));
		var firstCompletion = coordinator.WaitForImmediateNavigationAsync();
		Assert.True(coordinator.TryNavigateForCurrentQuery(1));
		var secondCompletion = coordinator.WaitForImmediateNavigationAsync();

		Assert.Same(firstCompletion, secondCompletion);
		await secondCompletion;
		Assert.Same(root.Children[1].Children[0], treeView.SelectedItem);
		Assert.Equal("(2 / 2)", viewModel.SearchMatchSummaryText);
	}

	[AvaloniaFact]
	public async Task TryNavigateForCurrentQuery_QueryReplacementInvalidatesPendingNavigation()
	{
		var (viewModel, treeView) = CreateContext();
		viewModel.TreeNodes.Add(CreateTree());
		viewModel.SearchVisible = true;
		viewModel.SearchQuery = "delta";
		viewModel.SetSearchInProgress(true);

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		Assert.True(coordinator.TryNavigateForCurrentQuery(1));
		var staleCompletion = coordinator.WaitForImmediateNavigationAsync();

		viewModel.SearchQuery = "missing";
		coordinator.OnSearchQueryChanged();
		await staleCompletion;
		coordinator.CancelPending();

		Assert.Null(treeView.SelectedItem);
		Assert.False(coordinator.HasMatches);
	}

	[Fact]
	public void BringIntoViewPathProgress_Depth128HasNoGlobalRetryLimit()
	{
		var progress = new TreeSearchCoordinator.BringIntoViewPathProgress(
			segmentCount: 128);

		for (var segment = 0; segment < progress.SegmentCount; segment++)
			Assert.True(progress.Observe(segment));

		Assert.Equal(127, progress.DeepestRealizedSegment);
		Assert.Equal(0, progress.NoProgressAttempts);
		Assert.Equal(128, progress.TotalAttempts);
	}

	[Fact]
	public void BuildNavigationPath_Depth128BuildsOneRootToTargetPath()
	{
		var root = new TreeNodeViewModel(
			CreateDescriptor("level-000"),
			null,
			null);
		var target = root;
		for (var depth = 1; depth < 128; depth++)
		{
			var child = new TreeNodeViewModel(
				CreateDescriptor($"level-{depth:D3}"),
				target,
				null);
			target.Children.Add(child);
			target = child;
		}

		var path = TreeSearchCoordinator.BuildNavigationPath(target);

		Assert.Equal(128, path.Length);
		Assert.Same(root, path[0]);
		Assert.Same(target, path[^1]);
		for (var index = 1; index < path.Length; index++)
			Assert.Same(path[index - 1], path[index].Parent);
	}

	[Fact]
	public void BringIntoViewPathProgress_UnattachedTreeStopsAfterBoundedNoProgress()
	{
		var progress = new TreeSearchCoordinator.BringIntoViewPathProgress(
			segmentCount: 128);

		for (var attempt = 1;
		     attempt < TreeSearchCoordinator.BringIntoViewPathProgress.MaxNoProgressAttempts;
		     attempt++)
		{
			Assert.True(progress.Observe(deepestRealizedSegment: -1));
		}

		Assert.False(progress.Observe(deepestRealizedSegment: -1));
		Assert.Equal(
			TreeSearchCoordinator.BringIntoViewPathProgress.MaxNoProgressAttempts,
			progress.TotalAttempts);
	}

	[AvaloniaFact]
	public void SearchNavigation_UnattachedTreeDrainsBoundedDispatcherRetries()
	{
		var (viewModel, treeView) = CreateContext();
		viewModel.TreeNodes.Add(CreateTree());
		viewModel.SearchQuery = "delta";
		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);

		coordinator.UpdateSearchMatches();
		var stopwatch = Stopwatch.StartNew();
		Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);

		Assert.True(
			stopwatch.Elapsed < TimeSpan.FromSeconds(1),
			$"Navigation retry queue took {stopwatch.Elapsed} to drain.");
		Assert.Equal(
			TreeSearchCoordinator.BringIntoViewPathProgress.MaxNoProgressAttempts,
			coordinator.LastBringIntoViewAttemptCount);
	}

	[Fact]
	public void ClearSearchState_RemovesCurrentMatchAndHighlights()
	{
		var (viewModel, treeView) = CreateContext();
		var root = CreateTree();
		viewModel.TreeNodes.Add(root);

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "delta";
		coordinator.UpdateSearchMatches();

		var delta = root.Children[1].Children[0];
		Assert.True(delta.IsCurrentSearchMatch);
		Assert.True(delta.HasHighlightedDisplay);

		coordinator.ClearSearchState();

		Assert.False(coordinator.HasMatches);
		Assert.False(delta.IsCurrentSearchMatch);
		Assert.False(delta.HasHighlightedDisplay);
		Assert.Equal("(0 / 0)", viewModel.SearchMatchSummaryText);
	}

	[Theory]
	[InlineData(100, -50, -20, 200, 1000, 50)]
	[InlineData(100, 220, 250, 200, 1000, 150)]
	[InlineData(10, -50, -20, 200, 1000, 0)]
	[InlineData(780, 240, 280, 200, 1000, 800)]
	public void ResolveVerticalOffsetForSearchNavigation_WhenTargetIsOutsideViewport_ReturnsClampedVerticalOffset(
		double currentOffsetY,
		double itemTop,
		double itemBottom,
		double viewportHeight,
		double extentHeight,
		double expectedOffsetY)
	{
		var targetOffsetY = TreeSearchCoordinator.ResolveVerticalOffsetForSearchNavigation(
			currentOffsetY,
			itemTop,
			itemBottom,
			viewportHeight,
			extentHeight);

		Assert.Equal(expectedOffsetY, targetOffsetY);
	}

	[Theory]
	[InlineData(100, 0, 30, 100)]
	[InlineData(100, -20, 10, 80)]
	[InlineData(100, 190, 230, 130)]
	[InlineData(100, 20, 250, 120)]
	public void ResolveVerticalOffsetForSearchNavigation_MovesOnlyWhenRequiredToFullyRevealTarget(
		double currentOffsetY,
		double itemTop,
		double itemBottom,
		double expectedOffsetY)
	{
		var targetOffsetY = TreeSearchCoordinator.ResolveVerticalOffsetForSearchNavigation(
			currentOffsetY,
			itemTop,
			itemBottom,
			viewportHeight: 200,
			extentHeight: 1000);

		Assert.Equal(expectedOffsetY, targetOffsetY);
	}

	[Theory]
	[InlineData(100, 60, 90, 200, 1000, 100)]
	[InlineData(100, 0, 30, 200, 1000, 25)]
	[InlineData(100, -20, 10, 200, 1000, 5)]
	[InlineData(100, 190, 230, 200, 1000, 200)]
	[InlineData(0, 0, 30, 200, 1000, 0)]
	[InlineData(780, 190, 230, 200, 1000, 800)]
	[InlineData(100, 10, 190, 200, 1000, 100)]
	[InlineData(100, 20, 250, 200, 1000, 120)]
	public void ResolveComfortableVerticalOffsetForSearchNavigation_KeepsTargetInCentralBandAndClampsAtContentEdges(
		double currentOffsetY,
		double itemTop,
		double itemBottom,
		double viewportHeight,
		double extentHeight,
		double expectedOffsetY)
	{
		var targetOffsetY =
			TreeSearchCoordinator.ResolveComfortableVerticalOffsetForSearchNavigation(
				currentOffsetY,
				itemTop,
				itemBottom,
				viewportHeight,
				extentHeight);

		Assert.Equal(expectedOffsetY, targetOffsetY);
	}

	[Theory]
	[InlineData(0, 500, 200, 0)]
	[InlineData(120, 500, 200, 120)]
	[InlineData(420, 500, 200, 300)]
	[InlineData(-15, 500, 200, 0)]
	[InlineData(120, 150, 200, 0)]
	public void ResolveClampedTreeHorizontalOffset_ReturnsOffsetInsideScrollableRange(
		double preservedOffsetX,
		double extentWidth,
		double viewportWidth,
		double expectedOffsetX)
	{
		var targetOffsetX = TreeSearchCoordinator.ResolveClampedTreeHorizontalOffset(
			preservedOffsetX,
			extentWidth,
			viewportWidth);

		Assert.Equal(expectedOffsetX, targetOffsetX);
	}

	[Theory]
	[InlineData(0, 12, 120, 200, 500, 108, 0)]
	[InlineData(80, 0, 200.5, 200, 500, 200.5, 80)]
	[InlineData(120, -30, 90, 200, 500, 120, 78)]
	[InlineData(120, 160, 260, 200, 500, 100, 192)]
	[InlineData(10, -50, 90, 200, 500, 140, 0)]
	[InlineData(260, 170, 260, 200, 500, 90, 300)]
	[InlineData(120, 80, 380, 200, 800, 300, 200)]
	[InlineData(180, 12, 112, 200, 500, 100, 180)]
	public void ResolveHorizontalOffsetForSearchNavigation_PreservesVisibleBaselineOrRevealsClippedContentWithPadding(
		double baselineOffsetX,
		double itemLeft,
		double itemRight,
		double viewportWidth,
		double extentWidth,
		double itemWidth,
		double expectedOffsetX)
	{
		var targetOffsetX =
			TreeSearchCoordinator.ResolveHorizontalOffsetForSearchNavigation(
				baselineOffsetX,
				itemLeft,
				itemRight,
				viewportWidth,
				extentWidth,
				itemWidth);

		Assert.Equal(expectedOffsetX, targetOffsetX);
	}

	[Fact]
	public void UpdateSearchMatches_WhenQueryHasNoMatches_ResetsSearchSummary()
	{
		var (viewModel, treeView) = CreateContext();
		var root = CreateTree();
		viewModel.TreeNodes.Add(root);
		viewModel.SearchVisible = true;

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "___no_match___";

		coordinator.UpdateSearchMatches();

		Assert.False(coordinator.HasMatches);
		Assert.Equal("(0 / 0)", viewModel.SearchMatchSummaryText);
		Assert.False(viewModel.SearchMatchSummaryVisible);
	}

	[AvaloniaFact]
	public void ClearSearchState_AfterRapidQueryReplacement_CompletesAllBatchedHighlightRemovals()
	{
		const int childCount = 600;
		var (viewModel, treeView) = CreateContext();
		var childDescriptors = Enumerable
			.Range(0, childCount)
			.Select(index => CreateDescriptor($"alpha-beta-{index:D4}"))
			.ToArray();
		var rootDescriptor = CreateDescriptor("Root", childDescriptors);
		var root = new TreeNodeViewModel(rootDescriptor, null, null);
		foreach (var descriptor in childDescriptors)
			root.Children.Add(new TreeNodeViewModel(descriptor, root, null));

		viewModel.TreeNodes.Add(root);
		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);

		viewModel.SearchQuery = "alpha";
		coordinator.UpdateSearchMatches();
		Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
		Assert.All(root.Children, node => Assert.True(node.HasHighlightedDisplay));

		viewModel.SearchQuery = "beta";
		coordinator.UpdateSearchMatches();
		viewModel.SearchQuery = string.Empty;
		coordinator.UpdateSearchMatches();
		coordinator.ClearSearchState(preservePendingHighlightCleanup: true);
		Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);

		Assert.False(coordinator.HasMatches);
		Assert.All(root.Children, node =>
		{
			Assert.False(node.HasHighlightedDisplay);
			Assert.False(node.IsCurrentSearchMatch);
		});
	}

	[Fact]
	public void CanApplySearchResult_RequiresLiveTokenMatchingVersionAndSameRoot()
	{
		var root = CreateTree();
		var replacementRoot = CreateTree();
		using var cancellation = new CancellationTokenSource();

		Assert.True(TreeSearchCoordinator.CanApplySearchResult(
			CancellationToken.None,
			requestVersion: 7,
			currentVersion: 7,
			root,
			root));
		Assert.False(TreeSearchCoordinator.CanApplySearchResult(
			CancellationToken.None,
			requestVersion: 7,
			currentVersion: 8,
			root,
			root));
		Assert.False(TreeSearchCoordinator.CanApplySearchResult(
			CancellationToken.None,
			requestVersion: 7,
			currentVersion: 7,
			root,
			replacementRoot));

		cancellation.Cancel();
		Assert.False(TreeSearchCoordinator.CanApplySearchResult(
			cancellation.Token,
			requestVersion: 7,
			currentVersion: 7,
			root,
			root));
	}

	[Fact]
	public void UpdateSearchMatches_DeepMatchMaterializesOnlyItsAncestorBranch()
	{
		var (viewModel, treeView) = CreateContext();
		var targetDescriptor = CreateDescriptor("target.cs");
		var untouchedDescriptor = CreateDescriptor("untouched.cs");
		var matchingFolderDescriptor = CreateDescriptor("src", targetDescriptor);
		var untouchedFolderDescriptor = CreateDescriptor("docs", untouchedDescriptor);
		var rootDescriptor = CreateDescriptor("Root", matchingFolderDescriptor, untouchedFolderDescriptor);
		var matchingFactoryCalls = 0;
		var untouchedFactoryCalls = 0;
		var root = new TreeNodeViewModel(rootDescriptor, null, null);
		var matchingFolder = new TreeNodeViewModel(
			matchingFolderDescriptor,
			root,
			null,
			parent =>
			{
				matchingFactoryCalls++;
				return [new TreeNodeViewModel(targetDescriptor, parent, null)];
			});
		var untouchedFolder = new TreeNodeViewModel(
			untouchedFolderDescriptor,
			root,
			null,
			parent =>
			{
				untouchedFactoryCalls++;
				return [new TreeNodeViewModel(untouchedDescriptor, parent, null)];
			});
		root.Children.Add(matchingFolder);
		root.Children.Add(untouchedFolder);
		viewModel.TreeNodes.Add(root);

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "target";
		coordinator.UpdateSearchMatches();

		Assert.Equal(1, matchingFactoryCalls);
		Assert.Equal(0, untouchedFactoryCalls);
		Assert.True(matchingFolder.AreChildrenRealized);
		Assert.False(untouchedFolder.AreChildrenRealized);
		Assert.Equal("target.cs", Assert.IsType<TreeNodeViewModel>(treeView.SelectedItem).DisplayName);

		viewModel.SearchQuery = string.Empty;
		coordinator.UpdateSearchMatches();
		root.IsChecked = true;

		Assert.Equal(0, untouchedFactoryCalls);
		Assert.True(untouchedFolder.IsChecked);
	}

	[Fact]
	public void SearchClose_ReleasesUncheckedSiblingsAndPreservesCheckedPath()
	{
		var (viewModel, treeView) = CreateContext();
		var targetDescriptor = CreateDescriptor("target.cs");
		var siblingDescriptor = CreateDescriptor("other.cs");
		var folderDescriptor = CreateDescriptor(
			"src",
			targetDescriptor,
			siblingDescriptor);
		var root = new TreeNodeViewModel(
			CreateDescriptor("Root", folderDescriptor),
			null,
			null);
		var folder = new TreeNodeViewModel(
			folderDescriptor,
			root,
			null,
			parent =>
			[
				new TreeNodeViewModel(targetDescriptor, parent, null),
				new TreeNodeViewModel(siblingDescriptor, parent, null)
			]);
		root.Children.Add(folder);
		viewModel.TreeNodes.Add(root);

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "target";
		coordinator.UpdateSearchMatches();
		folder.Children[0].IsChecked = true;

		viewModel.SearchQuery = string.Empty;
		coordinator.UpdateSearchMatches();

		Assert.False(folder.AreChildrenRealized);
		var selectedPaths = new HashSet<string>(PathComparer.Default);
		root.CollectCheckedPaths(selectedPaths);
		Assert.Equal([targetDescriptor.FullPath], selectedPaths);

		folder.IsExpanded = true;
		Assert.True(folder.Children[0].IsChecked);
	}

	[Fact]
	public void SearchClose_ReturnsSearchOnlyBranchesToLazyState()
	{
		const int branchCount = 3_000;
		var (viewModel, treeView) = CreateContext();
		var branchDescriptors = Enumerable
			.Range(0, branchCount)
			.Select(index => CreateDescriptor(
				$"folder-{index:D4}",
				CreateDescriptor($"match-{index:D4}.txt")))
			.ToArray();
		var root = CreateLazyTree(CreateDescriptor("Root", branchDescriptors));
		_ = root.Children.Count;
		viewModel.TreeNodes.Add(root);

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "match-";
		coordinator.UpdateSearchMatches();

		Assert.All(root.Children, node => Assert.True(node.AreChildrenRealized));

		viewModel.SearchQuery = string.Empty;
		coordinator.UpdateSearchMatches();

		Assert.All(
			root.Children,
			node => Assert.False(node.AreChildrenRealized));
		var retainedNodeCount = 0;
		TreeNodeViewModel.ForEachRealizedDescendant(
			[root],
			_ => retainedNodeCount++);
		Assert.Equal(branchCount + 2, retainedNodeCount);
	}

	[Fact]
	public void SearchClose_KeepsOnlySelectedPathInsideWideCommonAncestor()
	{
		const int matchCount = 1_000;
		var (viewModel, treeView) = CreateContext();
		var commonAncestor = CreateDescriptor(
			"src",
			Enumerable
				.Range(0, matchCount)
				.Select(index => CreateDescriptor(
					$"feature-{index:D4}",
					CreateDescriptor($"match-{index:D4}.cs")))
				.ToArray());
		var root = CreateLazyTree(CreateDescriptor("Root", commonAncestor));
		_ = root.Children.Count;
		viewModel.TreeNodes.Add(root);

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		for (var cycle = 0; cycle < 3; cycle++)
		{
			viewModel.SearchQuery = "match-";
			coordinator.UpdateSearchMatches();
			viewModel.SearchQuery = string.Empty;
			coordinator.UpdateSearchMatches();

			var retainedNodeCount = 0;
			TreeNodeViewModel.ForEachRealizedDescendant(
				[root],
				_ => retainedNodeCount++);
			Assert.Equal(4, retainedNodeCount);
		}
	}

	[Fact]
	public void SearchClose_PrunesNestedCommonAncestorsCreatedBySearch()
	{
		const int matchCount = 1_000;
		var (viewModel, treeView) = CreateContext();
		var features = CreateDescriptor(
			"features",
			Enumerable
				.Range(0, matchCount)
				.Select(index => CreateDescriptor(
					$"feature-{index:D4}",
					CreateDescriptor($"match-{index:D4}.cs")))
				.ToArray());
		var root = CreateLazyTree(
			CreateDescriptor(
				"Root",
				CreateDescriptor("src", features)));
		_ = root.Children.Count;
		viewModel.TreeNodes.Add(root);

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "match-";
		coordinator.UpdateSearchMatches();

		viewModel.SearchQuery = string.Empty;
		coordinator.UpdateSearchMatches();

		var retainedNodes = new List<TreeNodeViewModel>();
		TreeNodeViewModel.ForEachRealizedDescendant(
			[root],
			retainedNodes.Add);
		Assert.Equal(
			["Root", "src", "features", "feature-0000", "match-0000.cs"],
			retainedNodes.Select(node => node.DisplayName));
	}

	[Fact]
	public void SearchClose_PreservesCheckedAndCurrentPathsWithoutRetainingSiblings()
	{
		const int matchCount = 1_000;
		var (viewModel, treeView) = CreateContext();
		var features = CreateDescriptor(
			"features",
			Enumerable
				.Range(0, matchCount)
				.Select(index => CreateDescriptor(
					$"feature-{index:D4}",
					CreateDescriptor($"match-{index:D4}.cs")))
				.ToArray());
		var root = CreateLazyTree(
			CreateDescriptor(
				"Root",
				CreateDescriptor("src", features)));
		_ = root.Children.Count;
		viewModel.TreeNodes.Add(root);

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "match-";
		coordinator.UpdateSearchMatches();
		var featuresNode = root.Children[0].Children[0];
		var checkedBranch = featuresNode.Children[^1];
		var checkedNode = checkedBranch.Children[0];
		checkedNode.IsChecked = true;

		viewModel.SearchQuery = string.Empty;
		coordinator.UpdateSearchMatches();

		var retainedNodes = new List<TreeNodeViewModel>();
		TreeNodeViewModel.ForEachRealizedDescendant(
			[root],
			retainedNodes.Add);
		Assert.Equal(6, retainedNodes.Count);
		Assert.Contains(retainedNodes, node => node.DisplayName == "match-0000.cs");
		Assert.Contains(retainedNodes, node => node.DisplayName == $"feature-{matchCount - 1:D4}");
		Assert.DoesNotContain(retainedNodes, node => node.DisplayName == $"match-{matchCount - 1:D4}.cs");

		var checkedPaths = new HashSet<string>(PathComparer.Default);
		root.CollectCheckedPaths(checkedPaths);
		Assert.Equal([checkedBranch.FullPath], checkedPaths);

		viewModel.SearchQuery = "match-";
		coordinator.UpdateSearchMatches();
		viewModel.SearchQuery = string.Empty;
		coordinator.UpdateSearchMatches();

		checkedPaths.Clear();
		root.CollectCheckedPaths(checkedPaths);
		Assert.Equal([checkedBranch.FullPath], checkedPaths);
		retainedNodes.Clear();
		TreeNodeViewModel.ForEachRealizedDescendant(
			[root],
			retainedNodes.Add);
		Assert.Equal(6, retainedNodes.Count);
	}

	[Fact]
	public void SearchClose_ToggledBackCheckboxDoesNotRetainObsoleteBranch()
	{
		const int matchCount = 1_000;
		var (viewModel, treeView) = CreateContext();
		var features = CreateDescriptor(
			"features",
			Enumerable
				.Range(0, matchCount)
				.Select(index => CreateDescriptor(
					$"feature-{index:D4}",
					CreateDescriptor($"match-{index:D4}.cs")))
				.ToArray());
		var root = CreateLazyTree(
			CreateDescriptor(
				"Root",
				CreateDescriptor("src", features)));
		_ = root.Children.Count;
		viewModel.TreeNodes.Add(root);

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "match-";
		coordinator.UpdateSearchMatches();
		var toggledNode =
			root.Children[0].Children[0].Children[^1].Children[0];
		toggledNode.IsChecked = true;
		toggledNode.IsChecked = false;

		viewModel.SearchQuery = string.Empty;
		coordinator.UpdateSearchMatches();

		var retainedNodeCount = 0;
		TreeNodeViewModel.ForEachRealizedDescendant(
			[root],
			_ => retainedNodeCount++);
		Assert.Equal(5, retainedNodeCount);

		var checkedPaths = new HashSet<string>(PathComparer.Default);
		root.CollectCheckedPaths(checkedPaths);
		Assert.Empty(checkedPaths);
	}

	[Fact]
	public void SearchClose_PreservesActualManualTreeSelection()
	{
		var (viewModel, treeView) = CreateContext();
		var commonAncestor = CreateDescriptor(
			"src",
			CreateDescriptor("first", CreateDescriptor("match-first.cs")),
			CreateDescriptor("second", CreateDescriptor("match-second.cs")));
		var root = CreateLazyTree(CreateDescriptor("Root", commonAncestor));
		_ = root.Children.Count;
		viewModel.TreeNodes.Add(root);

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "match-";
		coordinator.UpdateSearchMatches();
		var commonAncestorNode = root.Children[0];
		var manuallySelected = commonAncestorNode.Children[1].Children[0];
		treeView.SelectedItem = manuallySelected;
		manuallySelected.IsSelected = true;

		viewModel.SearchQuery = string.Empty;
		coordinator.UpdateSearchMatches();
		commonAncestorNode.IsExpanded = true;
		commonAncestorNode.Children[1].IsExpanded = true;

		Assert.Same(manuallySelected, treeView.SelectedItem);
		Assert.Same(
			manuallySelected,
			commonAncestorNode.Children[1].Children[0]);
	}

	[Fact]
	public void SearchClose_IgnoresDetachedTreeSelectionAndKeepsLiveMatch()
	{
		var (viewModel, treeView) = CreateContext();
		var root = CreateLazyTree(
			CreateDescriptor(
				"Root",
				CreateDescriptor("src", CreateDescriptor("match.cs"))));
		_ = root.Children.Count;
		viewModel.TreeNodes.Add(root);

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "match";
		coordinator.UpdateSearchMatches();
		var liveMatch = Assert.IsType<TreeNodeViewModel>(treeView.SelectedItem);
		var detachedSelection = CreateLazyTree(
			CreateDescriptor(
				"OldRoot",
				CreateDescriptor("stale.cs")));
		treeView.SelectedItem = detachedSelection.Children[0];

		viewModel.SearchQuery = string.Empty;
		coordinator.UpdateSearchMatches();

		Assert.Same(liveMatch, treeView.SelectedItem);
		var attachedRoot = Assert.IsType<TreeNodeViewModel>(treeView.SelectedItem);
		while (attachedRoot.Parent is not null)
			attachedRoot = attachedRoot.Parent;
		Assert.Same(root, attachedRoot);
	}

	[Fact]
	public void UpdateSearchMatches_NoMatchDoesNotMaterializeLazyBranches()
	{
		var (viewModel, treeView) = CreateContext();
		var childDescriptor = CreateDescriptor("child.cs");
		var folderDescriptor = CreateDescriptor("src", childDescriptor);
		var rootDescriptor = CreateDescriptor("Root", folderDescriptor);
		var factoryCalls = 0;
		var root = new TreeNodeViewModel(rootDescriptor, null, null);
		var folder = new TreeNodeViewModel(
			folderDescriptor,
			root,
			null,
			parent =>
			{
				factoryCalls++;
				return [new TreeNodeViewModel(childDescriptor, parent, null)];
			});
		root.Children.Add(folder);
		viewModel.TreeNodes.Add(root);

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "missing";
		coordinator.UpdateSearchMatches();

		Assert.Equal(0, factoryCalls);
		Assert.False(folder.AreChildrenRealized);
		Assert.False(coordinator.HasMatches);
	}

	[Theory]
	[InlineData(3_000)]
	[InlineData(4_000)]
	public void UpdateSearchMatches_BroadDispersedMatchSetSettlesBeforeNavigation(
		int branchCount)
	{
		var (viewModel, treeView) = CreateContext();
		var (root, getRealizedFactoryCount) =
			CreateBroadLazySearchTree(branchCount);

		viewModel.TreeNodes.Add(root);
		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "match-";

		coordinator.UpdateSearchMatches();

		Assert.Equal(branchCount, viewModel.SearchTotalMatches);
		Assert.Equal(branchCount, getRealizedFactoryCount());
		Assert.Equal("match-0000.txt", Assert.IsType<TreeNodeViewModel>(treeView.SelectedItem).DisplayName);

		var expandedBeforeNavigation = root.Children
			.Select(node => node.IsExpanded)
			.ToArray();
		coordinator.Navigate(1);

		Assert.Equal(branchCount, getRealizedFactoryCount());
		Assert.Equal(
			expandedBeforeNavigation,
			root.Children.Select(node => node.IsExpanded));
		Assert.Equal(
			"match-0001.txt",
			Assert.IsType<TreeNodeViewModel>(treeView.SelectedItem).DisplayName);
	}

	[AvaloniaFact]
	public async Task UpdateSearchMatchesAsync_BroadDispersedMatchSetSettlesBeforeNavigation()
	{
		const int branchCount = 2_501;
		var (viewModel, treeView) = CreateContext();
		var (root, getRealizedFactoryCount) =
			CreateBroadLazySearchTree(branchCount);
		viewModel.TreeNodes.Add(root);
		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "match-";

		await coordinator.UpdateSearchMatchesAsync();

		Assert.False(viewModel.IsSearchInProgress);
		Assert.Equal(branchCount, viewModel.SearchTotalMatches);
		Assert.Equal(branchCount, getRealizedFactoryCount());
		var expandedBeforeNavigation = root.Children
			.Select(node => node.IsExpanded)
			.ToArray();

		coordinator.Navigate(1);

		Assert.Equal(branchCount, getRealizedFactoryCount());
		Assert.Equal(
			expandedBeforeNavigation,
			root.Children.Select(node => node.IsExpanded));
		Assert.Equal(
			"match-0001.txt",
			Assert.IsType<TreeNodeViewModel>(treeView.SelectedItem).DisplayName);
	}

	[Fact]
	public void UpdateSearchMatches_WideTreePreparesEveryResultBeforeNavigation()
	{
		const int matchCount = 360;
		const int branchCount = 3_000;
		var (viewModel, treeView) = CreateContext();
		var branchDescriptors = Enumerable.Range(0, branchCount)
			.Select(index => CreateDescriptor(
				$"group-{index:D4}",
				CreateDescriptor(
					index < matchCount
						? $"match-{index:D4}.txt"
						: $"ordinary-{index:D4}.txt")))
			.ToArray();
		var rootDescriptor = CreateDescriptor("Root", branchDescriptors);
		var realizedFactories = 0;
		var root = new TreeNodeViewModel(rootDescriptor, null, null);
		foreach (var branchDescriptor in branchDescriptors)
		{
			root.Children.Add(new TreeNodeViewModel(
				branchDescriptor,
				root,
				null,
				parent =>
				{
					Interlocked.Increment(ref realizedFactories);
					return
					[
						new TreeNodeViewModel(
							parent.Descriptor.Children[0],
							parent,
							null)
					];
				}));
		}

		viewModel.TreeNodes.Add(root);
		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "match-";

		coordinator.UpdateSearchMatches();

		Assert.Equal(matchCount, viewModel.SearchTotalMatches);
		Assert.Equal(matchCount, realizedFactories);
		Assert.All(
			root.Children.Take(matchCount),
			node => Assert.True(node.IsExpanded));
		Assert.All(
			root.Children.Skip(matchCount),
			node => Assert.False(node.IsExpanded));
		Assert.Equal(
			"match-0000.txt",
			Assert.IsType<TreeNodeViewModel>(
				treeView.SelectedItem).DisplayName);

		var expandedBeforeNavigation = root.Children
			.Select(node => node.IsExpanded)
			.ToArray();
		for (var index = 0; index < 50; index++)
			coordinator.Navigate(1);

		Assert.Equal(matchCount, realizedFactories);
		Assert.Equal(
			expandedBeforeNavigation,
			root.Children.Select(node => node.IsExpanded));
	}

	[AvaloniaFact]
	public void UpdateSearchMatches_RapidDisjointQueriesLeaveOnlyLatestExpansionState()
	{
		const int branchCountPerQuery = 320;
		var (viewModel, treeView) = CreateContext();
		var rootDescriptor = CreateDescriptor(
			"Root",
			[.. Enumerable.Range(0, branchCountPerQuery)
				.Select(index => CreateDescriptor(
					$"alpha-folder-{index:D4}",
					CreateDescriptor($"alpha-match-{index:D4}.txt"))),
			 .. Enumerable.Range(0, branchCountPerQuery)
				.Select(index => CreateDescriptor(
					$"beta-folder-{index:D4}",
					CreateDescriptor($"beta-match-{index:D4}.txt")))]);
		var root = CreateLazyTree(rootDescriptor);
		viewModel.TreeNodes.Add(root);
		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);

		viewModel.SearchQuery = "alpha-match";
		coordinator.UpdateSearchMatches();
		viewModel.SearchQuery = "beta-match";
		coordinator.UpdateSearchMatches();
		Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);

		Assert.All(
			root.Children.Take(branchCountPerQuery),
			node => Assert.False(node.IsExpanded));
		Assert.All(
			root.Children.Skip(branchCountPerQuery),
			node => Assert.True(node.IsExpanded));
	}

	[AvaloniaFact]
	public void UpdateSearchMatches_NarrowThenBroadQueryReplacesExpansionState()
	{
		const int narrowBranchCount = 320;
		const int totalBranchCount = 2_700;
		var (viewModel, treeView) = CreateContext();
		var branchDescriptors = Enumerable
			.Range(0, totalBranchCount)
			.Select(index => CreateDescriptor(
				$"folder-{index:D4}",
				CreateDescriptor(
					index < narrowBranchCount
						? $"narrow-match-{index:D4}.txt"
						: $"broad-match-{index:D4}.txt")))
			.ToArray();
		var root = CreateLazyTree(CreateDescriptor("Root", branchDescriptors));
		viewModel.TreeNodes.Add(root);
		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);

		viewModel.SearchQuery = "narrow-match";
		coordinator.UpdateSearchMatches();
		viewModel.SearchQuery = "match";
		coordinator.UpdateSearchMatches();
		Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);

		Assert.All(root.Children, node => Assert.True(node.IsExpanded));
		Assert.Equal(totalBranchCount, viewModel.SearchTotalMatches);
	}

	private static (MainWindowViewModel viewModel, TreeView treeView) CreateContext()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>()
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var viewModel = new MainWindowViewModel(localization, new HelpContentProvider());
		var treeView = new TreeView();
		return (viewModel, treeView);
	}

	private static TreeNodeViewModel CreateTree()
	{
		var deltaDescriptor = CreateDescriptor("Delta");
		var betaDescriptor = CreateDescriptor("Beta", deltaDescriptor);
		var alphaDescriptor = CreateDescriptor("Alpha");
		var rootDescriptor = CreateDescriptor("Root", alphaDescriptor, betaDescriptor);
		var root = new TreeNodeViewModel(rootDescriptor, null, null);
		var alpha = new TreeNodeViewModel(alphaDescriptor, root, null);
		var beta = new TreeNodeViewModel(betaDescriptor, root, null);
		var delta = new TreeNodeViewModel(deltaDescriptor, beta, null);

		beta.Children.Add(delta);
		root.Children.Add(alpha);
		root.Children.Add(beta);
		return root;
	}

	private static TreeNodeViewModel CreateLazyTree(
		TreeNodeDescriptor rootDescriptor)
	{
		TreeNodeViewModel CreateNode(
			TreeNodeDescriptor descriptor,
			TreeNodeViewModel? parent) =>
			new(
				descriptor,
				parent,
				null,
				current => current.Descriptor.Children
					.Select(child => CreateNode(child, current))
					.ToArray());

		return CreateNode(rootDescriptor, parent: null);
	}

	private static (
		TreeNodeViewModel Root,
		Func<int> GetRealizedFactoryCount)
		CreateBroadLazySearchTree(int branchCount)
	{
		var branchDescriptors = Enumerable
			.Range(0, branchCount)
			.Select(index => CreateDescriptor(
				$"group-{index:D4}",
				CreateDescriptor($"match-{index:D4}.txt")))
			.ToArray();
		var root = new TreeNodeViewModel(
			CreateDescriptor("Root", branchDescriptors),
			null,
			null);
		var realizedFactoryCount = 0;
		foreach (var branchDescriptor in branchDescriptors)
		{
			root.Children.Add(new TreeNodeViewModel(
				branchDescriptor,
				root,
				null,
				parent =>
				{
					Interlocked.Increment(ref realizedFactoryCount);
					return
					[
						new TreeNodeViewModel(
							parent.Descriptor.Children[0],
							parent,
							null)
					];
				}));
		}

		return (
			root,
			() => Volatile.Read(ref realizedFactoryCount));
	}

	private static TreeNodeDescriptor CreateDescriptor(string name, params TreeNodeDescriptor[] children)
	{
		return new TreeNodeDescriptor(
			DisplayName: name,
			FullPath: $"C:\\{name}",
			IsDirectory: true,
			IsAccessDenied: false,
			IconKey: "icon",
			Children: children);
	}

	private sealed class BlockingDescriptorList(IReadOnlyList<TreeNodeDescriptor> items)
		: IReadOnlyList<TreeNodeDescriptor>
	{
		private readonly ManualResetEventSlim _blockedRead = new(initialState: false);
		private readonly ManualResetEventSlim _release = new(initialState: false);
		private int _blockReads;

		public int ReaderThreadId { get; private set; }

		public int Count
		{
			get
			{
				PauseIfRequested();
				return items.Count;
			}
		}

		public TreeNodeDescriptor this[int index]
		{
			get
			{
				PauseIfRequested();
				return items[index];
			}
		}

		public void BlockReads() => Volatile.Write(ref _blockReads, 1);

		public void ReleaseReads() => _release.Set();

		public Task<bool> WaitForBlockedReadAsync(TimeSpan timeout) =>
			Task.Run(() => _blockedRead.Wait(timeout));

		public IEnumerator<TreeNodeDescriptor> GetEnumerator() => items.GetEnumerator();

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

		private void PauseIfRequested()
		{
			if (Volatile.Read(ref _blockReads) == 0)
				return;

			ReaderThreadId = Environment.CurrentManagedThreadId;
			_blockedRead.Set();
			_release.Wait();
		}
	}

	private sealed class RecordingToastService : IToastService
	{
		public ObservableCollection<ToastMessageViewModel> Items { get; } = [];

		public void Show(string message) => Items.Add(new ToastMessageViewModel(message));

		public void Show(string message, TimeSpan duration) => Show(message);
	}
}
