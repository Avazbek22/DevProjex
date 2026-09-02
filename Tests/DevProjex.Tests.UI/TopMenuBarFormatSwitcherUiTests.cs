using Avalonia.Interactivity;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.VisualTree;
using DevProjex.Application.Services;
using DevProjex.Application.Updates;
using DevProjex.Infrastructure.ResourceStore;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Tests.UI;

public sealed class TopMenuBarFormatSwitcherUiTests
{
	[AvaloniaFact]
	public async Task UpdateIndicator_TracksSuccessfulAvailabilityAndSurvivesFailure()
	{
		var viewModel = CreateViewModel();
		var view = new TopMenuBarView { DataContext = viewModel };
		var window = new Window
		{
			Content = view,
			Width = 520,
			Height = 90
		};

		try
		{
			window.Show();
			await FlushUiAsync();
			var indicator = Assert.IsType<Ellipse>(
				view.FindControl<Ellipse>("UpdateAvailableIndicator"));
			Assert.False(indicator.IsVisible);

			viewModel.CompleteUpdateCheck(new ApplicationUpdateCheckResult(
				ApplicationUpdateAvailability.UpdateAvailable,
				"5.1",
				"5.1"));
			await FlushUiAsync();
			Assert.True(indicator.IsVisible);

			viewModel.CompleteUpdateCheck(new ApplicationUpdateCheckResult(
				ApplicationUpdateAvailability.CheckFailed,
				"5.1"));
			await FlushUiAsync();
			Assert.True(indicator.IsVisible);

			viewModel.CompleteUpdateCheck(new ApplicationUpdateCheckResult(
				ApplicationUpdateAvailability.UpToDate,
				"5.1",
				"5.1"));
			await FlushUiAsync();
			Assert.False(indicator.IsVisible);
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaFact]
	public async Task FormatSwitcher_RendersFourFormatsInOrderAndUpdatesSelection()
	{
		var viewModel = CreateViewModel();
		viewModel.IsProjectLoaded = true;
		var view = new TopMenuBarView { DataContext = viewModel };
		var window = new Window
		{
			Content = view,
			Width = 520,
			Height = 90
		};

		try
		{
			window.Show();
			await FlushUiAsync();

			var buttons = view.GetVisualDescendants()
				.OfType<Button>()
				.Where(static button => button.Content is "ASCII" or "JSON" or "XML" or "MD")
				.ToArray();

			Assert.Equal(["ASCII", "JSON", "XML", "MD"], buttons.Select(static button => button.Content).Cast<string>().ToArray());

			viewModel.SelectedExportFormat = ExportFormat.Xml;
			await FlushUiAsync();

			Assert.DoesNotContain("segment-selected", buttons[0].Classes);
			Assert.DoesNotContain("segment-selected", buttons[1].Classes);
			Assert.Contains("segment-selected", buttons[2].Classes);
			Assert.DoesNotContain("segment-selected", buttons[3].Classes);

			buttons[3].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await FlushUiAsync();

			Assert.Equal(ExportFormat.Markdown, viewModel.SelectedExportFormat);
			Assert.Contains("segment-selected", buttons[3].Classes);
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaFact]
	public async Task ProjectToolsReveal_StartsTogetherAndCancellationRestoresInteractiveControls()
	{
		var viewModel = CreateViewModel();
		viewModel.IsProjectLoaded = true;
		var view = new TopMenuBarView { DataContext = viewModel };
		var window = new Window
		{
			Content = view,
			Width = 900,
			Height = 90
		};

		try
		{
			window.Show();
			await FlushUiAsync();
			var controls = new Control[]
			{
				Assert.IsAssignableFrom<Control>(view.FindControl<Control>("FilterToggleButton")),
				Assert.IsAssignableFrom<Control>(view.FindControl<Control>("PreviewToggleButton")),
				Assert.IsAssignableFrom<Control>(view.FindControl<Control>("FormatSegmentedControl"))
			};

			view.PrepareProjectToolsReveal(animate: true);
			Assert.All(controls, static control =>
			{
				Assert.Equal(0, control.Opacity);
				Assert.False(control.IsHitTestVisible);
			});

			var revealTask = view.RevealProjectToolsAsync(
				animate: true,
				TestContext.Current.CancellationToken);
			await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
			var firstOpacity = controls[0].Opacity;
			Assert.All(controls, static control =>
			{
				Assert.True(control.IsHitTestVisible);
				Assert.IsType<RectangleGeometry>(control.Clip);
			});
			Assert.All(controls, control => Assert.Equal(firstOpacity, control.Opacity, 3));

			view.CompleteProjectToolsReveal();
			await revealTask;
			Assert.All(controls, static control =>
			{
				Assert.Equal(1, control.Opacity);
				Assert.True(control.IsHitTestVisible);
				Assert.Null(control.Clip);
			});
		}
		finally
		{
			window.Close();
		}
	}

	private static async Task FlushUiAsync()
	{
		await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
		await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
	}

	private static MainWindowViewModel CreateViewModel()
	{
		var localization = new LocalizationService(new TestLocalizationCatalog(), AppLanguage.En);
		return new MainWindowViewModel(localization, new HelpContentProvider());
	}

	private sealed class TestLocalizationCatalog : ILocalizationCatalog
	{
		private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

		public IReadOnlyDictionary<string, string> Get(AppLanguage language) => Empty;
	}
}
