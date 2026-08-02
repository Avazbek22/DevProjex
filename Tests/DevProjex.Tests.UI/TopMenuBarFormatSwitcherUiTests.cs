using Avalonia.Interactivity;
using Avalonia.Controls.Shapes;
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
				"5.0",
				"5.1"));
			await FlushUiAsync();
			Assert.True(indicator.IsVisible);

			viewModel.CompleteUpdateCheck(new ApplicationUpdateCheckResult(
				ApplicationUpdateAvailability.CheckFailed,
				"5.0"));
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
