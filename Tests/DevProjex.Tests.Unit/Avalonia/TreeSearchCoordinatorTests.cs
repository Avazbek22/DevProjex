using Avalonia.Controls;

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

	[Fact]
	public void TryNavigateForCurrentQuery_FreshForwardQuery_SelectsFirstMatchThenAdvances()
	{
		var (viewModel, treeView) = CreateContext();
		var root = CreateTree();
		viewModel.TreeNodes.Add(root);
		viewModel.SearchVisible = true;

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "ta";
		viewModel.SetSearchInProgress(true);

		Assert.True(coordinator.TryNavigateForCurrentQuery(1));
		Assert.Same(root.Children[1], treeView.SelectedItem);
		Assert.Equal("(1 / 2)", viewModel.SearchMatchSummaryText);

		Assert.True(coordinator.TryNavigateForCurrentQuery(1));
		Assert.Same(root.Children[1].Children[0], treeView.SelectedItem);
		Assert.Equal("(2 / 2)", viewModel.SearchMatchSummaryText);
	}

	[Fact]
	public void TryNavigateForCurrentQuery_WhenQueryChanges_RefreshesMatchesWithoutSkippingFirstResult()
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
		Assert.Same(root.Children[1], treeView.SelectedItem);
		Assert.Equal("(1 / 2)", viewModel.SearchMatchSummaryText);
	}

	[Fact]
	public void TryNavigateForCurrentQuery_FreshBackwardQuery_WrapsToLastMatch()
	{
		var (viewModel, treeView) = CreateContext();
		var root = CreateTree();
		viewModel.TreeNodes.Add(root);
		viewModel.SearchVisible = true;

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "ta";

		Assert.True(coordinator.TryNavigateForCurrentQuery(-1));
		Assert.Same(root.Children[1].Children[0], treeView.SelectedItem);
		Assert.Equal("(2 / 2)", viewModel.SearchMatchSummaryText);
	}

	[Fact]
	public void TryNavigateForCurrentQuery_WhenNoMatches_ReturnsFalse()
	{
		var (viewModel, treeView) = CreateContext();
		var root = CreateTree();
		viewModel.TreeNodes.Add(root);
		viewModel.SearchVisible = true;

		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		viewModel.SearchQuery = "___no_match___";

		Assert.False(coordinator.TryNavigateForCurrentQuery(1));
		Assert.False(coordinator.HasMatches);
		Assert.Equal("(0 / 0)", viewModel.SearchMatchSummaryText);
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
	[InlineData(100, 0, 30)]
	[InlineData(100, -20, 10)]
	[InlineData(100, 190, 230)]
	public void ResolveVerticalOffsetForSearchNavigation_WhenTargetIsAlreadyPartlyVisible_KeepsCurrentOffset(
		double currentOffsetY,
		double itemTop,
		double itemBottom)
	{
		var targetOffsetY = TreeSearchCoordinator.ResolveVerticalOffsetForSearchNavigation(
			currentOffsetY,
			itemTop,
			itemBottom,
			viewportHeight: 200,
			extentHeight: 1000);

		Assert.Equal(currentOffsetY, targetOffsetY);
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
		var root = new TreeNodeViewModel(CreateDescriptor("Root"), null, null);
		var alpha = new TreeNodeViewModel(CreateDescriptor("Alpha"), root, null);
		var beta = new TreeNodeViewModel(CreateDescriptor("Beta"), root, null);
		var delta = new TreeNodeViewModel(CreateDescriptor("Delta"), beta, null);

		beta.Children.Add(delta);
		root.Children.Add(alpha);
		root.Children.Add(beta);
		return root;
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
}
