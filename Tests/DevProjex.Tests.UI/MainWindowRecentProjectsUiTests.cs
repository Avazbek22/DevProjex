using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Infrastructure.RecentProjects;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowRecentProjectsUiTests(UiWorkspaceFixture workspace)
{
	[AvaloniaFact]
	public async Task FileMenu_UnavailableFolder_IsMutedThenRestoredAfterNextOpenCheck()
	{
		var appDataPath = Path.Combine(workspace.Project.AppDataPath, Guid.NewGuid().ToString("N"));
		var missingPath = PathUtility.Normalize(Path.Combine(workspace.Project.RootPath, "history", "disconnected"));
		var recentStore = new RecentProjectsStore(() => appDataPath);
		recentStore.AddFolder(recentStore.Load(), missingPath);
		var missingFolderAvailable = 0;
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			appDataPathOverride: appDataPath,
			configureServices: services => services with
			{
				RecentFolderAvailabilityService = new RecentFolderAvailabilityService(path =>
					!PathComparer.Default.Equals(path, missingPath) ||
					Volatile.Read(ref missingFolderAvailable) == 1)
			});

		try
		{
			var recentMenu = await OpenRecentMenuAsync(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => FindRecentItem(recentMenu, missingPath).Classes.Contains("recent-folder-unavailable"),
				"unavailable recent folder to become visually muted");
			Assert.True(FindRecentItem(recentMenu, missingPath).IsEnabled);

			Interlocked.Exchange(ref missingFolderAvailable, 1);
			recentMenu.IsSubMenuOpen = false;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			recentMenu.IsSubMenuOpen = true;
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !FindRecentItem(recentMenu, missingPath).Classes.Contains("recent-folder-unavailable"),
				"available recent folder to return to normal styling");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

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
			Assert.DoesNotContain(
				recentMenu.Items.OfType<MenuItem>(),
				static item => item.Tag is string);

			recentMenu = await OpenRecentMenuAsync(window);
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
	public async Task FileMenu_RecentFolders_LargeHistoryUsesScrollableMenuOnFirstOpen()
	{
		var appDataPath = Path.Combine(workspace.Project.AppDataPath, Guid.NewGuid().ToString("N"));
		var recentStore = new RecentProjectsStore(() => appDataPath);
		var db = recentStore.Load();
		for (var index = 0; index < 20; index++)
		{
			var folderPath = Path.Combine(workspace.Project.RootPath, "history", $"scroll-{index:D2}");
			Directory.CreateDirectory(folderPath);
			db = recentStore.AddFolder(db, folderPath);
		}

		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			appDataPathOverride: appDataPath);

		try
		{
			var recentMenu = await OpenRecentMenuAsync(window);
			Assert.Contains("menu-scrollable", recentMenu.Classes);
			Assert.True(recentMenu.IsSubMenuOpen);
			Assert.True(recentMenu.Items.Count > MenuScrollBehavior.VisibleItemLimit);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 12);
			var popupCandidates = recentMenu.GetVisualDescendants().OfType<Popup>().ToArray();
			Assert.True(
				popupCandidates.Any(static candidate => candidate.IsOpen),
				$"No open recent popup. Popups: {popupCandidates.Length}.");

			var popup = Assert.Single(
				recentMenu.GetVisualDescendants().OfType<Popup>(),
				static candidate => candidate.IsOpen);
			var popupRoot = Assert.IsAssignableFrom<Visual>(popup.Child);
			var scrollViewer = Assert.Single(popupRoot.GetVisualDescendants().OfType<ScrollViewer>());
			var nativeHoverScrollButtons = popupRoot
				.GetVisualDescendants()
				.OfType<RepeatButton>()
				.Where(button => !button.Classes.Contains(MenuScrollBehavior.ArrowButtonClass))
				.ToArray();
			var arrowButtons = popupRoot
				.GetVisualDescendants()
				.OfType<RepeatButton>()
				.Where(button => button.Classes.Contains(MenuScrollBehavior.ArrowButtonClass))
				.ToArray();
			var scrollIndicator = Assert.Single(
				popupRoot.GetVisualDescendants().OfType<Control>(),
				control => control.Classes.Contains("menu-external-scrollbar"));
			var indicatorThumb = Assert.Single(
				popupRoot.GetVisualDescendants().OfType<Border>(),
				border => border.Classes.Contains("menu-scroll-indicator-thumb"));

			Assert.Equal(ScrollBarVisibility.Visible, scrollViewer.VerticalScrollBarVisibility);
			Assert.True(scrollViewer.Bounds.Height <= 520);
			Assert.Equal(2, nativeHoverScrollButtons.Length);
			Assert.All(nativeHoverScrollButtons, static button =>
			{
				Assert.False(button.IsVisible);
				Assert.False(button.IsHitTestVisible);
			});
			Assert.Equal(2, arrowButtons.Length);
			Assert.All(arrowButtons, static button =>
			{
				Assert.True(button.IsVisible);
				Assert.True(button.IsHitTestVisible);
				Assert.True(button.Bounds.Width <= 12);
				Assert.True(button.Bounds.Height <= 14);
				Assert.Equal(0, button.BorderThickness.Left);
				Assert.Equal(0, button.BorderThickness.Top);
				Assert.Equal(0, button.BorderThickness.Right);
				Assert.Equal(0, button.BorderThickness.Bottom);
				var icon = Assert.IsType<PathIcon>(button.Content);
				Assert.NotNull(icon.Foreground);
			});

			var menuItems = popupRoot
				.GetVisualDescendants()
				.OfType<MenuItem>()
				.ToArray();
			var partiallyVisibleItems = menuItems
				.Where(item => item.TranslatePoint(default, scrollViewer) is { } origin &&
				               origin.Y < scrollViewer.Viewport.Height &&
				               origin.Y + item.Bounds.Height > scrollViewer.Viewport.Height)
				.ToArray();
			Assert.Empty(partiallyVisibleItems);
			Assert.True(scrollIndicator.IsVisible);
			Assert.True(indicatorThumb.Bounds.Height >= 32);

			var firstOrigin = Assert.IsType<Point>(menuItems[0].TranslatePoint(default, scrollViewer));
			var secondOrigin = Assert.IsType<Point>(menuItems[1].TranslatePoint(default, scrollViewer));
			var rowStep = secondOrigin.Y - firstOrigin.Y;
			var rawMaximumOffset = scrollViewer.Extent.Height - scrollViewer.Viewport.Height;
			var maximumOffset = Math.Floor(rawMaximumOffset / rowStep) * rowStep;
			Assert.True(maximumOffset > 0);
			var upButton = Assert.Single(arrowButtons, static button => Grid.GetRow(button) == 0);
			var downButton = Assert.Single(arrowButtons, static button => Grid.GetRow(button) == 2);
			Assert.False(upButton.IsEnabled);
			Assert.True(downButton.IsEnabled);

			var lastInitiallyVisibleItem = menuItems
				.Last(item => item.TranslatePoint(default, scrollViewer) is { } origin &&
				              origin.Y + item.Bounds.Height <= scrollViewer.Viewport.Height);
			lastInitiallyVisibleItem.BringIntoView();
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.Equal(0, scrollViewer.Offset.Y, precision: 3);

			downButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.Equal(rowStep, scrollViewer.Offset.Y, precision: 3);

			scrollViewer.Offset = new Vector(0, rowStep * 1.4);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.Equal(rowStep, scrollViewer.Offset.Y, precision: 3);

			scrollViewer.Offset = new Vector(0, rawMaximumOffset);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.Equal(maximumOffset, scrollViewer.Offset.Y, precision: 3);
			Assert.True(upButton.IsEnabled);
			Assert.False(downButton.IsEnabled);
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
			var recentMenu = await OpenRecentMenuAsync(window);

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
			var recentMenu = await OpenRecentMenuAsync(window);
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
			var recentMenu = await OpenRecentMenuAsync(window);
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
	public async Task GitCloneWindow_RecentRepositorySelectionRemainsVisibleUntilUrlIsEdited()
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

				var recentEntry = recentComboBox.Items
					.OfType<RecentProjectEntryViewModel>()
					.Single(item => string.Equals(item.Value, repositoryUrl, StringComparison.OrdinalIgnoreCase));
				recentComboBox.IsDropDownOpen = true;
				await UiTestDriver.WaitForSettledFramesAsync(frameCount: 3);
				var popup = Assert.Single(
					recentComboBox.GetVisualDescendants().OfType<Popup>(),
					static candidate => candidate.IsOpen);
				var recentItem = Assert.Single(
					popup.Child!.GetVisualDescendants().OfType<ComboBoxItem>(),
					candidate => ReferenceEquals(candidate.DataContext, recentEntry));
				await UiTestDriver.ClickAsync(window, recentItem);

				await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);

				Assert.Equal(repositoryUrl, urlTextBox!.Text);
				Assert.Same(recentEntry, recentComboBox.SelectedItem);
				Assert.True(urlTextBox.IsFocused);
				Assert.Equal(0, urlTextBox.SelectionStart);
				Assert.Equal(repositoryUrl.Length, urlTextBox.SelectionEnd);

				urlTextBox.Text = "https://github.com/example/manually-edited";
				await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
				Assert.Null(recentComboBox.SelectedItem);
			}
			finally
			{
				await UiTestDriver.CloseTopLevelWindowAsync(cloneWindow);
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
				await UiTestDriver.CloseTopLevelWindowAsync(cloneWindow);
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
					await UiTestDriver.CloseTopLevelWindowAsync(cloneWindow);
				}
			}
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(secondWindow);
			await UiTestDriver.CloseWindowAsync(firstWindow);
		}
	}

	private static MenuItem FindRecentItem(MenuItem recentMenu, string path)
		=> Assert.Single(
			recentMenu.Items.OfType<MenuItem>(),
			item => item.Tag is string itemPath && PathComparer.Default.Equals(itemPath, path));

	private static async Task<MenuItem> OpenRecentMenuAsync(MainWindow window)
	{
		var fileMenu = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "FileMenuItem");
		var recentMenu = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "RecentMenuItem");
		fileMenu.IsSubMenuOpen = true;
		await UiTestDriver.WaitForSettledFramesAsync(frameCount: 3);
		recentMenu.IsSubMenuOpen = true;
		await UiTestDriver.WaitForConditionAsync(
			window,
			() => recentMenu.Items.OfType<MenuItem>().Any(static item => item.Tag is string),
			"recent project menu entries to materialize on first open");
		return recentMenu;
	}
}
