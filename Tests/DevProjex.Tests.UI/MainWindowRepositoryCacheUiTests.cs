using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DevProjex.Infrastructure.Git;
using DevProjex.Infrastructure.RecentProjects;
using DevProjex.Kernel.Abstractions;
using System.Globalization;
using System.Reflection;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowRepositoryCacheUiTests(UiWorkspaceFixture workspace)
{
	[AvaloniaFact]
	public async Task GitCloneWindow_LocalCache_ShowsNewestFirstDetailsAndLocalizedHeadings()
	{
		var appDataPath = CreateAppDataPath();
		var cache = new RepoCacheService(Path.Combine(appDataPath, "RepoCache"));
		CreateCachedRepository(cache, "https://github.com/example/older.git", "main", 64, git: false);
		await Task.Delay(20, TestContext.Current.CancellationToken);
		CreateCachedRepository(cache, "https://github.com/example/newer.git", "feature", 256, git: false);
		var expected = cache.ListIndexedRepositories();
		var window = await CreateWindowAsync(appDataPath, cache);

		try
		{
			var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			try
			{
				await WaitForCatalogAsync(window);
				var viewModel = UiTestDriver.GetViewModel(window);
				var container = Assert.IsType<StackPanel>(cloneWindow.FindControl<StackPanel>("LocalCacheContainer"));
				var comboBox = Assert.IsType<ComboBox>(cloneWindow.FindControl<ComboBox>("LocalCacheComboBox"));
				var items = comboBox.Items.OfType<RepositoryCacheEntryViewModel>().ToArray();
				Assert.True(container.IsVisible);
				Assert.Equal(viewModel.GitCloneLocalCacheLabel, comboBox.PlaceholderText);
				Assert.Equal(expected.Select(static entry => entry.LocalPath), items.Select(static item => item.LocalPath), PathComparer.Default);
				Assert.Equal("newer", items[0].Entry.RepositoryName);
				Assert.Equal("example / newer (ZIP)", items[0].DisplayName);
				Assert.Equal("feature", items[0].DetailsText);
				Assert.Contains(
					expected[0].LastOpenedUtc.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo("en-US")),
					items[0].ToolTipText,
					StringComparison.Ordinal);
				Assert.Contains(
					RepositoryCacheEntryViewModel.FormatByteSize(
						expected[0].ApproximateSizeBytes,
						CultureInfo.GetCultureInfo("en-US")),
					items[0].ToolTipText,
					StringComparison.Ordinal);
				Assert.Contains(expected[0].RepositoryUrl, items[0].ToolTipText, StringComparison.Ordinal);
				var deleteButton = await OpenAndFindDeleteButtonAsync(window, comboBox, items[0]);
				var itemRow = Assert.Single(
					deleteButton.GetVisualAncestors().OfType<Grid>(),
					grid => ReferenceEquals(grid.DataContext, items[0]) && ToolTip.GetTip(grid) is not null);
				Assert.Equal(items[0].ToolTipText, ToolTip.GetTip(itemRow));
				var textStack = Assert.Single(itemRow.Children.OfType<StackPanel>());
				var textLines = textStack.Children.OfType<TextBlock>().ToArray();
				Assert.Equal(0, textStack.Spacing);
				Assert.Equal(2, textLines.Length);
				Assert.Equal(items[0].DisplayName, textLines[0].Text);
				Assert.Equal(items[0].DetailsText, textLines[1].Text);
				Assert.Equal(11, textLines[1].FontSize);
				Assert.Equal(0.6, textLines[1].Opacity);
				comboBox.IsDropDownOpen = false;
				Assert.Equal(
					viewModel.GitCloneRecentRepositoriesLabel,
					cloneWindow.FindControl<TextBlock>("RecentRepositoriesLabelText")?.Text);
				Assert.Equal(
					viewModel.GitCloneLocalCacheLabel,
					cloneWindow.FindControl<TextBlock>("LocalCacheLabelText")?.Text);

				viewModel.GitCloneCacheLoading = true;
				await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
				Assert.False(container.IsVisible);
				viewModel.GitCloneCacheLoading = false;
				await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
				Assert.True(container.IsVisible);
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
	public async Task GitCloneWindow_DeleteIconRemovesEntryWithoutOpeningRepository()
	{
		var appDataPath = CreateAppDataPath();
		var cache = new RepoCacheService(Path.Combine(appDataPath, "RepoCache"));
		CreateCachedRepository(cache, "https://github.com/example/remove.git", "main", 128, git: false);
		CreateCachedRepository(cache, "https://github.com/example/keep.git", "main", 64, git: false);
		var window = await CreateWindowAsync(appDataPath, cache);

		try
		{
			var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			try
			{
				await WaitForCatalogAsync(window, expectedCount: 2);
				var viewModel = UiTestDriver.GetViewModel(window);
				var originalSourceType = viewModel.ProjectSourceType;
				var removed = viewModel.CachedRepositories.Single(item => item.Entry.RepositoryName == "remove");
				var kept = viewModel.CachedRepositories.Single(item => item.Entry.RepositoryName == "keep");
				var comboBox = Assert.IsType<ComboBox>(cloneWindow.FindControl<ComboBox>("LocalCacheComboBox"));
				comboBox.SelectedItem = kept;
				await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
				var deleteButton = await OpenAndFindDeleteButtonAsync(window, comboBox, removed);

				await UiTestDriver.ClickAsync(window, deleteButton);
				await UiTestDriver.WaitForConditionAsync(
					window,
					() => viewModel.CachedRepositories.Count == 1,
					"cache catalog to refresh after deletion");

				Assert.False(Directory.Exists(removed.LocalPath));
				Assert.True(cloneWindow.IsVisible);
				Assert.Equal(originalSourceType, viewModel.ProjectSourceType);
				Assert.Equal(kept.LocalPath, viewModel.SelectedGitCloneCacheEntry?.LocalPath, PathComparer.Default);
				Assert.DoesNotContain(viewModel.CachedRepositories, item => PathComparer.Default.Equals(item.LocalPath, removed.LocalPath));
				Assert.Equal(cache.ListIndexedRepositories().Single().ApproximateSizeBytes,
					viewModel.CachedRepositories.Single().Entry.ApproximateSizeBytes);
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
	public async Task GitCloneWindow_NonEmptyCacheShowsDeleteIconAndActiveRepositoryTooltip()
	{
		var appDataPath = CreateAppDataPath();
		var cache = new RepoCacheService(Path.Combine(appDataPath, "RepoCache"));
		var activePath = CreateCachedRepository(cache, "https://github.com/example/active.git", "snapshot", 96, git: false);
		var window = await CreateWindowAsync(appDataPath, cache);

		try
		{
			var firstCloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			await WaitForCatalogAsync(window, expectedCount: 1);
			var active = UiTestDriver.GetViewModel(window).CachedRepositories.Single(item =>
				PathComparer.Default.Equals(item.LocalPath, activePath));
			var combo = Assert.IsType<ComboBox>(firstCloneWindow.FindControl<ComboBox>("LocalCacheComboBox"));
			combo.SelectedItem = active;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.True(firstCloneWindow.IsVisible);
			await UiTestDriver.ClickAsync(
				window,
				Assert.IsType<Button>(firstCloneWindow.FindControl<Button>("StartCloneButton")));
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !firstCloneWindow.IsVisible,
				"cached repository to open");
			await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => UiTestDriver.GetViewModel(window).CanChangeProjectTree,
				"cached project load to release project-changing operations");

			var secondCloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			try
			{
				await WaitForCatalogAsync(window, expectedCount: 1);
				var viewModel = UiTestDriver.GetViewModel(window);
				var container = Assert.IsType<StackPanel>(secondCloneWindow.FindControl<StackPanel>("LocalCacheContainer"));
				var activeItem = viewModel.CachedRepositories.Single(item =>
					cache.PathsBelongToSameRepository(item.LocalPath, activePath));
				Assert.True(container.IsVisible);
				Assert.False(activeItem.CanDelete);
				Assert.Equal(viewModel.GitCloneLocalCacheActiveDeleteToolTip, activeItem.DeleteToolTip);
				var activeCombo = Assert.IsType<ComboBox>(secondCloneWindow.FindControl<ComboBox>("LocalCacheComboBox"));
				var activeDeleteButton = await OpenAndFindDeleteButtonAsync(window, activeCombo, activeItem);
				Assert.Equal(activeItem.RemoveText, AutomationProperties.GetName(activeDeleteButton));
				var icon = Assert.IsType<Viewbox>(activeDeleteButton.Content);
				Assert.IsType<global::Avalonia.Controls.Shapes.Path>(icon.Child);
				Assert.False(activeDeleteButton.IsEnabled);
				var tooltipHost = Assert.Single(
					activeDeleteButton.GetVisualAncestors().OfType<Border>(),
					static border => border.Classes.Contains("cache-delete-tooltip-host"));
				Assert.Equal(viewModel.GitCloneLocalCacheActiveDeleteToolTip, ToolTip.GetTip(tooltipHost));
				await UiTestDriver.OpenToolTipThroughPointerAsync(window, tooltipHost);
				Assert.True(ToolTip.GetIsOpen(tooltipHost));
				Assert.True(Directory.Exists(activePath));
			}
			finally
			{
				await UiTestDriver.CloseTopLevelWindowAsync(secondCloneWindow);
			}
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task GitCloneWindow_EmptyCacheHidesTheEntireSectionWithoutArtifacts()
	{
		var appDataPath = CreateAppDataPath();
		var cache = new RepoCacheService(Path.Combine(appDataPath, "RepoCache"));
		var window = await CreateWindowAsync(appDataPath, cache);

		try
		{
			var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			try
			{
				await WaitForCatalogAsync(window);
				var container = Assert.IsType<StackPanel>(cloneWindow.FindControl<StackPanel>("LocalCacheContainer"));
				var combo = Assert.IsType<ComboBox>(cloneWindow.FindControl<ComboBox>("LocalCacheComboBox"));
				Assert.False(container.IsVisible);
				Assert.Equal(420, cloneWindow.Width);
				Assert.Equal(UiTestDriver.GetViewModel(window).GitCloneLocalCacheLabel, combo.PlaceholderText);
				Assert.False(combo.IsEnabled);
				Assert.Null(cloneWindow.FindControl<TextBlock>("LocalCacheUsageText"));
				Assert.Null(cloneWindow.FindControl<Button>("ClearLocalCacheButton"));
				Assert.False(UiTestDriver.GetViewModel(window).CanStartGitClone);

				await UiTestDriver.PressKeyAsync(cloneWindow, Key.Enter);

				Assert.True(cloneWindow.IsVisible);
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
	public async Task GitCloneWindow_DeletingLastEntryHidesTheEntireSection()
	{
		var appDataPath = CreateAppDataPath();
		var cache = new RepoCacheService(Path.Combine(appDataPath, "RepoCache"));
		CreateCachedRepository(cache, "https://github.com/example/last.git", "main", 64, git: false);
		var window = await CreateWindowAsync(appDataPath, cache);

		try
		{
			var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			try
			{
				await WaitForCatalogAsync(window, expectedCount: 1);
				var viewModel = UiTestDriver.GetViewModel(window);
				var container = Assert.IsType<StackPanel>(cloneWindow.FindControl<StackPanel>("LocalCacheContainer"));
				var comboBox = Assert.IsType<ComboBox>(cloneWindow.FindControl<ComboBox>("LocalCacheComboBox"));
				var entry = Assert.Single(viewModel.CachedRepositories);
				comboBox.SelectedItem = entry;
				await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
				Assert.Same(entry, viewModel.SelectedGitCloneCacheEntry);
				var deleteButton = await OpenAndFindDeleteButtonAsync(window, comboBox, entry);

				await UiTestDriver.ClickAsync(window, deleteButton);
				await UiTestDriver.WaitForConditionAsync(
					window,
					() => viewModel.CachedRepositories.Count == 0 && !container.IsVisible,
					"last cache deletion to hide the local-cache section");

				Assert.True(cloneWindow.IsVisible);
				Assert.Null(comboBox.SelectedItem);
				Assert.Null(viewModel.SelectedGitCloneCacheEntry);
				Assert.Empty(cache.ListIndexedRepositories());
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
	public async Task GitCloneWindow_CacheSelectionWaitsForConfirmationAndUsesIndexedBranch()
	{
		var appDataPath = CreateAppDataPath();
		var cache = new RepoCacheService(Path.Combine(appDataPath, "RepoCache"));
		var repositoryPath = CreateCachedRepository(
			cache,
			"https://github.com/example/offline.git",
			"feature/offline",
			128,
			git: true);
		var git = new FailingNetworkGitRepositoryService();
		var window = await CreateWindowAsync(appDataPath, cache, git);

		try
		{
			var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			await WaitForCatalogAsync(window, expectedCount: 1);
			var viewModel = UiTestDriver.GetViewModel(window);
			viewModel.GitCloneUrl = "https://github.com/example/network-intent.git";
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			var combo = Assert.IsType<ComboBox>(cloneWindow.FindControl<ComboBox>("LocalCacheComboBox"));
			combo.SelectedItem = viewModel.CachedRepositories.Single();
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.True(cloneWindow.IsVisible);
			Assert.Equal(
				"https://github.com/example/network-intent.git",
				Assert.IsType<TextBox>(cloneWindow.FindControl<TextBox>("UrlTextBox")).Text);
			Assert.Equal(0, git.OperationCount);
			await UiTestDriver.ClickAsync(
				window,
				Assert.IsType<Button>(cloneWindow.FindControl<Button>("StartCloneButton")));
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !cloneWindow.IsVisible,
				"offline Git cache to open");
			await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);

			Assert.Equal(ProjectSourceType.GitClone, viewModel.ProjectSourceType);
			Assert.Equal("feature/offline", viewModel.CurrentBranch);
			Assert.Equal(0, git.OperationCount);
			Assert.True(Directory.Exists(repositoryPath));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task GitCloneWindow_EnterWithCacheSelectionOpensLocalEntryWithoutGitCalls()
	{
		var appDataPath = CreateAppDataPath();
		var cache = new RepoCacheService(Path.Combine(appDataPath, "RepoCache"));
		CreateCachedRepository(
			cache,
			"https://github.com/example/enter-cache.git",
			"feature/enter",
			64,
			git: true);
		var git = new FailingNetworkGitRepositoryService();
		var window = await CreateWindowAsync(appDataPath, cache, git);

		try
		{
			var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			await WaitForCatalogAsync(window, expectedCount: 1);
			var combo = Assert.IsType<ComboBox>(cloneWindow.FindControl<ComboBox>("LocalCacheComboBox"));
			combo.SelectedItem = UiTestDriver.GetViewModel(window).CachedRepositories.Single();
			combo.Focus();

			await UiTestDriver.PressKeyAsync(cloneWindow, Key.Enter);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !cloneWindow.IsVisible,
				"Enter to confirm the selected cache entry");
			await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);

			var viewModel = UiTestDriver.GetViewModel(window);
			Assert.Equal(ProjectSourceType.GitClone, viewModel.ProjectSourceType);
			Assert.Equal("feature/enter", viewModel.CurrentBranch);
			Assert.Equal(0, git.OperationCount);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task GitCloneWindow_ArrowNavigationDoesNotOpenCachedRepository()
	{
		var appDataPath = CreateAppDataPath();
		var cache = new RepoCacheService(Path.Combine(appDataPath, "RepoCache"));
		CreateCachedRepository(cache, "https://github.com/example/arrow-a.git", "main", 64, git: true);
		CreateCachedRepository(cache, "https://github.com/example/arrow-b.git", "main", 64, git: true);
		var git = new FailingNetworkGitRepositoryService();
		var window = await CreateWindowAsync(appDataPath, cache, git);

		try
		{
			var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			try
			{
				await WaitForCatalogAsync(window, expectedCount: 2);
				var combo = Assert.IsType<ComboBox>(cloneWindow.FindControl<ComboBox>("LocalCacheComboBox"));
				var originalSourceType = UiTestDriver.GetViewModel(window).ProjectSourceType;
				combo.Focus();
				combo.IsDropDownOpen = true;
				await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);

				await UiTestDriver.PressKeyAsync(cloneWindow, Key.Down);
				await UiTestDriver.PressKeyAsync(cloneWindow, Key.Up);

				Assert.True(cloneWindow.IsVisible);
				Assert.Equal(originalSourceType, UiTestDriver.GetViewModel(window).ProjectSourceType);
				Assert.Equal(0, git.OperationCount);
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
	public async Task GitCloneWindow_UrlAndRecentIntentClearCacheSelectionAndEnterUsesNetworkPath()
	{
		var appDataPath = CreateAppDataPath();
		var cache = new RepoCacheService(Path.Combine(appDataPath, "RepoCache"));
		const string repositoryUrl = "https://github.com/example/recent-intent.git";
		CreateCachedRepository(cache, repositoryUrl, "feature", 64, git: true);
		var recentStore = new RecentProjectsStore(() => appDataPath);
		recentStore.AddRepository(recentStore.Load(), repositoryUrl);
		var git = new OfflineUpdateGitRepositoryService();
		var window = await CreateWindowAsync(appDataPath, cache, git);

		try
		{
			var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			await WaitForCatalogAsync(window, expectedCount: 1);
			var viewModel = UiTestDriver.GetViewModel(window);
			var cacheCombo = Assert.IsType<ComboBox>(cloneWindow.FindControl<ComboBox>("LocalCacheComboBox"));
			var recentCombo = Assert.IsType<ComboBox>(cloneWindow.FindControl<ComboBox>("RecentRepositoriesComboBox"));
			var urlTextBox = Assert.IsType<TextBox>(cloneWindow.FindControl<TextBox>("UrlTextBox"));
			var cacheEntry = Assert.Single(viewModel.CachedRepositories);
			cacheCombo.SelectedItem = cacheEntry;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.Same(cacheEntry, viewModel.SelectedGitCloneCacheEntry);

			await UiTestDriver.ClickAsync(window, urlTextBox);
			urlTextBox.SelectAll();
			cloneWindow.KeyTextInput("https://github.com/example/manual-intent.git");
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
			Assert.Null(cacheCombo.SelectedItem);
			Assert.Null(viewModel.SelectedGitCloneCacheEntry);

			cacheCombo.SelectedItem = cacheEntry;
			recentCombo.SelectedItem = recentCombo.Items
				.OfType<RecentProjectEntryViewModel>()
				.Single(item => string.Equals(item.Value, repositoryUrl, StringComparison.OrdinalIgnoreCase));
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
			Assert.Equal(repositoryUrl, urlTextBox.Text);
			Assert.Null(cacheCombo.SelectedItem);
			Assert.Null(viewModel.SelectedGitCloneCacheEntry);
			Assert.True(cloneWindow.IsVisible);
			Assert.Equal(0, git.PullCount);

			await UiTestDriver.PressKeyAsync(cloneWindow, Key.Enter);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !cloneWindow.IsVisible,
				"Enter to confirm the recent URL through the network path");
			await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
			Assert.Equal(1, git.PullCount);
			Assert.Equal(0, git.CloneCount);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task OpeningLocalFolder_ReleasesCacheLeaseWithoutDeletingRepository()
	{
		var appDataPath = CreateAppDataPath();
		var cache = new RepoCacheService(Path.Combine(appDataPath, "RepoCache"));
		var repositoryPath = CreateCachedRepository(
			cache,
			"https://github.com/example/retained.git",
			"snapshot",
			128,
			git: false);
		var window = await CreateWindowAsync(appDataPath, cache);

		try
		{
			var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			await WaitForCatalogAsync(window, expectedCount: 1);
			var combo = Assert.IsType<ComboBox>(cloneWindow.FindControl<ComboBox>("LocalCacheComboBox"));
			combo.SelectedItem = UiTestDriver.GetViewModel(window).CachedRepositories.Single();
			await UiTestDriver.ClickAsync(
				window,
				Assert.IsType<Button>(cloneWindow.FindControl<Button>("StartCloneButton")));
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !cloneWindow.IsVisible,
				"cached repository to open before local-folder switch");
			await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);

			await UiTestDriver.OpenFolderAsync(window, workspace.Project.RootPath);

			Assert.True(Directory.Exists(repositoryPath));
			Assert.Single(cache.ListIndexedRepositories());
			var reopened = await cache.TryAcquireRepositorySessionByPathAsync(
				repositoryPath,
				TestContext.Current.CancellationToken);
			Assert.NotNull(reopened);
			reopened.Dispose();
			cache.DeleteRepositoryDirectory(repositoryPath);
			Assert.Empty(cache.ListIndexedRepositories());
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task GitCloneWindow_MissingDirectoryAtSelection_IsRemovedAndReported()
	{
		var appDataPath = CreateAppDataPath();
		var cache = new RepoCacheService(Path.Combine(appDataPath, "RepoCache"));
		CreateCachedRepository(
			cache,
			"https://github.com/example/disappeared.git",
			"snapshot",
			64,
			git: false);
		var window = await CreateWindowAsync(appDataPath, cache);

		try
		{
			var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			try
			{
				await WaitForCatalogAsync(window, expectedCount: 1);
				var viewModel = UiTestDriver.GetViewModel(window);
				var entry = viewModel.CachedRepositories.Single();
				Directory.Delete(RepositoryCacheLayout.GetContainer(entry.LocalPath), recursive: true);
				var combo = Assert.IsType<ComboBox>(cloneWindow.FindControl<ComboBox>("LocalCacheComboBox"));
				combo.SelectedItem = entry;
				await UiTestDriver.ClickAsync(
					window,
					Assert.IsType<Button>(cloneWindow.FindControl<Button>("StartCloneButton")));

				await UiTestDriver.WaitForConditionAsync(
					window,
					() => viewModel.CachedRepositories.Count == 0,
					"missing cache entry to be removed from the catalog");
				await UiTestDriver.WaitForConditionAsync(
					window,
					() => UiTestDriver.GetToastService(window).Items.Any(toast =>
						toast.Message.Contains("no longer available", StringComparison.Ordinal)),
					"missing cache toast to be shown");
				Assert.True(cloneWindow.IsVisible);
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
	public async Task CachedUrl_WhenFetchFails_OpensLocalCopyAndShowsFallbackToast()
	{
		var appDataPath = CreateAppDataPath();
		var cache = new RepoCacheService(Path.Combine(appDataPath, "RepoCache"));
		const string repositoryUrl = "https://github.com/example/network-fallback.git";
		CreateCachedRepository(cache, repositoryUrl, "feature", 128, git: true);
		var git = new OfflineUpdateGitRepositoryService();
		var window = await CreateWindowAsync(appDataPath, cache, git);

		try
		{
			var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			await WaitForCatalogAsync(window, expectedCount: 1);
			var viewModel = UiTestDriver.GetViewModel(window);
			viewModel.GitCloneUrl = repositoryUrl;
			var start = Assert.IsType<Button>(cloneWindow.FindControl<Button>("StartCloneButton"));
			await UiTestDriver.RaiseButtonClickAsync(start);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !cloneWindow.IsVisible,
				"cached URL fallback to open the local copy");
			await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => UiTestDriver.GetToastService(window).Items.Any(toast =>
					toast.Message.Contains("local copy was opened", StringComparison.Ordinal)),
				"cached update failure toast to be shown");

			Assert.Equal(1, git.PullCount);
			Assert.Equal(0, git.CloneCount);
			Assert.Equal(0, git.BranchDiscoveryCount);
			Assert.Equal(ProjectSourceType.GitClone, viewModel.ProjectSourceType);
			Assert.Equal("main", viewModel.CurrentBranch);
			Assert.DoesNotContain(
				UiTestDriver.GetToastService(window).Items,
				toast => toast.Message.Contains("cloned successfully", StringComparison.Ordinal));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task CachedZipUrl_OpensWithoutGitOrFalseUpdateFailureToast()
	{
		var appDataPath = CreateAppDataPath();
		var cache = new RepoCacheService(Path.Combine(appDataPath, "RepoCache"));
		const string repositoryUrl = "https://github.com/example/archive.git";
		CreateCachedRepository(cache, repositoryUrl, "archive", 128, git: false);
		var git = new FailingNetworkGitRepositoryService();
		var window = await CreateWindowAsync(appDataPath, cache, git);

		try
		{
			var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			await WaitForCatalogAsync(window, expectedCount: 1);
			var viewModel = UiTestDriver.GetViewModel(window);
			viewModel.GitCloneUrl = repositoryUrl;
			await UiTestDriver.RaiseButtonClickAsync(
				Assert.IsType<Button>(cloneWindow.FindControl<Button>("StartCloneButton")));
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !cloneWindow.IsVisible,
				"cached ZIP URL to open");
			await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);

			Assert.Equal(0, git.OperationCount);
			Assert.Equal(ProjectSourceType.ZipDownload, viewModel.ProjectSourceType);
			Assert.DoesNotContain(
				UiTestDriver.GetToastService(window).Items,
				toast => toast.Message.Contains("local copy was opened", StringComparison.Ordinal));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task CachedUrl_CancelDuringFetch_ReleasesSessionLease()
	{
		var appDataPath = CreateAppDataPath();
		var cache = new RepoCacheService(Path.Combine(appDataPath, "RepoCache"));
		const string repositoryUrl = "https://github.com/example/cancel-fetch.git";
		var repositoryPath = CreateCachedRepository(cache, repositoryUrl, "feature", 64, git: true);
		var git = new CancelableUpdateGitRepositoryService();
		var window = await CreateWindowAsync(appDataPath, cache, git);

		try
		{
			var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			try
			{
				await WaitForCatalogAsync(window, expectedCount: 1);
				UiTestDriver.GetViewModel(window).GitCloneUrl = repositoryUrl;
				await UiTestDriver.RaiseButtonClickAsync(
					Assert.IsType<Button>(cloneWindow.FindControl<Button>("StartCloneButton")));
				await git.PullStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

				await UiTestDriver.RaiseButtonClickAsync(
					Assert.IsType<Button>(cloneWindow.FindControl<Button>("CancelCloneButton")));
				await git.PullExited.Task.WaitAsync(TimeSpan.FromSeconds(10));
				await UiTestDriver.WaitForConditionAsync(
					window,
					() => GetGitCloneActionState(window) == 0,
					"canceled cached fetch to release its backend gate and lease");

				cache.DeleteRepositoryDirectory(repositoryPath);
				Assert.Empty(cache.ListIndexedRepositories());
				Assert.False(Directory.Exists(repositoryPath));
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
	public async Task GitCloneWindow_RepeatedOpenDuringRecentLoadCreatesSingleDialog()
	{
		var appDataPath = CreateAppDataPath();
		var cache = new RepoCacheService(Path.Combine(appDataPath, "RepoCache"));
		var window = await CreateWindowAsync(appDataPath, cache);

		try
		{
			var loadCompletion = new TaskCompletionSource<RecentProjectsDb>(
				TaskCreationOptions.RunContinuationsAsynchronously);
			var loadedField = GetRequiredMainWindowField("_recentProjectsLoaded");
			var loadTaskField = GetRequiredMainWindowField("_recentProjectsLoadTask");
			var databaseField = GetRequiredMainWindowField("_recentProjectsDb");
			var database = Assert.IsType<RecentProjectsDb>(databaseField.GetValue(window));
			loadedField.SetValue(window, false);
			loadTaskField.SetValue(window, loadCompletion.Task);
			var method = typeof(MainWindow).GetMethod(
				"OnGitClone",
				BindingFlags.Instance | BindingFlags.NonPublic);
			Assert.NotNull(method);

			await window.Dispatcher.InvokeAsync(() =>
			{
				method!.Invoke(window, [window, new RoutedEventArgs()]);
				method.Invoke(window, [window, new RoutedEventArgs()]);
			});
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
			Assert.Empty(window.OwnedWindows.OfType<GitCloneWindow>());

			loadCompletion.SetResult(database);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.OwnedWindows.OfType<GitCloneWindow>().Count() == 1,
				"concurrent Git clone commands to publish one dialog");
			var cloneWindow = Assert.Single(window.OwnedWindows.OfType<GitCloneWindow>());
			try
			{
				Assert.Same(cloneWindow, GetGitCloneWindow(window));
				Assert.True(cloneWindow.IsVisible);
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

	private static FieldInfo GetRequiredMainWindowField(string name)
	{
		var field = typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		return field;
	}

	private string CreateAppDataPath() =>
		Path.Combine(workspace.Project.AppDataPath, Guid.NewGuid().ToString("N"));

	private async Task<MainWindow> CreateWindowAsync(
		string appDataPath,
		RepoCacheService cache,
		IGitRepositoryService? git = null) =>
		await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			appDataPathOverride: appDataPath,
			configureServices: services => services with
			{
				RepoCacheService = cache,
				GitRepositoryService = git ?? services.GitRepositoryService
			});

	private static string CreateCachedRepository(
		RepoCacheService cache,
		string repositoryUrl,
		string branch,
		int payloadSize,
		bool git)
	{
		var staging = cache.CreateRepositoryStagingDirectory(repositoryUrl);
		if (git)
			Directory.CreateDirectory(Path.Combine(staging, ".git"));
		File.WriteAllText(Path.Combine(staging, "payload.txt"), new string('x', payloadSize));
		var published = cache.PublishRepositoryDirectory(staging, repositoryUrl);
		cache.RecordIndexedRepository(repositoryUrl, published, branch);
		return published;
	}

	private static async Task WaitForCatalogAsync(MainWindow window, int? expectedCount = null)
	{
		await UiTestDriver.WaitForConditionAsync(
			window,
			() =>
			{
				var viewModel = UiTestDriver.GetViewModel(window);
				return !viewModel.GitCloneCacheLoading &&
				       (!expectedCount.HasValue || viewModel.CachedRepositories.Count == expectedCount.Value);
			},
			"local cache catalog to load");
	}

	private static async Task<Button> OpenAndFindDeleteButtonAsync(
		MainWindow window,
		ComboBox comboBox,
		RepositoryCacheEntryViewModel entry)
	{
		comboBox.IsDropDownOpen = true;
		Button? button = null;
		await UiTestDriver.WaitForConditionAsync(
			window,
			() =>
			{
				var popup = comboBox
					.GetVisualDescendants()
					.OfType<Popup>()
					.FirstOrDefault(static candidate => string.Equals(candidate.Name, "PART_Popup", StringComparison.Ordinal));
				button = popup?.Child?
					.GetVisualDescendants()
					.OfType<Button>()
					.FirstOrDefault(candidate => ReferenceEquals(candidate.Tag, entry));
				return button is not null;
			},
			"cache entry delete button to be realized");
		return button!;
	}

	private static int GetGitCloneActionState(MainWindow window)
	{
		var field = typeof(MainWindow).GetField(
			"_gitCloneActionInProgress",
			BindingFlags.Instance | BindingFlags.NonPublic);
		return Assert.IsType<int>(field?.GetValue(window));
	}

	private static GitCloneWindow? GetGitCloneWindow(MainWindow window)
	{
		var field = typeof(MainWindow).GetField(
			"_gitCloneWindow",
			BindingFlags.Instance | BindingFlags.NonPublic);
		return field?.GetValue(window) as GitCloneWindow;
	}

	private sealed class FailingNetworkGitRepositoryService : IGitRepositoryService
	{
		public int OperationCount { get; private set; }

		private T Fail<T>()
		{
			OperationCount++;
			throw new InvalidOperationException("Git service must not be used for a local-cache open.");
		}

		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) => Fail<Task<bool>>();
		public Task<GitCloneResult> CloneAsync(string url, string targetDirectory, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Fail<Task<GitCloneResult>>();
		public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repositoryPath, CancellationToken cancellationToken = default) => Fail<Task<IReadOnlyList<GitBranch>>>();
		public Task<string?> GetDefaultBranchAsync(string repositoryPath, CancellationToken cancellationToken = default) => Fail<Task<string?>>();
		public Task<bool> SwitchBranchAsync(string repositoryPath, string branchName, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Fail<Task<bool>>();
		public Task<bool> PullUpdatesAsync(string repositoryPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Fail<Task<bool>>();
		public Task<string?> GetHeadCommitAsync(string repositoryPath, CancellationToken cancellationToken = default) => Fail<Task<string?>>();
		public Task<string?> GetCurrentBranchAsync(string repositoryPath, CancellationToken cancellationToken = default) => Fail<Task<string?>>();
		public Task<string?> GetRemoteUrlAsync(string repositoryPath, CancellationToken cancellationToken = default) => Fail<Task<string?>>();
	}

	private sealed class OfflineUpdateGitRepositoryService : IGitRepositoryService
	{
		private string _branch = "feature";

		public int PullCount { get; private set; }
		public int CloneCount { get; private set; }
		public int BranchDiscoveryCount { get; private set; }

		public Task<string?> GetDefaultBranchAsync(string repositoryPath, CancellationToken cancellationToken = default) => Task.FromResult<string?>("main");
		public Task<bool> SwitchBranchAsync(string repositoryPath, string branchName, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
		{
			_branch = branchName;
			return Task.FromResult(true);
		}
		public Task<bool> PullUpdatesAsync(string repositoryPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
		{
			PullCount++;
			return Task.FromResult(false);
		}
		public Task<string?> GetCurrentBranchAsync(string repositoryPath, CancellationToken cancellationToken = default) => Task.FromResult<string?>(_branch);
		public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repositoryPath, CancellationToken cancellationToken = default)
		{
			BranchDiscoveryCount++;
			return Task.FromResult<IReadOnlyList<GitBranch>>([new GitBranch(_branch, IsActive: true, IsRemote: false)]);
		}
		public Task<string?> GetHeadCommitAsync(string repositoryPath, CancellationToken cancellationToken = default) => Task.FromResult<string?>("cached-head");
		public Task<string?> GetRemoteUrlAsync(string repositoryPath, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
		public Task<GitCloneResult> CloneAsync(string url, string targetDirectory, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
		{
			CloneCount++;
			throw new InvalidOperationException("The cached URL must not be cloned again.");
		}
	}

	private sealed class CancelableUpdateGitRepositoryService : IGitRepositoryService
	{
		private string _branch = "feature";

		public TaskCompletionSource PullStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource PullExited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<string?> GetDefaultBranchAsync(string repositoryPath, CancellationToken cancellationToken = default) => Task.FromResult<string?>("main");
		public Task<bool> SwitchBranchAsync(string repositoryPath, string branchName, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
		{
			_branch = branchName;
			return Task.FromResult(true);
		}
		public async Task<bool> PullUpdatesAsync(string repositoryPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
		{
			PullStarted.TrySetResult();
			try
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				return true;
			}
			finally
			{
				PullExited.TrySetResult();
			}
		}
		public Task<string?> GetCurrentBranchAsync(string repositoryPath, CancellationToken cancellationToken = default) => Task.FromResult<string?>(_branch);
		public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repositoryPath, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GitBranch>>([]);
		public Task<string?> GetHeadCommitAsync(string repositoryPath, CancellationToken cancellationToken = default) => Task.FromResult<string?>("head");
		public Task<string?> GetRemoteUrlAsync(string repositoryPath, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
		public Task<GitCloneResult> CloneAsync(string url, string targetDirectory, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}
}
