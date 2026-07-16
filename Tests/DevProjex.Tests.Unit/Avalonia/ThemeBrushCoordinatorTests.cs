using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using ThemeEffectMode = DevProjex.Infrastructure.ThemePresets.ThemeEffectMode;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
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
		Assert.True(firstPanelColor.A < byte.MaxValue, "Transparent mode must publish a translucent panel material.");

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

	[Fact]
	public void Calculate_EveryThemeEffectAndBoundaryValue_ProducesValidLayerOrdering()
	{
		var boundaryValues = new[] { -25d, 0d, 50d, 100d, 125d };

		foreach (var isDark in new[] { false, true })
		{
			foreach (var effect in Enum.GetValues<ThemeEffectMode>())
			{
				foreach (var value in boundaryValues)
				{
					var palette = ThemePaletteCalculator.Calculate(
						isDark,
						effect,
						value,
						value,
						value,
						value,
						value);

					Assert.Equal((byte)Math.Round(255 * Math.Clamp(value / 100, 0, 1)), palette.Border.A);
					if (effect == ThemeEffectMode.Solid)
					{
						Assert.Equal(byte.MaxValue, palette.Background.A);
						Assert.Equal(byte.MaxValue, palette.Panel.A);
						Assert.Equal(byte.MaxValue, palette.Menu.A);
						Assert.Equal(byte.MaxValue, palette.MenuChild.A);
					}
					else
					{
						Assert.InRange(palette.Background.A, (byte)90, byte.MaxValue);
						Assert.True(
							palette.Panel.A <= palette.Background.A - 12,
							$"Panel ordering failed for {(isDark ? "Dark" : "Light")}.{effect} at {value}.");
					}
				}
			}
		}
	}

	[Fact]
	public void Calculate_SolidMode_UsesExactOpaqueThemeColors()
	{
		var dark = ThemePaletteCalculator.Calculate(true, ThemeEffectMode.Solid, 37, 48, 59, 61, 72);
		var light = ThemePaletteCalculator.Calculate(false, ThemeEffectMode.Solid, 37, 48, 59, 61, 72);

		Assert.Equal(Color.Parse("#FF121214"), dark.Background);
		Assert.Equal(Color.Parse("#FF17171A"), dark.Panel);
		Assert.Equal(Color.Parse("#FF17171A"), dark.Menu);
		Assert.Equal(Color.Parse("#FFFFFFFF"), light.Background);
		Assert.Equal(Color.Parse("#FFF3F3F3"), light.Panel);
		Assert.Equal(Color.Parse("#FFF3F3F3"), light.Menu);
	}

	[AvaloniaFact]
	public void UpdateDynamicThemeBrushes_EveryEffectPublishesCalculatedPalette()
	{
		var app = global::Avalonia.Application.Current;
		Assert.NotNull(app);
		app!.RequestedThemeVariant = ThemeVariant.Dark;
		using var harness = CreateHarness();
		harness.ViewModel.MaterialIntensity = 63;
		harness.ViewModel.BlurRadius = 29;
		harness.ViewModel.PanelContrast = 41;
		harness.ViewModel.MenuChildIntensity = 17;
		harness.ViewModel.BorderStrength = 52;

		foreach (var effect in Enum.GetValues<ThemeEffectMode>())
		{
			SetEffect(harness.ViewModel, effect);
			harness.Coordinator.UpdateDynamicThemeBrushes();
			var isDark = app.ActualThemeVariant == ThemeVariant.Dark;
			var expected = ThemePaletteCalculator.Calculate(isDark, effect, 63, 29, 41, 17, 52);

			Assert.Equal(expected.Background, GetBrush(harness.Window, "AppBackgroundBrush").Color);
			Assert.Equal(expected.Panel, GetBrush(harness.Window, "AppPanelBrush").Color);
			Assert.Equal(expected.Menu, GetBrush(harness.Window, "MenuPopupBrush").Color);
			Assert.Equal(expected.MenuChild, GetBrush(harness.Window, "MenuChildPopupBrush").Color);
			Assert.Equal(expected.Border, GetBrush(harness.Window, "AppBorderBrush").Color);
		}
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

	private static void SetEffect(MainWindowViewModel viewModel, ThemeEffectMode effect)
	{
		viewModel.SetThemeEffects(
			transparent: effect == ThemeEffectMode.Transparent,
			mica: effect == ThemeEffectMode.Mica,
			acrylic: effect == ThemeEffectMode.Acrylic);
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
