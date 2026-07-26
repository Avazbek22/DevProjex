using Avalonia.Controls;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class TreeSearchCoordinatorDebounceTests
{
	[Fact]
	public void OnSearchQueryChanged_MultipleCalls_ReplacesAndCancelsPreviousDebounceToken()
	{
		var (viewModel, treeView) = CreateContext();
		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);

		coordinator.OnSearchQueryChanged();
		var firstCts = GetPrivateField<CancellationTokenSource>(coordinator, "_searchDebounceCts");
		Assert.NotNull(firstCts);

		coordinator.OnSearchQueryChanged();
		var secondCts = GetPrivateField<CancellationTokenSource>(coordinator, "_searchDebounceCts");
		Assert.NotNull(secondCts);

		coordinator.OnSearchQueryChanged();
		var thirdCts = GetPrivateField<CancellationTokenSource>(coordinator, "_searchDebounceCts");
		Assert.NotNull(thirdCts);

		Assert.True(firstCts!.IsCancellationRequested);
		Assert.True(secondCts!.IsCancellationRequested);
		Assert.False(thirdCts!.IsCancellationRequested);
	}

	[Fact]
	public void OnSearchQueryChanged_IncrementsSearchVersion()
	{
		var (viewModel, treeView) = CreateContext();
		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);

		var initialVersion = GetPrivateField<int>(coordinator, "_searchVersion");
		coordinator.OnSearchQueryChanged();
		coordinator.OnSearchQueryChanged();
		var currentVersion = GetPrivateField<int>(coordinator, "_searchVersion");

		Assert.Equal(initialVersion + 2, currentVersion);
	}

	[Fact]
	public void QueryChangeCancelAndResultReplacement_InvalidatePendingBringIntoView()
	{
		var (viewModel, treeView) = CreateContext();
		viewModel.TreeNodes.Add(new TreeNodeViewModel(
			new TreeNodeDescriptor(
				"Root",
				"C:\\Root",
				IsDirectory: true,
				IsAccessDenied: false,
				IconKey: "folder",
				Children: []),
			null,
			null));
		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);

		var initialVersion = GetPrivateField<int>(coordinator, "_bringIntoViewVersion");
		coordinator.OnSearchQueryChanged();
		var afterQueryChange = GetPrivateField<int>(coordinator, "_bringIntoViewVersion");
		coordinator.CancelPending();
		var afterCancel = GetPrivateField<int>(coordinator, "_bringIntoViewVersion");
		coordinator.UpdateSearchMatches();
		var afterResultReplacement = GetPrivateField<int>(coordinator, "_bringIntoViewVersion");

		Assert.True(afterQueryChange > initialVersion);
		Assert.True(afterCancel > afterQueryChange);
		Assert.True(afterResultReplacement > afterCancel);
	}

	[Fact]
	public void CancelPending_CancelsDebounceAndActiveSearchAndHighlightTokens()
	{
		var (viewModel, treeView) = CreateContext();
		using var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		coordinator.OnSearchQueryChanged();

		var debounceCts = GetPrivateField<CancellationTokenSource>(coordinator, "_searchDebounceCts");
		var searchCts = new CancellationTokenSource();
		var highlightCts = new CancellationTokenSource();
		SetPrivateField(coordinator, "_searchCts", searchCts);
		SetPrivateField(coordinator, "_highlightApplyCts", highlightCts);

		coordinator.CancelPending();

		Assert.NotNull(debounceCts);
		Assert.True(debounceCts!.IsCancellationRequested);
		Assert.True(searchCts.IsCancellationRequested);
		Assert.True(highlightCts.IsCancellationRequested);
	}

	[Fact]
	public void Dispose_CancelsDebounceTokenAndClearsTokenReferences()
	{
		var (viewModel, treeView) = CreateContext();
		var coordinator = new TreeSearchCoordinator(viewModel, treeView);
		coordinator.OnSearchQueryChanged();
		var debounceCts = GetPrivateField<CancellationTokenSource>(coordinator, "_searchDebounceCts");

		coordinator.Dispose();

		Assert.NotNull(debounceCts);
		Assert.True(debounceCts!.IsCancellationRequested);
		Assert.Null(GetPrivateField<CancellationTokenSource>(coordinator, "_searchDebounceCts"));
		Assert.Null(GetPrivateField<CancellationTokenSource>(coordinator, "_searchCts"));
		Assert.Null(GetPrivateField<CancellationTokenSource>(coordinator, "_highlightApplyCts"));
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

	private static T? GetPrivateField<T>(TreeSearchCoordinator coordinator, string fieldName)
	{
		var field = typeof(TreeSearchCoordinator).GetField(
			fieldName,
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		return (T?)field!.GetValue(coordinator);
	}

	private static void SetPrivateField(TreeSearchCoordinator coordinator, string fieldName, object value)
	{
		var field = typeof(TreeSearchCoordinator).GetField(
			fieldName,
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		field!.SetValue(coordinator, value);
	}
}

