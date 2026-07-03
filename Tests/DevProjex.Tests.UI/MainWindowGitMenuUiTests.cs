using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowGitMenuUiTests(UiWorkspaceFixture workspace)
{
	[AvaloniaFact]
	public async Task GitBranchMenu_ShowsNormalItemsWhenBranchCountFitsLimit()
	{
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

		try
		{
			PopulateBranches(window, count: 15);
			InvokeUpdateBranchMenu(window);

			var branchMenu = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "GitBranchMenuItem");
			Assert.Equal(15, branchMenu.Items.Count);
			Assert.DoesNotContain("git-branch-menu-scrollable", branchMenu.Classes);
			Assert.All(branchMenu.Items.OfType<MenuItem>(), item => Assert.Equal(32, item.MinHeight));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task GitBranchMenu_KeepsLargeBranchListAsIndividualMenuItems()
	{
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

		try
		{
			PopulateBranches(window, count: 16);
			InvokeUpdateBranchMenu(window);

			var branchMenu = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "GitBranchMenuItem");
			var branchItems = branchMenu.Items.OfType<MenuItem>().ToArray();

			Assert.Contains("git-branch-menu", branchMenu.Classes);
			Assert.Contains("git-branch-menu-scrollable", branchMenu.Classes);
			Assert.Equal(16, branchMenu.Items.Count);
			Assert.Equal(16, branchItems.Length);
			Assert.DoesNotContain(branchMenu.Items, item => item is ScrollViewer);
			Assert.All(branchItems, item => Assert.Equal(32, item.MinHeight));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task GitBranchMenu_RemovesScrollableClassWhenBranchCountDropsBackToLimit()
	{
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

		try
		{
			PopulateBranches(window, count: 16);
			InvokeUpdateBranchMenu(window);

			var branchMenu = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "GitBranchMenuItem");
			Assert.Contains("git-branch-menu-scrollable", branchMenu.Classes);
			var popup = await OpenBranchPopupAsync(window, branchMenu);
			var popupRoot = Assert.IsAssignableFrom<Visual>(popup.Child);
			Assert.Contains(
				popupRoot
					.GetVisualDescendants()
					.OfType<Control>(),
				control => control.Classes.Contains("git-branch-external-scrollbar"));

			PopulateBranches(window, count: 15);
			InvokeUpdateBranchMenu(window);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 12);

			var refreshedBranchMenu = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "GitBranchMenuItem");
			Assert.DoesNotContain("git-branch-menu-scrollable", refreshedBranchMenu.Classes);
			Assert.Equal(15, refreshedBranchMenu.Items.Count);
			Assert.DoesNotContain(refreshedBranchMenu.Items, item => item is ScrollViewer);

			var refreshedPopup = refreshedBranchMenu
				.GetVisualDescendants()
				.OfType<Popup>()
				.FirstOrDefault(popup => popup.IsOpen);
			if (refreshedPopup?.Child is Visual refreshedPopupRoot)
			{
				Assert.DoesNotContain(
					refreshedPopupRoot
						.GetVisualDescendants()
						.OfType<Control>(),
					control => control.Classes.Contains("git-branch-external-scrollbar"));
			}
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task GitBranchMenu_OpenScrollablePopup_UsesVisibleVerticalScrollBar()
	{
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

		try
		{
			PopulateBranches(window, count: 20);
			InvokeUpdateBranchMenu(window);

			var branchMenu = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "GitBranchMenuItem");
			var popup = await OpenBranchPopupAsync(window, branchMenu);
			var popupRoot = Assert.IsAssignableFrom<Visual>(popup.Child);
			var scrollViewer = Assert.Single(popupRoot.GetVisualDescendants().OfType<ScrollViewer>());
			var externalScrollIndicator = Assert.Single(
				popupRoot.GetVisualDescendants().OfType<Control>(),
				control => control.Classes.Contains("git-branch-external-scrollbar"));
			var indicatorThumb = Assert.Single(
				popupRoot.GetVisualDescendants().OfType<Border>(),
				border => border.Classes.Contains("git-branch-scroll-indicator-thumb"));

			Assert.False(scrollViewer.AllowAutoHide);
			Assert.Equal(ScrollBarVisibility.Visible, scrollViewer.VerticalScrollBarVisibility);
			Assert.True(externalScrollIndicator.IsVisible);
			Assert.True(externalScrollIndicator.Bounds.Width >= 12);
			Assert.True(externalScrollIndicator.Opacity > 0.9);
			Assert.True(indicatorThumb.Bounds.Height >= 32);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	private static async Task<Popup> OpenBranchPopupAsync(MainWindow window, MenuItem branchMenu)
	{
		var gitMenu = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "GitMenuItem");
		gitMenu.IsSubMenuOpen = true;
		await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
		branchMenu.IsSubMenuOpen = true;
		await UiTestDriver.WaitForSettledFramesAsync(frameCount: 12);

		var popup = branchMenu.GetVisualDescendants().OfType<Popup>().FirstOrDefault(popup => popup.IsOpen);
		return Assert.IsType<Popup>(popup);
	}

	private static void PopulateBranches(MainWindow window, int count)
	{
		var viewModel = UiTestDriver.GetViewModel(window);
		viewModel.ProjectSourceType = ProjectSourceType.GitClone;
		viewModel.GitBranches.Clear();
		for (var index = 0; index < count; index++)
			viewModel.GitBranches.Add(new GitBranch($"branch-{index:D2}", IsActive: index == 0, IsRemote: false));
	}

	private static void InvokeUpdateBranchMenu(MainWindow window)
	{
		var method = typeof(MainWindow).GetMethod(
			"UpdateBranchMenu",
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

		Assert.NotNull(method);
		method.Invoke(window, []);
	}
}
