using DevProjex.Infrastructure.RecentProjects;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowRecentProjectsUiTests(UiWorkspaceFixture workspace)
{
	[AvaloniaFact]
	public async Task FileMenu_RecentFolders_ShowsPersistedEntriesInOrder()
	{
		var appDataPath = Path.Combine(workspace.Project.AppDataPath, Guid.NewGuid().ToString("N"));
		var recentStore = new RecentProjectsStore(() => appDataPath);
		var firstPath = Path.Combine(workspace.Project.RootPath, "history", "alpha");
		var secondPath = Path.Combine(workspace.Project.RootPath, "history", "beta");
		var repositoryUrl = "https://github.com/example/recent-repo";
		Directory.CreateDirectory(firstPath);
		Directory.CreateDirectory(secondPath);

		var db = recentStore.Load();
		db = recentStore.AddFolder(db, firstPath);
		db = recentStore.AddFolder(db, secondPath);
		db = recentStore.AddFolder(db, firstPath);
		db = recentStore.AddRepository(db, repositoryUrl);

		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project, appDataPathOverride: appDataPath);

		try
		{
			var recentMenu = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "RecentMenuItem");
			Assert.True(recentMenu.IsVisible);
			Assert.Equal(3, recentMenu.Items.Count);

			var recentItems = recentMenu.Items.OfType<MenuItem>().ToArray();
			Assert.Equal(workspace.Project.RootPath, recentItems[0].Tag);
			Assert.Equal(firstPath, recentItems[1].Tag);
			Assert.Equal(secondPath, recentItems[2].Tag);
			Assert.DoesNotContain(recentItems, item => string.Equals(item.Tag as string, repositoryUrl, StringComparison.OrdinalIgnoreCase));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task FileMenu_RecentFolders_ShowsWhenOnlyCurrentWorkspaceIsPresent()
	{
		var appDataPath = Path.Combine(workspace.Project.AppDataPath, Guid.NewGuid().ToString("N"));
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project, appDataPathOverride: appDataPath);

		try
		{
			var recentMenu = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "RecentMenuItem");

			Assert.True(recentMenu.IsVisible);
			Assert.Single(recentMenu.Items.OfType<MenuItem>());
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task FileMenu_RecentFolders_DoesNotShowApplicationStateDirectory()
	{
		var appDataPath = Path.Combine(workspace.Project.AppDataPath, Guid.NewGuid().ToString("N"));
		var recentStore = new RecentProjectsStore(() => appDataPath);
		var validFolder = Path.Combine(workspace.Project.RootPath, "history", "visible-folder");
		var applicationStateDirectory = Path.Combine(appDataPath, "DevProjex");
		Directory.CreateDirectory(validFolder);
		Directory.CreateDirectory(applicationStateDirectory);

		var db = recentStore.Load();
		db = recentStore.AddFolder(db, applicationStateDirectory);
		db = recentStore.AddFolder(db, validFolder);

		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project, appDataPathOverride: appDataPath);

		try
		{
			var recentMenu = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "RecentMenuItem");
			var recentItems = recentMenu.Items.OfType<MenuItem>().ToArray();

			Assert.Contains(recentItems, item => string.Equals(item.Tag as string, workspace.Project.RootPath, StringComparison.Ordinal));
			Assert.Contains(recentItems, item => string.Equals(item.Tag as string, validFolder, StringComparison.Ordinal));
			Assert.DoesNotContain(recentItems, item => string.Equals(item.Tag as string, applicationStateDirectory, StringComparison.Ordinal));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task FileMenu_RecentFolders_DoesNotAttachTooltipToItems()
	{
		var appDataPath = Path.Combine(workspace.Project.AppDataPath, Guid.NewGuid().ToString("N"));
		var recentStore = new RecentProjectsStore(() => appDataPath);
		var folderPath = Path.Combine(workspace.Project.RootPath, "history", "tooltip-free");
		Directory.CreateDirectory(folderPath);

		var db = recentStore.Load();
		db = recentStore.AddFolder(db, folderPath);

		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project, appDataPathOverride: appDataPath);

		try
		{
			var recentMenu = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "RecentMenuItem");
			var recentItems = recentMenu.Items.OfType<MenuItem>().ToArray();

			Assert.NotEmpty(recentItems);
			Assert.All(recentItems, item => Assert.Null(ToolTip.GetTip(item)));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task GitCloneWindow_RecentRepositories_FillsUrlFromSelection()
	{
		var appDataPath = Path.Combine(workspace.Project.AppDataPath, Guid.NewGuid().ToString("N"));
		var recentStore = new RecentProjectsStore(() => appDataPath);
		var repositoryUrl = "https://github.com/example/recent-repo";
		var db = recentStore.Load();
		db = recentStore.AddRepository(db, repositoryUrl);

		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project, appDataPathOverride: appDataPath);

		try
		{
			var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);

			try
			{
				var urlTextBox = cloneWindow.FindControl<TextBox>("UrlTextBox");
				var recentComboBox = cloneWindow.FindControl<ComboBox>("RecentRepositoriesComboBox");

				Assert.NotNull(urlTextBox);
				Assert.NotNull(recentComboBox);
				Assert.Equal(UiTestDriver.GetViewModel(window).GitCloneRecentRepositoriesLabel, recentComboBox!.PlaceholderText);
				Assert.True(recentComboBox.Items.OfType<RecentProjectEntryViewModel>().Any());

				recentComboBox.SelectedItem = recentComboBox.Items
					.OfType<RecentProjectEntryViewModel>()
					.Single(item => string.Equals(item.Value, repositoryUrl, StringComparison.OrdinalIgnoreCase));

				await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);

				Assert.Equal(repositoryUrl, urlTextBox!.Text);
			}
			finally
			{
				cloneWindow.Close();
				await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);
			}
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task GitCloneWindow_RecentRepositories_StaysEmptyWhenOnlyFolderHistoryExists()
	{
		var appDataPath = Path.Combine(workspace.Project.AppDataPath, Guid.NewGuid().ToString("N"));
		var recentStore = new RecentProjectsStore(() => appDataPath);
		var folderPath = Path.Combine(workspace.Project.RootPath, "history", "only-folder");
		Directory.CreateDirectory(folderPath);

		var db = recentStore.Load();
		db = recentStore.AddFolder(db, folderPath);

		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project, appDataPathOverride: appDataPath);

		try
		{
			var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);

			try
			{
				var recentContainer = cloneWindow.FindControl<Border>("RecentRepositoriesContainer");
				var recentComboBox = cloneWindow.FindControl<ComboBox>("RecentRepositoriesComboBox");

				Assert.NotNull(recentContainer);
				Assert.NotNull(recentComboBox);
				Assert.False(recentContainer!.IsVisible);
				Assert.Empty(recentComboBox!.Items.OfType<RecentProjectEntryViewModel>());
			}
			finally
			{
				cloneWindow.Close();
				await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);
			}
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task GitCloneWindow_RecentRepositories_PersistAcrossFreshMainWindowInstances()
	{
		var appDataPath = Path.Combine(workspace.Project.AppDataPath, Guid.NewGuid().ToString("N"));
		var recentStore = new RecentProjectsStore(() => appDataPath);
		var repositoryUrl = "https://github.com/example/recent-repo";
		var db = recentStore.Load();
		db = recentStore.AddRepository(db, repositoryUrl);

		var firstWindow = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project, appDataPathOverride: appDataPath);
		var secondWindow = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project, appDataPathOverride: appDataPath);

		try
		{
			foreach (var window in new[] { firstWindow, secondWindow })
			{
				var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
				try
				{
				var recentComboBox = cloneWindow.FindControl<ComboBox>("RecentRepositoriesComboBox");
				var recentContainer = cloneWindow.FindControl<Border>("RecentRepositoriesContainer");

				Assert.NotNull(recentContainer);
				Assert.NotNull(recentComboBox);
				Assert.True(recentContainer!.IsVisible);
				Assert.True(recentComboBox!.IsVisible);
				Assert.True(UiTestDriver.GetViewModel(window).HasRecentRepositories);
				Assert.Contains(
					recentComboBox.Items.OfType<RecentProjectEntryViewModel>(),
					item => string.Equals(item.Value, repositoryUrl, StringComparison.OrdinalIgnoreCase));
				}
				finally
				{
					cloneWindow.Close();
					await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);
				}
			}
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(secondWindow);
			await UiTestDriver.CloseWindowAsync(firstWindow);
		}
	}
}
