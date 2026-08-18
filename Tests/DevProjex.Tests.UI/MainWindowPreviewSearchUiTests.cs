using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using DevProjex.Avalonia.Controls;
using DevProjex.Avalonia.Views;
using System.Reflection;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowPreviewSearchUiTests
{
	[AvaloniaFact]
	public async Task HotkeyToggleAndEscape_AnimatePanelAndRestorePreviewFocus()
	{
		using var project = UiTestProject.CreateWithPreviewSearchWorkspace();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

		try
		{
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
			var viewModel = UiTestDriver.GetViewModel(window);
			var searchBar = UiTestDriver.GetRequiredControl<PreviewSearchBarView>(
				window,
				"PreviewSearchBar");
			var container = UiTestDriver.GetRequiredControl<Border>(
				window,
				"PreviewSearchBarContainer");
			var preview = UiTestDriver.GetRequiredControl<VirtualizedPreviewTextControl>(
				window,
				"PreviewTextControl");
			var searchButton = UiTestDriver.GetRequiredControl<Button>(
				window,
				"PreviewSearchButton");

			await UiTestDriver.ClickAsync(window, searchButton);
			await WaitForSearchPanelAsync(window, visible: true);

			Assert.True(viewModel.PreviewSearchVisible);
			Assert.True(container.IsVisible);
			Assert.InRange(container.Bounds.Height, 45, 47);
			Assert.True(searchBar.SearchBoxControl!.IsFocused);

			await UiTestDriver.PressKeyAsync(
				window,
				Key.F,
				RawInputModifiers.Control | RawInputModifiers.Shift);
			await WaitForSearchPanelAsync(window, visible: false);
			Assert.False(viewModel.PreviewSearchVisible);

			await UiTestDriver.PressKeyAsync(
				window,
				Key.F,
				RawInputModifiers.Control | RawInputModifiers.Shift);
			await WaitForSearchPanelAsync(window, visible: true);
			await UiTestDriver.PressKeyAsync(window, Key.Escape);
			await WaitForSearchPanelAsync(window, visible: false);

			Assert.True(preview.IsFocused);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task LiveSearch_SelectsVisibleMatchAndNavigatesWithWrapAround()
	{
		using var project = UiTestProject.CreateWithPreviewSearchWorkspace();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

		try
		{
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
			await OpenSearchAsync(window);
			var viewModel = UiTestDriver.GetViewModel(window);
			var searchBox = UiTestDriver.GetRequiredControl<PreviewSearchBarView>(
				window,
				"PreviewSearchBar").SearchBoxControl!;
			var preview = UiTestDriver.GetRequiredControl<VirtualizedPreviewTextControl>(
				window,
				"PreviewTextControl");
			var scrollViewer = UiTestDriver.GetRequiredPreviewScrollViewer(window);

			searchBox.Text = "previewsearchneedle";
			await WaitForPreviewSearchCountAsync(window, expectedCount: 3);

			Assert.Equal(1, viewModel.PreviewSearchCurrentMatchIndex);
			Assert.Equal("(1 / 3)", viewModel.PreviewSearchMatchSummaryText);
			Assert.Equal("PreviewSearchNeedle", preview.GetSelectedText());
			Assert.True(scrollViewer.Offset.Y > 0);

			await UiTestDriver.PressKeyAsync(window, Key.Enter);
			Assert.Equal(2, viewModel.PreviewSearchCurrentMatchIndex);
			Assert.Equal("previewsearchneedle", preview.GetSelectedText());
			await UiTestDriver.PressKeyAsync(window, Key.Enter);
			await UiTestDriver.PressKeyAsync(window, Key.Enter);
			Assert.Equal(1, viewModel.PreviewSearchCurrentMatchIndex);

			await UiTestDriver.PressKeyAsync(window, Key.Enter, RawInputModifiers.Shift);
			Assert.Equal(3, viewModel.PreviewSearchCurrentMatchIndex);
			Assert.Equal("PreviewSearchNeedle", preview.GetSelectedText());
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task RedactionRefresh_RescansVisibleTextWithoutMovingViewportOrFindingHiddenOriginal()
	{
		using var project = UiTestProject.CreateWithPreviewSearchWorkspace();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

		try
		{
			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
			await OpenSearchAsync(window);
			var viewModel = UiTestDriver.GetViewModel(window);
			var searchBox = UiTestDriver.GetRequiredControl<PreviewSearchBarView>(
				window,
				"PreviewSearchBar").SearchBoxControl!;
			var scrollViewer = UiTestDriver.GetRequiredPreviewScrollViewer(window);
			var preview = UiTestDriver.GetRequiredControl<VirtualizedPreviewTextControl>(
				window,
				"PreviewTextControl");

			searchBox.Text = "AKIAZ7M3Q5X2P6N4R7T5";
			await WaitForPreviewSearchCountAsync(window, expectedCount: 0);
			Assert.Equal("(0 / 0)", viewModel.PreviewSearchMatchSummaryText);

			searchBox.Text = "DEVPROJEX_REDACTED";
			await WaitForPreviewSearchCountAsync(window, expectedCount: 1);
			await UiTestDriver.PressKeyAsync(window, Key.Down, RawInputModifiers.Alt);
			Assert.NotNull(
				typeof(VirtualizedPreviewTextControl)
					.GetField("_activeRedactionTarget", BindingFlags.Instance | BindingFlags.NonPublic)!
					.GetValue(preview));
			var occurrenceId = Assert.Single(
				preview.Document!.Redactions
					.Select(static span => span.OccurrenceId)
					.Distinct(StringComparer.Ordinal));
			var offsetBeforeKeep = scrollViewer.Offset;

			await UiTestDriver.RequestRedactionToggleAsync(window, occurrenceId);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => viewModel.PreviewSearchTotalMatches == 0 &&
				      preview.Document?.GetLineRangeText(1, preview.Document.LineCount)
					      .Contains("AKIAZ7M3Q5X2P6N4R7T5", StringComparison.Ordinal) == true,
				"preview search to rescan the kept visible value");

			Assert.Equal("(0 / 0)", viewModel.PreviewSearchMatchSummaryText);
			Assert.InRange(Math.Abs(scrollViewer.Offset.X - offsetBeforeKeep.X), 0, 0.5);
			Assert.InRange(Math.Abs(scrollViewer.Offset.Y - offsetBeforeKeep.Y), 0, 0.5);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task TreeAndContent_SearchesFileContentButNotTreeOrSectionHeaders()
	{
		using var project = UiTestProject.CreateWithPreviewSearchWorkspace();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

		try
		{
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.TreeAndContent);
			await OpenSearchAsync(window);
			var searchBox = UiTestDriver.GetRequiredControl<PreviewSearchBarView>(
				window,
				"PreviewSearchBar").SearchBoxControl!;
			var preview = UiTestDriver.GetRequiredControl<VirtualizedPreviewTextControl>(
				window,
				"PreviewTextControl");

			Assert.Contains(
				preview.Document!.Sections,
				section => preview.Document.GetLineText(section.HeaderLine)
					.Contains("PreviewSearch.cs", StringComparison.Ordinal));
			searchBox.Text = "PreviewSearch.cs";
			await WaitForPreviewSearchCountAsync(window, expectedCount: 0);
			Assert.Equal("(0 / 0)", UiTestDriver.GetViewModel(window).PreviewSearchMatchSummaryText);

			searchBox.Text = "PreviewSearchNeedle";
			await WaitForPreviewSearchCountAsync(window, expectedCount: 3);
			Assert.Equal("PreviewSearchNeedle", preview.GetSelectedText());
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task TreeOnlyMode_DisablesSearchButtonAndIgnoresToggleHotkey()
	{
		using var project = UiTestProject.CreateWithPreviewSearchWorkspace();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

		try
		{
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
			await OpenSearchAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Tree);
			await WaitForSearchPanelAsync(window, visible: false);
			var viewModel = UiTestDriver.GetViewModel(window);
			var searchButton = UiTestDriver.GetRequiredControl<Button>(window, "PreviewSearchButton");
			var container = UiTestDriver.GetRequiredControl<Border>(
				window,
				"PreviewSearchBarContainer");

			Assert.False(viewModel.IsPreviewSearchAvailable);
			Assert.False(searchButton.IsEnabled);
			await UiTestDriver.PressKeyAsync(
				window,
				Key.F,
				RawInputModifiers.Control | RawInputModifiers.Shift);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

			Assert.False(viewModel.PreviewSearchVisible);
			Assert.False(container.IsVisible);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task SearchIconAndHighlightPalette_MatchPreviewToolbarAndMasterTreeSearchPalette()
	{
		using var project = UiTestProject.CreateWithPreviewSearchWorkspace();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

		try
		{
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
			var searchButton = UiTestDriver.GetRequiredControl<Button>(window, "PreviewSearchButton");
			var searchIcon = UiTestDriver.GetRequiredControl<Viewbox>(window, "PreviewSearchIcon");
			AssertSearchIconFitsButton(searchIcon, searchButton);

			await UiTestDriver.ClickAsync(window, searchButton);
			await WaitForSearchPanelAsync(window, visible: true);
			window.Width = 1100;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
			AssertSearchIconFitsButton(searchIcon, searchButton);
			var searchContainer = UiTestDriver.GetRequiredControl<Border>(
				window,
				"PreviewSearchBarContainer");
			var previewPane = UiTestDriver.GetRequiredControl<Grid>(window, "PreviewPaneRoot");
			var searchBounds = UiTestDriver.GetBoundsInWindow(searchContainer, window);
			var previewBounds = UiTestDriver.GetBoundsInWindow(previewPane, window);
			Assert.True(searchBounds.Left >= previewBounds.Left - 0.5);
			Assert.True(searchBounds.Right <= previewBounds.Right + 0.5);
			var searchBox = UiTestDriver.GetRequiredControl<PreviewSearchBarView>(
				window,
				"PreviewSearchBar").SearchBoxControl!;
			searchBox.Text = "PreviewSearchNeedle";
			await WaitForPreviewSearchCountAsync(window, expectedCount: 3);

			var preview = UiTestDriver.GetRequiredControl<VirtualizedPreviewTextControl>(
				window,
				"PreviewTextControl");
			var application = global::Avalonia.Application.Current;
			Assert.NotNull(application);
			var theme = application.ActualThemeVariant ?? ThemeVariant.Light;
			AssertThemeSearchPalette(application, ThemeVariant.Light);
			AssertThemeSearchPalette(application, ThemeVariant.Dark);
			AssertSearchBrush(
				preview,
				application,
				theme,
				current: false,
				"TreeSearchHighlightBrush",
				"#FFEB3B");
			AssertSearchBrush(
				preview,
				application,
				theme,
				current: true,
				"TreeSearchCurrentBrush",
				"#F9A825");
			AssertSearchTextBrush(preview, application, theme, "#000000");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	private static async Task OpenSearchAsync(MainWindow window)
	{
		await UiTestDriver.PressKeyAsync(
			window,
			Key.F,
			RawInputModifiers.Control | RawInputModifiers.Shift);
		await WaitForSearchPanelAsync(window, visible: true);
	}

	private static async Task WaitForSearchPanelAsync(MainWindow window, bool visible)
	{
		await UiTestDriver.WaitForConditionAsync(
			window,
			() =>
			{
				var viewModel = UiTestDriver.GetViewModel(window);
				var container = UiTestDriver.GetRequiredControl<Border>(
					window,
					"PreviewSearchBarContainer");
				var searchBox = UiTestDriver.GetRequiredControl<PreviewSearchBarView>(
					window,
					"PreviewSearchBar").SearchBoxControl;
				return visible
					? viewModel.PreviewSearchVisible &&
					  container.IsVisible &&
					  container.Bounds.Height >= 45 &&
					  searchBox?.IsFocused == true
					: !viewModel.PreviewSearchVisible && !container.IsVisible;
			},
			visible ? "preview search panel to open" : "preview search panel to close");
	}

	private static async Task WaitForPreviewSearchCountAsync(MainWindow window, int expectedCount)
	{
		await UiTestDriver.WaitForConditionAsync(
			window,
			() =>
			{
				var viewModel = UiTestDriver.GetViewModel(window);
				return !viewModel.IsPreviewSearchInProgress &&
				       viewModel.PreviewSearchTotalMatches == expectedCount;
			},
			$"preview search count to become {expectedCount}");
		await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
	}

	private static void AssertSearchBrush(
		VirtualizedPreviewTextControl preview,
		global::Avalonia.Application application,
		ThemeVariant theme,
		bool current,
		string resourceKey,
		string expectedColor)
	{
		Assert.True(application.TryFindResource(resourceKey, theme, out var expected));
		var method = typeof(VirtualizedPreviewTextControl).GetMethod(
			"ResolveSearchBrush",
			BindingFlags.Instance | BindingFlags.NonPublic);
		var actual = method!.Invoke(preview, [current]);
		Assert.Same(expected, actual);
		Assert.Equal(Color.Parse(expectedColor), Assert.IsType<SolidColorBrush>(actual).Color);
	}

	private static void AssertSearchTextBrush(
		VirtualizedPreviewTextControl preview,
		global::Avalonia.Application application,
		ThemeVariant theme,
		string expectedColor)
	{
		Assert.True(application.TryFindResource(
			"TreeSearchHighlightTextBrush",
			theme,
			out var expected));
		var method = typeof(VirtualizedPreviewTextControl).GetMethod(
			"ResolveSearchHighlightTextBrush",
			BindingFlags.Instance | BindingFlags.NonPublic);
		var actual = method!.Invoke(preview, null);
		Assert.Same(expected, actual);
		Assert.Equal(Color.Parse(expectedColor), Assert.IsType<SolidColorBrush>(actual).Color);
	}

	private static void AssertThemeSearchPalette(
		global::Avalonia.Application application,
		ThemeVariant theme)
	{
		AssertThemeBrushColor(application, theme, "TreeSearchHighlightBrush", "#FFEB3B");
		AssertThemeBrushColor(application, theme, "TreeSearchCurrentBrush", "#F9A825");
		AssertThemeBrushColor(application, theme, "TreeSearchHighlightTextBrush", "#000000");
	}

	private static void AssertThemeBrushColor(
		global::Avalonia.Application application,
		ThemeVariant theme,
		string resourceKey,
		string expectedColor)
	{
		Assert.True(application.TryFindResource(resourceKey, theme, out var resource));
		var brush = Assert.IsType<SolidColorBrush>(resource);
		Assert.Equal(Color.Parse(expectedColor), brush.Color);
	}

	private static void AssertSearchIconFitsButton(Viewbox searchIcon, Button searchButton)
	{
		var iconOrigin = Assert.IsType<Point>(searchIcon.TranslatePoint(default, searchButton));
		var iconBounds = new Rect(iconOrigin, searchIcon.Bounds.Size);
		Assert.True(iconBounds.Left >= 0 && iconBounds.Top >= 0);
		Assert.True(iconBounds.Right <= searchButton.Bounds.Width);
		Assert.True(iconBounds.Bottom <= searchButton.Bounds.Height);
		Assert.InRange(
			Math.Abs(iconBounds.Center.X - (searchButton.Bounds.Width / 2)),
			0,
			0.75);
		Assert.InRange(
			Math.Abs(iconBounds.Center.Y - (searchButton.Bounds.Height / 2)),
			0,
			0.75);
	}
}
