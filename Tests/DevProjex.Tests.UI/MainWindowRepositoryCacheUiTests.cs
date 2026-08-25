using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using DevProjex.Infrastructure.Git;
using DevProjex.Infrastructure.RecentProjects;
using DevProjex.Kernel.Abstractions;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowRepositoryCacheUiTests(UiWorkspaceFixture workspace)
{
	[AvaloniaFact]
	public async Task GitCloneWindow_LargeLocalCacheUsesOwnerBoundedScrollablePopup()
	{
		var appDataPath = CreateAppDataPath();
		var cache = new RepoCacheService(Path.Combine(appDataPath, "RepoCache"));
		const int repositoryCount = 18;
		for (var index = 0; index < repositoryCount; index++)
		{
			CreateCachedRepository(
				cache,
				$"https://github.com/example/cache-{index:D2}.git",
				"main",
				64,
				git: false);
		}

		var window = await CreateWindowAsync(appDataPath, cache);
		try
		{
			window.Height = window.MinHeight;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
			var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			try
			{
				await WaitForCatalogAsync(window, repositoryCount);
				var comboBox = Assert.IsType<ComboBox>(cloneWindow.FindControl<ComboBox>("LocalCacheComboBox"));
				comboBox.IsDropDownOpen = true;

				ScrollViewer? scrollViewer = null;
				await UiTestDriver.WaitForConditionAsync(
					window,
					() =>
					{
						var popup = comboBox
							.GetVisualDescendants()
							.OfType<Popup>()
							.FirstOrDefault(static candidate => candidate.IsOpen);
						scrollViewer = popup?.Child?
							.GetVisualDescendants()
							.OfType<ScrollViewer>()
							.FirstOrDefault();
						return scrollViewer is { Viewport.Height: > 0 } &&
						       scrollViewer.Extent.Height > scrollViewer.Viewport.Height;
					},
					"large local-cache popup to expose a scrollable viewport");

				var realizedScrollViewer = Assert.IsType<ScrollViewer>(scrollViewer);
				var verticalScrollBar = Assert.Single(
					realizedScrollViewer.GetVisualDescendants().OfType<ScrollBar>(),
					static scrollBar => scrollBar.Orientation == Orientation.Vertical);
				var thumb = Assert.Single(verticalScrollBar.GetVisualDescendants().OfType<Thumb>());
				var popup = Assert.Single(
					comboBox.GetVisualDescendants().OfType<Popup>(),
					static candidate => candidate.IsOpen);
				var popupBorder = Assert.IsType<Border>(popup.Child);
				var scrollBarOrigin = Assert.IsType<Point>(verticalScrollBar.TranslatePoint(default, popupBorder));
				var popupInnerRight = popupBorder.Bounds.Width - popupBorder.BorderThickness.Right;
				var popupInnerBottom = popupBorder.Bounds.Height - popupBorder.BorderThickness.Bottom;

				Assert.InRange(
					comboBox.MaxDropDownHeight,
					GitCloneWindow.MinimumRepositoryDropDownHeight,
					GitCloneWindow.MaximumRepositoryDropDownHeight - 1);
				Assert.True(realizedScrollViewer.Bounds.Height <= comboBox.MaxDropDownHeight + 1);
				Assert.False(realizedScrollViewer.AllowAutoHide);
				Assert.Equal(ScrollBarVisibility.Disabled, realizedScrollViewer.HorizontalScrollBarVisibility);
				Assert.Equal(ScrollBarVisibility.Auto, realizedScrollViewer.VerticalScrollBarVisibility);
				Assert.True(verticalScrollBar.IsVisible);
				Assert.Equal(10, verticalScrollBar.Width);
				Assert.Equal(0, verticalScrollBar.Margin.Top);
				Assert.Equal(0, verticalScrollBar.Margin.Bottom);
				Assert.Equal(VerticalAlignment.Stretch, verticalScrollBar.VerticalAlignment);
				Assert.InRange(
					Math.Abs(scrollBarOrigin.Y - popupBorder.BorderThickness.Top),
					0,
					0.5);
				Assert.InRange(
					Math.Abs(scrollBarOrigin.X + verticalScrollBar.Bounds.Width - popupInnerRight),
					0,
					0.5);
				Assert.InRange(
					Math.Abs(scrollBarOrigin.Y + verticalScrollBar.Bounds.Height - popupInnerBottom),
					0,
					0.5);
				Assert.Equal(5, thumb.Width);
				Assert.True(thumb.MinHeight >= 28);
				Assert.Equal(new CornerRadius(3), thumb.CornerRadius);
				Assert.Equal(0.72, thumb.Opacity);

				var maximumOffset = realizedScrollViewer.Extent.Height - realizedScrollViewer.Viewport.Height;
				realizedScrollViewer.Offset = new Vector(0, maximumOffset);
				await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
				Assert.Equal(maximumOffset, realizedScrollViewer.Offset.Y, precision: 3);
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
				cloneWindow.Resources["MenuPopupBrush"] = new SolidColorBrush(Color.FromArgb(32, 1, 2, 3));
				viewModel.SetThemeEffects(transparent: true, mica: false, acrylic: false);
				var deleteButton = await OpenAndFindDeleteButtonAsync(window, comboBox, items[0]);
				var popup = comboBox
					.GetVisualDescendants()
					.OfType<Popup>()
					.First(static candidate => string.Equals(candidate.Name, "PART_Popup", StringComparison.Ordinal));
				var popupSurface = Assert.IsType<Border>(popup.Child);
				var popupBackground = Assert.IsType<SolidColorBrush>(popupSurface.Background);
				Assert.Equal((byte)32, popupBackground.Color.A);
				var itemRow = Assert.Single(
					deleteButton.GetVisualAncestors().OfType<Grid>(),
					grid => ReferenceEquals(grid.DataContext, items[0]) && ToolTip.GetTip(grid) is not null);
				var itemToolTip = Assert.IsType<ToolTip>(ToolTip.GetTip(itemRow));
				var hostTransparencyBeforeToolTip = cloneWindow.TransparencyLevelHint.ToArray();
				var hostBackgroundBeforeToolTip = cloneWindow.Background;
				ToolTip.SetIsOpen(itemRow, true);
				await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
				Assert.True(ToolTip.GetIsOpen(itemRow));
				Assert.Equal(items[0].ToolTipText, itemToolTip.Content);
				var toolTipBackground = Assert.IsType<SolidColorBrush>(itemToolTip.Background);
				Assert.Equal((byte)32, toolTipBackground.Color.A);
				Assert.Equal(1, itemToolTip.Opacity);
				var toolTipLevel = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(itemToolTip));
				if (toolTipLevel is PopupRoot)
				{
					Assert.Same(cloneWindow, ThemedToolTipService.ResolveHostTopLevel(toolTipLevel));
					Assert.Equal(
						[
							WindowTransparencyLevel.AcrylicBlur,
							WindowTransparencyLevel.Blur,
							WindowTransparencyLevel.Transparent,
							WindowTransparencyLevel.None
						],
						toolTipLevel.TransparencyLevelHint);
					Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(toolTipLevel.Background).Color);
				}
				else
				{
					Assert.Same(cloneWindow, toolTipLevel);
					Assert.Equal(hostTransparencyBeforeToolTip, cloneWindow.TransparencyLevelHint);
					Assert.Same(hostBackgroundBeforeToolTip, cloneWindow.Background);
				}
				var textStack = Assert.Single(itemRow.Children.OfType<StackPanel>());
				var textLines = textStack.Children.OfType<TextBlock>().ToArray();
				Assert.Equal(0, textStack.Spacing);
				Assert.Equal(2, textLines.Length);
				Assert.Equal(items[0].DisplayName, textLines[0].Text);
				Assert.Equal(items[0].DetailsText, textLines[1].Text);
				Assert.Equal(11, textLines[1].FontSize);
				Assert.Equal(0.6, textLines[1].Opacity);
				comboBox.IsDropDownOpen = false;
				comboBox.SelectedItem = items[0];
				await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
				var selectedContent = Assert.Single(
					comboBox.GetVisualDescendants().OfType<StackPanel>(),
					panel => ReferenceEquals(panel.Tag, items[0]));
				Assert.DoesNotContain(selectedContent.GetVisualDescendants(), static visual => visual is Button);
				Assert.Equal(
					items[0].DisplayName,
					selectedContent.Children.OfType<TextBlock>().First().Text);
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
			await UiTestDriver.RaiseButtonClickAsync(
				Assert.IsType<Button>(firstCloneWindow.FindControl<Button>("StartCloneButton")));
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !firstCloneWindow.IsVisible,
				"cached repository to open");
			await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() =>
				{
					var viewModel = UiTestDriver.GetViewModel(window);
					return viewModel.CanChangeProjectTree && !viewModel.GitCloneInProgress;
				},
				"cached project load to release project-changing and Git operations");

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
				var iconViewport = Assert.IsType<Canvas>(icon.Child);
				Assert.Single(iconViewport.Children.OfType<global::Avalonia.Controls.Shapes.Path>());
				Assert.Equal(HorizontalAlignment.Center, activeDeleteButton.HorizontalContentAlignment);
				Assert.Equal(VerticalAlignment.Center, activeDeleteButton.VerticalContentAlignment);
				Assert.Equal(HorizontalAlignment.Center, icon.HorizontalAlignment);
				Assert.Equal(VerticalAlignment.Center, icon.VerticalAlignment);
				var iconOrigin = Assert.IsType<Point>(icon.TranslatePoint(default, activeDeleteButton));
				Assert.InRange(Math.Abs(iconOrigin.X + (icon.Bounds.Width / 2) - (activeDeleteButton.Bounds.Width / 2)), 0, 0.01);
				Assert.InRange(Math.Abs(iconOrigin.Y + (icon.Bounds.Height / 2) - (activeDeleteButton.Bounds.Height / 2)), 0, 0.01);
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
		const string recentRepositoryUrl = "https://github.com/example/network-intent.git";
		var repositoryPath = CreateCachedRepository(
			cache,
			"https://github.com/example/offline.git",
			"feature/offline",
			128,
			git: true,
			initializeGit: true);
		var recentStore = new RecentProjectsStore(() => appDataPath);
		recentStore.AddRepository(recentStore.Load(), recentRepositoryUrl);
		var git = new FailingNetworkGitRepositoryService();
		var window = await CreateWindowAsync(appDataPath, cache, git);

		try
		{
			var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			await WaitForCatalogAsync(window, expectedCount: 1);
			var viewModel = UiTestDriver.GetViewModel(window);
			var recentCombo = Assert.IsType<ComboBox>(cloneWindow.FindControl<ComboBox>("RecentRepositoriesComboBox"));
			var cacheCombo = Assert.IsType<ComboBox>(cloneWindow.FindControl<ComboBox>("LocalCacheComboBox"));
			var urlTextBox = Assert.IsType<TextBox>(cloneWindow.FindControl<TextBox>("UrlTextBox"));
			var recentEntry = Assert.Single(recentCombo.Items.OfType<RecentProjectEntryViewModel>());
			recentCombo.SelectedItem = recentEntry;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.Same(recentEntry, recentCombo.SelectedItem);
			Assert.Equal(recentRepositoryUrl, urlTextBox.Text);

			var cacheEntry = viewModel.CachedRepositories.Single();
			cacheCombo.SelectedItem = cacheEntry;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.True(cloneWindow.IsVisible);
			Assert.Same(cacheEntry, cacheCombo.SelectedItem);
			Assert.Same(cacheEntry, viewModel.SelectedGitCloneCacheEntry);
			Assert.Null(recentCombo.SelectedItem);
			Assert.Equal(string.Empty, urlTextBox.Text);
			Assert.Equal(0, git.OperationCount);
			await UiTestDriver.RaiseButtonClickAsync(
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
			git: true,
			initializeGit: true);
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
	public async Task CachedGitOpen_ReplacesPreviousRepositoryBranchMenuAfterPublication()
	{
		var appDataPath = CreateAppDataPath();
		var cache = new RepoCacheService(Path.Combine(appDataPath, "RepoCache"));
		CreateCachedRepository(
			cache,
			"https://github.com/example/branch-menu.git",
			"feature/cache",
			128,
			git: true,
			initializeGit: true);
		var git = new BranchCatalogGitRepositoryService(
			[new GitBranch("feature/cache", IsActive: true, IsRemote: false),
			 new GitBranch("release", IsActive: false, IsRemote: false)]);
		var window = await CreateWindowAsync(appDataPath, cache, git);

		try
		{
			var viewModel = UiTestDriver.GetViewModel(window);
			viewModel.GitBranches.Add(new GitBranch("stale-from-previous", IsActive: true, IsRemote: false));
			var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			await WaitForCatalogAsync(window, expectedCount: 1);
			var cacheCombo = Assert.IsType<ComboBox>(cloneWindow.FindControl<ComboBox>("LocalCacheComboBox"));
			cacheCombo.SelectedItem = Assert.Single(viewModel.CachedRepositories);

			await UiTestDriver.RaiseButtonClickAsync(
				Assert.IsType<Button>(cloneWindow.FindControl<Button>("StartCloneButton")));
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !cloneWindow.IsVisible &&
				      viewModel.GitBranches.Select(static branch => branch.Name)
					      .SequenceEqual(["feature/cache", "release"]),
				"the cached repository branch catalog to refresh after publication");

			Assert.Equal(["feature/cache", "release"], viewModel.GitBranches.Select(static branch => branch.Name));
			Assert.DoesNotContain(viewModel.GitBranches, static branch => branch.Name == "stale-from-previous");
			var menu = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "GitBranchMenuItem");
			Assert.Equal(
				["feature/cache", "release"],
				menu.Items.OfType<MenuItem>().Select(static item => Assert.IsType<string>(item.Tag)));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task GitCloneWindow_EscapeInRepositoryDropDownClosesOnlyThePopup()
	{
		var appDataPath = CreateAppDataPath();
		var cache = new RepoCacheService(Path.Combine(appDataPath, "RepoCache"));
		CreateCachedRepository(cache, "https://github.com/example/cached.git", "main", 64, git: true);
		var recentStore = new RecentProjectsStore(() => appDataPath);
		recentStore.AddRepository(recentStore.Load(), "https://github.com/example/recent.git");
		var window = await CreateWindowAsync(appDataPath, cache);

		try
		{
			var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			try
			{
				await WaitForCatalogAsync(window, expectedCount: 1);
				var cancelRequestCount = 0;
				cloneWindow.CancelRequested += (_, _) => cancelRequestCount++;
				var recentCombo = Assert.IsType<ComboBox>(cloneWindow.FindControl<ComboBox>("RecentRepositoriesComboBox"));
				var cacheCombo = Assert.IsType<ComboBox>(cloneWindow.FindControl<ComboBox>("LocalCacheComboBox"));

				await AssertEscapeClosesOnlyDropDownAsync(recentCombo);
				await AssertEscapeClosesOnlyDropDownAsync(cacheCombo);

				Assert.True(cloneWindow.IsVisible);
				Assert.Equal(0, cancelRequestCount);

				async Task AssertEscapeClosesOnlyDropDownAsync(ComboBox comboBox)
				{
					comboBox.Focus();
					comboBox.IsDropDownOpen = true;
					await UiTestDriver.WaitForSettledFramesAsync(frameCount: 3);
					var popup = Assert.Single(
						comboBox.GetVisualDescendants().OfType<Popup>(),
						static candidate => candidate.IsOpen);
					var popupRoot = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(popup.Child));

					await UiTestDriver.PressKeyAsync(popupRoot, Key.Escape);

					Assert.False(comboBox.IsDropDownOpen);
					Assert.True(cloneWindow.IsVisible);
					Assert.Equal(0, cancelRequestCount);
				}
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
	public async Task GitCloneWindow_RecentCacheRecentTransitionsKeepOnlyTheLastIntent()
	{
		var appDataPath = CreateAppDataPath();
		var cache = new RepoCacheService(Path.Combine(appDataPath, "RepoCache"));
		const string recentRepositoryUrl = "https://github.com/example/recent-intent.git";
		const string localCacheRepositoryUrl = "https://github.com/example/local-cache-intent.git";
		CreateCachedRepository(cache, recentRepositoryUrl, "main", 64, git: true);
		CreateCachedRepository(cache, localCacheRepositoryUrl, "feature", 64, git: true);
		var recentStore = new RecentProjectsStore(() => appDataPath);
		recentStore.AddRepository(recentStore.Load(), recentRepositoryUrl);
		var git = new OfflineUpdateGitRepositoryService();
		var window = await CreateWindowAsync(appDataPath, cache, git);

		try
		{
			var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			await WaitForCatalogAsync(window, expectedCount: 2);
			var viewModel = UiTestDriver.GetViewModel(window);
			var cacheCombo = Assert.IsType<ComboBox>(cloneWindow.FindControl<ComboBox>("LocalCacheComboBox"));
			var recentCombo = Assert.IsType<ComboBox>(cloneWindow.FindControl<ComboBox>("RecentRepositoriesComboBox"));
			var urlTextBox = Assert.IsType<TextBox>(cloneWindow.FindControl<TextBox>("UrlTextBox"));
			var recentEntry = Assert.Single(
				recentCombo.Items.OfType<RecentProjectEntryViewModel>(),
				item => string.Equals(item.Value, recentRepositoryUrl, StringComparison.OrdinalIgnoreCase));
			var cacheEntry = Assert.Single(
				viewModel.CachedRepositories,
				item => string.Equals(item.Entry.RepositoryUrl, localCacheRepositoryUrl, StringComparison.OrdinalIgnoreCase));

			recentCombo.IsDropDownOpen = true;
			recentCombo.SelectedItem = recentEntry;
			recentCombo.IsDropDownOpen = false;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
			Assert.Equal(recentRepositoryUrl, urlTextBox.Text);
			Assert.Same(recentEntry, recentCombo.SelectedItem);
			Assert.Null(cacheCombo.SelectedItem);
			Assert.Null(viewModel.SelectedGitCloneCacheEntry);

			cacheCombo.SelectedItem = cacheEntry;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.Null(recentCombo.SelectedItem);
			Assert.Equal(string.Empty, urlTextBox.Text);
			Assert.Same(cacheEntry, viewModel.SelectedGitCloneCacheEntry);

			recentCombo.IsDropDownOpen = true;
			recentCombo.IsDropDownOpen = false;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 3);
			Assert.Null(recentCombo.SelectedItem);
			Assert.Equal(string.Empty, urlTextBox.Text);
			Assert.Same(cacheEntry, cacheCombo.SelectedItem);
			Assert.Same(cacheEntry, viewModel.SelectedGitCloneCacheEntry);

			recentCombo.IsDropDownOpen = true;
			recentCombo.SelectedItem = recentEntry;
			recentCombo.IsDropDownOpen = false;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
			Assert.Equal(recentRepositoryUrl, urlTextBox.Text);
			Assert.Same(recentEntry, recentCombo.SelectedItem);
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
	public async Task GitCloneWindow_ManualUrlClearsCacheAndRecentIntentBeforeNetworkOpen()
	{
		var appDataPath = CreateAppDataPath();
		var cache = new RepoCacheService(Path.Combine(appDataPath, "RepoCache"));
		const string recentRepositoryUrl = "https://github.com/example/recent-before-manual.git";
		const string cacheRepositoryUrl = "https://github.com/example/cache-before-manual.git";
		const string manualRepositoryUrl = "https://github.com/example/manual-intent.git";
		CreateCachedRepository(cache, recentRepositoryUrl, "main", 64, git: true);
		CreateCachedRepository(cache, cacheRepositoryUrl, "feature", 64, git: true);
		CreateCachedRepository(cache, manualRepositoryUrl, "main", 64, git: true);
		var recentStore = new RecentProjectsStore(() => appDataPath);
		recentStore.AddRepository(recentStore.Load(), recentRepositoryUrl);
		var git = new OfflineUpdateGitRepositoryService();
		var window = await CreateWindowAsync(appDataPath, cache, git);

		try
		{
			var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			await WaitForCatalogAsync(window, expectedCount: 3);
			var viewModel = UiTestDriver.GetViewModel(window);
			var recentCombo = Assert.IsType<ComboBox>(cloneWindow.FindControl<ComboBox>("RecentRepositoriesComboBox"));
			var cacheCombo = Assert.IsType<ComboBox>(cloneWindow.FindControl<ComboBox>("LocalCacheComboBox"));
			var urlTextBox = Assert.IsType<TextBox>(cloneWindow.FindControl<TextBox>("UrlTextBox"));
			var recentEntry = Assert.Single(recentCombo.Items.OfType<RecentProjectEntryViewModel>());
			var cacheEntry = Assert.Single(
				viewModel.CachedRepositories,
				item => string.Equals(item.Entry.RepositoryUrl, cacheRepositoryUrl, StringComparison.OrdinalIgnoreCase));

			cacheCombo.SelectedItem = cacheEntry;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.Same(cacheEntry, cacheCombo.SelectedItem);
			Assert.Null(recentCombo.SelectedItem);
			Assert.Equal(string.Empty, urlTextBox.Text);

			await EnterManualUrlAsync();
			Assert.Null(cacheCombo.SelectedItem);
			Assert.Null(recentCombo.SelectedItem);
			Assert.Equal(manualRepositoryUrl, urlTextBox.Text);

			recentCombo.SelectedItem = recentEntry;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.Same(recentEntry, recentCombo.SelectedItem);
			Assert.Null(cacheCombo.SelectedItem);
			Assert.Equal(recentRepositoryUrl, urlTextBox.Text);

			await EnterManualUrlAsync();
			Assert.Null(cacheCombo.SelectedItem);
			Assert.Null(recentCombo.SelectedItem);
			Assert.Equal(manualRepositoryUrl, urlTextBox.Text);
			Assert.Equal(0, git.PullCount);

			await UiTestDriver.PressKeyAsync(cloneWindow, Key.Enter);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !cloneWindow.IsVisible,
				"Enter to confirm the manually entered repository URL");
			await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
			Assert.Equal(1, git.PullCount);
			Assert.Equal(0, git.CloneCount);

			async Task EnterManualUrlAsync()
			{
				await UiTestDriver.ClickAsync(window, urlTextBox);
				urlTextBox.SelectAll();
				cloneWindow.KeyTextInput(manualRepositoryUrl);
				await UiTestDriver.WaitForSettledFramesAsync(frameCount: 3);
			}
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
			await UiTestDriver.RaiseButtonClickAsync(
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
		var toasts = new RecordingToastService();
		var window = await CreateWindowAsync(appDataPath, cache, toastService: toasts);

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
				await UiTestDriver.RaiseButtonClickAsync(
					Assert.IsType<Button>(cloneWindow.FindControl<Button>("StartCloneButton")));

				await UiTestDriver.WaitForConditionAsync(
					window,
					() => viewModel.CachedRepositories.Count == 0,
					"missing cache entry to be removed from the catalog");
				await UiTestDriver.WaitForConditionAsync(
					window,
					() => toasts.Items.Any(toast =>
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
			Assert.Equal(1, git.BranchDiscoveryCount);
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
	public async Task CachedUrl_PairedGitProgressKeepsDialogProgressDeterminate()
	{
		var appDataPath = CreateAppDataPath();
		var cache = new RepoCacheService(Path.Combine(appDataPath, "RepoCache"));
		const string repositoryUrl = "https://github.com/example/progress.git";
		CreateCachedRepository(cache, repositoryUrl, "main", 128, git: true);
		var git = new PairedProgressGitRepositoryService();
		var window = await CreateWindowAsync(appDataPath, cache, git);

		try
		{
			var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			await WaitForCatalogAsync(window, expectedCount: 1);
			var viewModel = UiTestDriver.GetViewModel(window);
			var statuses = new List<string>();
			viewModel.PropertyChanged += (_, args) =>
			{
				if (args.PropertyName == nameof(MainWindowViewModel.GitCloneStatus))
					statuses.Add(viewModel.GitCloneStatus);
			};

			viewModel.GitCloneUrl = repositoryUrl;
			await UiTestDriver.RaiseButtonClickAsync(
				Assert.IsType<Button>(cloneWindow.FindControl<Button>("StartCloneButton")));
			await git.ProgressReported.Task.WaitAsync(TimeSpan.FromSeconds(10));
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !viewModel.GitCloneProgressIsIndeterminate &&
				      viewModel.GitCloneProgressValue == 42,
				"paired Git progress to become determinate");
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 3);

			var progressBar = Assert.IsType<ProgressBar>(cloneWindow.FindControl<ProgressBar>("CloneProgressBar"));
			Assert.False(progressBar.IsIndeterminate);
			Assert.Equal(42, progressBar.Value);
			Assert.EndsWith(" 42%", viewModel.GitCloneStatus, StringComparison.Ordinal);
			var firstMeasuredStatus = statuses.FindIndex(static status => status.EndsWith(" 42%", StringComparison.Ordinal));
			Assert.True(firstMeasuredStatus >= 0);
			Assert.All(
				statuses.Skip(firstMeasuredStatus),
				static status => Assert.EndsWith(" 42%", status, StringComparison.Ordinal));

			git.ReleasePull.TrySetResult();
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !cloneWindow.IsVisible && !viewModel.GitCloneInProgress,
				"cached repository operation to complete");
		}
		finally
		{
			git.ReleasePull.TrySetResult();
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task GitCloneWindow_RecentKeyboardSelectionUpdatesUrlAndEnterUsesNetworkPath()
	{
		var appDataPath = CreateAppDataPath();
		var cache = new RepoCacheService(Path.Combine(appDataPath, "RepoCache"));
		const string firstRepositoryUrl = "https://github.com/example/keyboard-first.git";
		const string secondRepositoryUrl = "https://github.com/example/keyboard-second.git";
		CreateCachedRepository(cache, firstRepositoryUrl, "main", 64, git: true);
		CreateCachedRepository(cache, secondRepositoryUrl, "main", 64, git: true);
		var recentStore = new RecentProjectsStore(() => appDataPath);
		var recentProjects = recentStore.AddRepository(recentStore.Load(), firstRepositoryUrl);
		recentStore.AddRepository(recentProjects, secondRepositoryUrl);
		var git = new OfflineUpdateGitRepositoryService();
		var window = await CreateWindowAsync(appDataPath, cache, git);

		try
		{
			var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
			await WaitForCatalogAsync(window, expectedCount: 2);
			var recentCombo = Assert.IsType<ComboBox>(cloneWindow.FindControl<ComboBox>("RecentRepositoriesComboBox"));
			var urlTextBox = Assert.IsType<TextBox>(cloneWindow.FindControl<TextBox>("UrlTextBox"));
			var recentEntries = recentCombo.Items.OfType<RecentProjectEntryViewModel>().ToArray();
			Assert.Equal(2, recentEntries.Length);
			recentCombo.Focus();
			recentCombo.IsDropDownOpen = true;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 3);

			await UiTestDriver.PressKeyAsync(cloneWindow, Key.Down);
			Assert.True(recentCombo.IsDropDownOpen);
			Assert.Same(recentEntries[0], recentCombo.SelectedItem);
			Assert.Equal(recentEntries[0].Value, urlTextBox.Text);

			await UiTestDriver.PressKeyAsync(cloneWindow, Key.Down);
			Assert.Same(recentEntries[1], recentCombo.SelectedItem);
			Assert.Equal(recentEntries[1].Value, urlTextBox.Text);

			await UiTestDriver.PressKeyAsync(cloneWindow, Key.Up);
			Assert.Same(recentEntries[0], recentCombo.SelectedItem);
			Assert.Equal(recentEntries[0].Value, urlTextBox.Text);
			Assert.Equal(0, git.PullCount);

			await UiTestDriver.PressKeyAsync(cloneWindow, Key.Enter);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !cloneWindow.IsVisible,
				"Enter to confirm the highlighted recent repository");
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
		IGitRepositoryService? git = null,
		IToastService? toastService = null) =>
		await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			appDataPathOverride: appDataPath,
			configureServices: services => services with
			{
				RepoCacheService = cache,
				GitRepositoryService = git ?? services.GitRepositoryService,
				ToastService = toastService ?? services.ToastService
			});

	private sealed class RecordingToastService : IToastService
	{
		public ObservableCollection<ToastMessageViewModel> Items { get; } = [];

		public void Show(string message) => Items.Add(new ToastMessageViewModel(message));

		public void Show(string message, TimeSpan duration) => Show(message);
	}

	private static string CreateCachedRepository(
		RepoCacheService cache,
		string repositoryUrl,
		string branch,
		int payloadSize,
		bool git,
		bool initializeGit = false)
	{
		var staging = cache.CreateRepositoryStagingDirectory(repositoryUrl);
		if (initializeGit)
		{
			InitializeGitRepository(staging, branch);
		}
		else if (git)
		{
			Directory.CreateDirectory(Path.Combine(staging, ".git"));
		}
		File.WriteAllText(Path.Combine(staging, "payload.txt"), new string('x', payloadSize));
		if (initializeGit)
		{
			RunGit(staging, ["add", "payload.txt"]);
			RunGit(staging, ["commit", "-m", "cache fixture"]);
			if (!string.Equals(branch, "main", StringComparison.Ordinal))
				RunGit(staging, ["branch", branch]);
		}
		var published = cache.PublishRepositoryDirectory(staging, repositoryUrl);
		cache.RecordIndexedRepository(repositoryUrl, published, branch);
		return published;
	}

	private static void InitializeGitRepository(string repositoryPath, string branch)
	{
		RunGit(repositoryPath, ["init", "--initial-branch=main"]);
		RunGit(repositoryPath, ["config", "user.email", "tests@devprojex.local"]);
		RunGit(repositoryPath, ["config", "user.name", "DevProjex Tests"]);
		Assert.True(GitBranchNameValidator.IsValid(branch));
	}

	private static void RunGit(string repositoryPath, IReadOnlyList<string> arguments)
	{
		using var process = new Process
		{
			StartInfo = GitProcessStartInfoFactory.Create(repositoryPath, arguments)
		};
		Assert.True(process.Start());
		process.StandardInput.Close();
		var output = process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		process.WaitForExit();
		Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}{output}");
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
		public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
			Task.FromResult<IReadOnlyList<GitBranch>>([]);
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

	private sealed class BranchCatalogGitRepositoryService(IReadOnlyList<GitBranch> branches) : IGitRepositoryService
	{
		public int BranchDiscoveryCount { get; private set; }

		public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default)
		{
			BranchDiscoveryCount++;
			return Task.FromResult(branches);
		}

		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
		public Task<GitCloneResult> CloneAsync(string url, string targetDirectory, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetDefaultBranchAsync(string repositoryPath, CancellationToken cancellationToken = default) => Task.FromResult<string?>("main");
		public Task<bool> SwitchBranchAsync(string repositoryPath, string branchName, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> PullUpdatesAsync(string repositoryPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string?> GetHeadCommitAsync(string repositoryPath, CancellationToken cancellationToken = default) => Task.FromResult<string?>("head");
		public Task<string?> GetCurrentBranchAsync(string repositoryPath, CancellationToken cancellationToken = default) => Task.FromResult<string?>(branches.FirstOrDefault(static branch => branch.IsActive)?.Name);
		public Task<string?> GetRemoteUrlAsync(string repositoryPath, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
	}

	private sealed class PairedProgressGitRepositoryService : IGitRepositoryService
	{
		public TaskCompletionSource ProgressReported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleasePull { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<string?> GetDefaultBranchAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
			Task.FromResult<string?>("main");

		public Task<bool> SwitchBranchAsync(
			string repositoryPath,
			string branchName,
			IProgress<string>? progress = null,
			CancellationToken cancellationToken = default) => Task.FromResult(true);

		public async Task<bool> PullUpdatesAsync(
			string repositoryPath,
			IProgress<string>? progress = null,
			CancellationToken cancellationToken = default)
		{
			progress?.Report("42%");
			progress?.Report("Receiving objects: 42% (42/100), 1.00 MiB");
			ProgressReported.TrySetResult();
			await ReleasePull.Task.WaitAsync(cancellationToken);
			return false;
		}

		public Task<string?> GetCurrentBranchAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
			Task.FromResult<string?>("main");

		public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
			Task.FromResult<IReadOnlyList<GitBranch>>([]);

		public Task<string?> GetHeadCommitAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
			Task.FromResult<string?>("head");

		public Task<string?> GetRemoteUrlAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
			Task.FromResult<string?>(null);

		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) =>
			Task.FromResult(true);

		public Task<GitCloneResult> CloneAsync(
			string url,
			string targetDirectory,
			IProgress<string>? progress = null,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}
}
