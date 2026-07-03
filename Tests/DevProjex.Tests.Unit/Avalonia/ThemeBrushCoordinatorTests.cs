using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class ThemeBrushCoordinatorTests
{
	[AvaloniaFact]
	public void UpdateTransparencyEffect_MapsEveryEffectModeToExpectedWindowHints()
	{
		using var noEffect = CreateHarness();
		noEffect.ViewModel.IsTransparentEnabled = false;
		noEffect.Coordinator.UpdateTransparencyEffect();
		AssertTransparencyHints(noEffect.Window, WindowTransparencyLevel.None);

		using var mica = CreateHarness();
		mica.ViewModel.IsMicaEnabled = true;
		mica.Coordinator.UpdateTransparencyEffect();
		AssertTransparencyHints(mica.Window, WindowTransparencyLevel.Mica, WindowTransparencyLevel.Blur, WindowTransparencyLevel.None);

		using var acrylic = CreateHarness();
		acrylic.ViewModel.IsAcrylicEnabled = true;
		acrylic.Coordinator.UpdateTransparencyEffect();
		AssertTransparencyHints(
			acrylic.Window,
			WindowTransparencyLevel.AcrylicBlur,
			WindowTransparencyLevel.Blur,
			WindowTransparencyLevel.Transparent,
			WindowTransparencyLevel.None);

		using var transparentWithoutBlur = CreateHarness();
		transparentWithoutBlur.ViewModel.IsTransparentEnabled = true;
		transparentWithoutBlur.ViewModel.BlurRadius = 0;
		transparentWithoutBlur.Coordinator.UpdateTransparencyEffect();
		AssertTransparencyHints(transparentWithoutBlur.Window, WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None);

		using var transparentWithBlur = CreateHarness();
		transparentWithBlur.ViewModel.IsTransparentEnabled = true;
		transparentWithBlur.ViewModel.BlurRadius = 75;
		transparentWithBlur.Coordinator.UpdateTransparencyEffect();
		AssertTransparencyHints(
			transparentWithBlur.Window,
			WindowTransparencyLevel.AcrylicBlur,
			WindowTransparencyLevel.Blur,
			WindowTransparencyLevel.Transparent,
			WindowTransparencyLevel.None);
	}

	[AvaloniaFact]
	public void UpdateDynamicThemeBrushes_PublishesExpectedResourcesAndReusesBrushInstances()
	{
		var app = global::Avalonia.Application.Current;
		Assert.NotNull(app);
		app!.RequestedThemeVariant = ThemeVariant.Dark;
		using var harness = CreateHarness();
		harness.ViewModel.IsTransparentEnabled = true;
		harness.ViewModel.MaterialIntensity = 20;

		harness.Coordinator.UpdateDynamicThemeBrushes();

		var firstBackgroundBrush = GetBrush(harness.Window, "AppBackgroundBrush");
		var firstPanelBrush = GetBrush(harness.Window, "AppPanelBrush");
		var firstMenuBrush = GetBrush(harness.Window, "MenuPopupBrush");
		var firstPanelColor = firstPanelBrush.Color;

		harness.ViewModel.MaterialIntensity = 90;
		harness.ViewModel.PanelContrast = 90;
		harness.Coordinator.UpdateDynamicThemeBrushes();

		Assert.Same(firstBackgroundBrush, GetBrush(harness.Window, "AppBackgroundBrush"));
		Assert.Same(firstPanelBrush, GetBrush(harness.Window, "AppPanelBrush"));
		Assert.Same(firstMenuBrush, GetBrush(harness.Window, "MenuPopupBrush"));
		Assert.NotEqual(firstPanelColor, firstPanelBrush.Color);
		Assert.IsType<SolidColorBrush>(app.Resources["AppAccentBrush"]);
		Assert.IsType<SolidColorBrush>(harness.Window.Resources["AppBorderBrush"]);
	}

	[AvaloniaFact]
	public void Dispose_DropsCoordinatorOwnedNullableBrushReferences()
	{
		using var harness = CreateHarness();
		harness.Coordinator.UpdateDynamicThemeBrushes();

		harness.Coordinator.Dispose();

		Assert.Null(GetPrivateFieldValue(harness.Coordinator, "_backgroundBrush"));
		Assert.Null(GetPrivateFieldValue(harness.Coordinator, "_panelBrush"));
		Assert.Null(GetPrivateFieldValue(harness.Coordinator, "_accentBrush"));
	}

	private static ThemeBrushHarness CreateHarness()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>()
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var viewModel = new MainWindowViewModel(localization, new HelpContentProvider());
		var window = new Window();
		var coordinator = new ThemeBrushCoordinator(window, viewModel, static () => null);
		return new ThemeBrushHarness(window, viewModel, coordinator);
	}

	private static SolidColorBrush GetBrush(Window window, string key)
	{
		var value = window.Resources[key];
		return Assert.IsType<SolidColorBrush>(value);
	}

	private static void AssertTransparencyHints(Window window, params WindowTransparencyLevel[] expected)
	{
		Assert.Equal(expected, window.TransparencyLevelHint.ToArray());
	}

	private static object? GetPrivateFieldValue(object instance, string fieldName)
	{
		var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		return field!.GetValue(instance);
	}

	private sealed class ThemeBrushHarness(
		Window window,
		MainWindowViewModel viewModel,
		ThemeBrushCoordinator coordinator)
		: IDisposable
	{
		public Window Window { get; } = window;
		public MainWindowViewModel ViewModel { get; } = viewModel;
		public ThemeBrushCoordinator Coordinator { get; } = coordinator;

		public void Dispose()
		{
			Coordinator.Dispose();
			ViewModel.Dispose();
		}
	}
}
