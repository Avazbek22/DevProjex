using Avalonia.Automation.Peers;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using DevProjex.Application.Services;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowAccessibilityUiTests(UiWorkspaceFixture workspace)
{
	[AvaloniaFact]
	public async Task IconOnlyButtonsAnnounceTheirLocalizedPurpose()
	{
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);
		try
		{
			var viewModel = UiTestDriver.GetViewModel(window);
			AssertAutomationName(
				UiTestDriver.GetRequiredTopMenuControl<Button>(window, "PreviewToggleButton"),
				viewModel.PreviewTooltip);
			AssertAutomationName(
				UiTestDriver.GetRequiredTopMenuControl<Button>(window, "FilterToggleButton"),
				viewModel.FilterTooltip);

			var expectedByButton = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["PreviewCopyButton"] = viewModel.PreviewCopyCurrentModeTooltip,
				["PreviewSearchButton"] = viewModel.PreviewSearchTooltip,
				["PreviewCloseButton"] = viewModel.PreviewCloseText,
				["PreviewTreeHideButton"] = viewModel.PreviewHideTreeTooltip,
				["PreviewStickyHeaderCopyButton"] = viewModel.PreviewCopyFilePathTooltip,
				["FilterCloseButton"] = viewModel.FilterCloseText,
				["SearchPreviousButton"] = viewModel.SearchPreviousTooltip,
				["SearchNextButton"] = viewModel.SearchNextTooltip,
				["SearchCloseButton"] = viewModel.SearchCloseText,
				["PreviewSearchPreviousButton"] = viewModel.SearchPreviousTooltip,
				["PreviewSearchNextButton"] = viewModel.SearchNextTooltip,
				["PreviewSearchCloseButton"] = viewModel.SearchCloseText
			};
			foreach (var (name, expected) in expectedByButton)
				AssertAutomationName(FindNamed<Button>(window, name), expected);

			Assert.Equal("Close preview", viewModel.PreviewCloseText);
			Assert.Equal("Close filter", viewModel.FilterCloseText);
			Assert.Equal("Close search", viewModel.SearchCloseText);

			var aboutPopover = UiTestDriver.GetRequiredTopMenuControl<AboutPopoverView>(window, "HelpPopover");
			AssertAutomationName(
				Assert.IsType<Button>(aboutPopover.FindControl<Button>("AboutCloseButton")),
				viewModel.PopoverCloseText);
			var updatePopover = UiTestDriver.GetRequiredTopMenuControl<UpdatePopoverView>(window, "UpdatePopover");
			AssertAutomationName(
				Assert.IsType<Button>(updatePopover.FindControl<Button>("UpdateCloseButton")),
				viewModel.PopoverCloseText);
			Assert.Equal("Close", viewModel.PopoverCloseText);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task SettingsOptionsAndTreeRowsAnnounceTheirLabels()
	{
		using var project = UiTestProject.CreateWithDynamicIgnoreEntries();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		try
		{
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);
			var ignoreItems = GetRealizedItems<ListBoxItem>(window, "IgnoreOptionsList");
			Assert.NotEmpty(ignoreItems);
			foreach (var item in ignoreItems)
				AssertAutomationName(item, Assert.IsType<IgnoreOptionViewModel>(item.DataContext).Label);

			var processingItems = GetRealizedItems<ListBoxItem>(window, "ContentProcessingOptionsList");
			Assert.NotEmpty(processingItems);
			foreach (var item in processingItems)
			{
				var option = Assert.IsType<IgnoreOptionViewModel>(item.DataContext);
				AssertAutomationName(item, option.Label);
				AssertAutomationName(Assert.Single(item.GetVisualDescendants().OfType<CheckBox>()), option.Label);
			}

			var treeItems = GetRealizedItems<TreeViewItem>(window, "ProjectTree");
			Assert.NotEmpty(treeItems);
			foreach (var item in treeItems)
			{
				var node = Assert.IsType<TreeNodeViewModel>(item.DataContext);
				AssertAutomationName(item, node.DisplayName);
				var checkBox = item.GetVisualDescendants()
					.OfType<CheckBox>()
					.First(candidate => ReferenceEquals(candidate.DataContext, node));
				AssertAutomationName(checkBox, node.DisplayName);
			}
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task AutomationNamesFollowTheInterfaceLanguage()
	{
		LocalizationService? localization = null;
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			configureServices: services =>
			{
				localization = services.Localization;
				return services;
			});
		try
		{
			Assert.IsType<LocalizationService>(localization).SetLanguage(AppLanguage.Ru);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
			var viewModel = UiTestDriver.GetViewModel(window);

			AssertAutomationName(
				FindNamed<Button>(window, "PreviewCloseButton"),
				"Закрыть превью");
			AssertAutomationName(
				FindNamed<Button>(window, "FilterCloseButton"),
				"Закрыть фильтр");
			AssertAutomationName(
				UiTestDriver.GetRequiredTopMenuControl<Button>(window, "FilterToggleButton"),
				viewModel.FilterTooltip);
			Assert.StartsWith("Фильтр", viewModel.FilterTooltip, StringComparison.Ordinal);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	private static TItem[] GetRealizedItems<TItem>(MainWindow window, string listName)
		where TItem : Control =>
		FindNamed<Control>(window, listName)
			.GetVisualDescendants()
			.OfType<TItem>()
			.Where(static item => item.DataContext is not null)
			.ToArray();

	// Bar and popover views keep their own name scopes, so a window-level lookup alone misses them.
	private static TControl FindNamed<TControl>(MainWindow window, string name)
		where TControl : Control
	{
		return window.GetVisualDescendants()
			.Concat(window.GetLogicalDescendants().OfType<Visual>())
			.OfType<TControl>()
			.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal)) ??
			throw new InvalidOperationException($"Control '{name}' was not found.");
	}

	private static void AssertAutomationName(Control control, string expected)
	{
		Assert.False(string.IsNullOrWhiteSpace(expected));
		var actual = ControlAutomationPeer.CreatePeerForElement(control).GetName();
		Assert.Equal(expected, actual);
		Assert.DoesNotContain("Avalonia.", actual, StringComparison.Ordinal);
		Assert.DoesNotContain("ViewModel", actual, StringComparison.Ordinal);
	}
}
