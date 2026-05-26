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

	private static void PopulateBranches(MainWindow window, int count)
	{
		var viewModel = UiTestDriver.GetViewModel(window);
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
