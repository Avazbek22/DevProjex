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
		AssertTransparencyHints(
			mica.Window,
			WindowTransparencyLevel.Mica,
			WindowTransparencyLevel.AcrylicBlur,
			WindowTransparencyLevel.Blur,
			WindowTransparencyLevel.None);

		using var acrylic = CreateHarness();
		acrylic.ViewModel.IsAcrylicEnabled = true;
		acrylic.Coordinator.UpdateTransparencyEffect();
		AssertTransparencyHints(
			acrylic.Window,
			WindowTransparencyLevel.AcrylicBlur,
			WindowTransparencyLevel.Blur,
			WindowTransparencyLevel.Mica,
			WindowTransparencyLevel.None);

		using var transparent = CreateHarness();
		transparent.ViewModel.IsTransparentEnabled = true;
		transparent.Coordinator.UpdateTransparencyEffect();
		AssertTransparencyHints(transparent.Window, WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None);
	}

	[AvaloniaFact]
	public void UpdateDynamicThemeBrushes_PublishesExpectedResourcesAndReusesBrushInstances()
	{
		var app = global::Avalonia.Application.Current;
		Assert.NotNull(app);
		app!.RequestedThemeVariant = ThemeVariant.Dark;
		using var harness = CreateHarness();
		harness.ViewModel.IsTransparentEnabled = true;
		harness.ViewModel.BackgroundTransparency = 20;

		harness.Coordinator.UpdateDynamicThemeBrushes();

		var firstBackgroundBrush = GetBrush(harness.Window, "AppBackgroundBrush");
		var firstPanelBrush = GetBrush(harness.Window, "AppPanelBrush");
		var firstMainMenuStripBrush = GetBrush(harness.Window, "MainMenuStripBrush");
		var firstMainMenuPopupBrush = GetBrush(harness.Window, "MainMenuPopupBrush");
		var firstMenuBrush = GetBrush(harness.Window, "MenuPopupBrush");
		var firstPanelColor = firstPanelBrush.Color;
		var firstMainMenuStripColor = firstMainMenuStripBrush.Color;
		Assert.True(firstPanelColor.A < byte.MaxValue, "Transparent mode must publish a translucent panel material.");

		harness.ViewModel.BackgroundTransparency = 90;
		harness.ViewModel.PanelContrast = 90;
		harness.Coordinator.UpdateDynamicThemeBrushes();

		Assert.Same(firstBackgroundBrush, GetBrush(harness.Window, "AppBackgroundBrush"));
		Assert.Same(firstPanelBrush, GetBrush(harness.Window, "AppPanelBrush"));
		Assert.Same(firstMainMenuStripBrush, GetBrush(harness.Window, "MainMenuStripBrush"));
		Assert.Same(firstMainMenuPopupBrush, GetBrush(harness.Window, "MainMenuPopupBrush"));
		Assert.Same(firstMenuBrush, GetBrush(harness.Window, "MenuPopupBrush"));
		Assert.NotEqual(firstPanelColor, firstPanelBrush.Color);
		Assert.NotEqual(firstMainMenuStripColor, firstMainMenuStripBrush.Color);
		Assert.IsType<SolidColorBrush>(app.Resources["AppAccentBrush"]);
		Assert.IsType<SolidColorBrush>(harness.Window.Resources["AppBorderBrush"]);
		var fallback = Assert.IsType<SolidColorBrush>(harness.Window.TransparencyBackgroundFallback);
		Assert.Equal(byte.MaxValue, fallback.Color.A);
	}

	[AvaloniaFact]
	public void ScheduleDynamicThemeBrushUpdate_CoalescesPendingWorkAndImmediateUpdateSupersedesIt()
	{
		using var harness = CreateHarness();

		harness.Coordinator.ScheduleDynamicThemeBrushUpdate();
		harness.Coordinator.ScheduleDynamicThemeBrushUpdate();

		Assert.Equal(1, GetPrivateFieldValue(harness.Coordinator, "_dynamicUpdateScheduled"));

		harness.Coordinator.UpdateDynamicThemeBrushes();

		Assert.Equal(0, GetPrivateFieldValue(harness.Coordinator, "_dynamicUpdateScheduled"));
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
						value);

					Assert.Equal((byte)Math.Round(255 * Math.Clamp(value / 100, 0, 1)), palette.Border.A);
					if (effect == ThemeEffectMode.Solid)
					{
						Assert.Equal(byte.MaxValue, palette.Background.A);
						Assert.Equal(byte.MaxValue, palette.Panel.A);
						Assert.Equal(byte.MaxValue, palette.Menu.A);
						Assert.Equal(byte.MaxValue, palette.MenuChild.A);
					}
					else if (effect == ThemeEffectMode.Mica)
					{
						Assert.Equal(0, palette.Background.A);
						Assert.InRange(palette.Panel.A, (byte)112, (byte)224);
					}
					else
					{
						Assert.InRange(palette.Background.A, (byte)90, byte.MaxValue);
						Assert.True(
							palette.Panel.A <= palette.Background.A - 12,
							$"Panel ordering failed for {(isDark ? "Dark" : "Light")}.{effect} at {value}.");
					}

					Assert.Equal(byte.MaxValue, palette.TransparencyFallback.A);
				}
			}
		}
	}

	[Fact]
	public void Calculate_SolidMode_UsesExactOpaqueThemeColors()
	{
		var dark = ThemePaletteCalculator.Calculate(true, ThemeEffectMode.Solid, 37, 59, 61, 72);
		var light = ThemePaletteCalculator.Calculate(false, ThemeEffectMode.Solid, 37, 59, 61, 72);

		Assert.Equal(Color.Parse("#FF121214"), dark.Background);
		Assert.Equal(Color.Parse("#FF17171A"), dark.Panel);
		Assert.Equal(Color.Parse("#FF17171A"), dark.MainMenuStrip);
		Assert.Equal(Color.Parse("#FF17171A"), dark.MainMenuPopup);
		Assert.Equal(Color.Parse("#FF17171A"), dark.Menu);
		Assert.Equal(Color.Parse("#FF17171A"), dark.MenuChild);
		Assert.Equal(Color.Parse("#FFFFFFFF"), light.Background);
		Assert.Equal(Color.Parse("#FFF3F3F3"), light.Panel);
		Assert.Equal(Color.Parse("#FFF3F3F3"), light.MainMenuStrip);
		Assert.Equal(Color.Parse("#FFF3F3F3"), light.MainMenuPopup);
		Assert.Equal(Color.Parse("#FFF3F3F3"), light.Menu);
		Assert.Equal(Color.Parse("#FFF3F3F3"), light.MenuChild);
	}

	[Fact]
	public void Calculate_MicaUsesNativeBackdropWithoutSyntheticWindowOverlay()
	{
		var lowIntensity = ThemePaletteCalculator.Calculate(true, ThemeEffectMode.Mica, 0, 42, 17, 58);
		var highIntensity = ThemePaletteCalculator.Calculate(true, ThemeEffectMode.Mica, 100, 42, 17, 58);

		Assert.Equal(lowIntensity, highIntensity);
		Assert.Equal(Colors.Transparent, lowIntensity.Background);
		Assert.NotEqual(0, lowIntensity.Panel.A);
		Assert.Equal(Color.Parse("#FF121214"), lowIntensity.TransparencyFallback);
	}

	[Theory]
	[InlineData(false, ThemeEffectMode.Transparent)]
	[InlineData(true, ThemeEffectMode.Transparent)]
	[InlineData(false, ThemeEffectMode.Acrylic)]
	[InlineData(true, ThemeEffectMode.Acrylic)]
	[InlineData(false, ThemeEffectMode.Mica)]
	[InlineData(true, ThemeEffectMode.Mica)]
	public void Calculate_PanelContrastChangesOnlyMainMenuStrip(bool isDark, ThemeEffectMode effect)
	{
		var lowContrast = ThemePaletteCalculator.Calculate(isDark, effect, 63, 0, 37, 52);
		var highContrast = ThemePaletteCalculator.Calculate(isDark, effect, 63, 100, 37, 52);

		Assert.NotEqual(lowContrast.MainMenuStrip, highContrast.MainMenuStrip);
		Assert.Equal(lowContrast.Panel.A, lowContrast.MainMenuStrip.A);
		Assert.Equal(byte.MaxValue, highContrast.MainMenuStrip.A);
		Assert.Equal(lowContrast with { MainMenuStrip = highContrast.MainMenuStrip }, highContrast);
	}

	[Theory]
	[InlineData(false, ThemeEffectMode.Transparent)]
	[InlineData(true, ThemeEffectMode.Transparent)]
	[InlineData(false, ThemeEffectMode.Acrylic)]
	[InlineData(true, ThemeEffectMode.Acrylic)]
	public void Calculate_BackgroundTransparencyUsesEntireRangeWithoutDeadZones(
		bool isDark,
		ThemeEffectMode effect)
	{
		var previousAlpha = ThemePaletteCalculator.Calculate(isDark, effect, 0, 50, 50, 50).Background.A;
		Assert.Equal(byte.MaxValue, previousAlpha);

		for (var intensity = 1; intensity <= 100; intensity++)
		{
			var current = ThemePaletteCalculator.Calculate(isDark, effect, intensity, 50, 50, 50);
			Assert.True(
				current.Background.A < previousAlpha,
				$"Background alpha did not decrease at {intensity} for {(isDark ? "Dark" : "Light")}.{effect}.");
			previousAlpha = current.Background.A;
		}

		Assert.Equal((byte)90, previousAlpha);
	}

	[Theory]
	[InlineData(false, ThemeEffectMode.Transparent)]
	[InlineData(true, ThemeEffectMode.Transparent)]
	[InlineData(false, ThemeEffectMode.Acrylic)]
	[InlineData(true, ThemeEffectMode.Acrylic)]
	[InlineData(false, ThemeEffectMode.Mica)]
	[InlineData(true, ThemeEffectMode.Mica)]
	public void Calculate_MenuTransparencyChangesOnlyMainMenuPopup(bool isDark, ThemeEffectMode effect)
	{
		var opaque = ThemePaletteCalculator.Calculate(isDark, effect, 63, 41, 0, 52);
		var transparent = ThemePaletteCalculator.Calculate(isDark, effect, 63, 41, 100, 52);
		var previousAlpha = opaque.MainMenuPopup.A;

		for (var transparency = 10; transparency <= 100; transparency += 10)
		{
			var current = ThemePaletteCalculator.Calculate(isDark, effect, 63, 41, transparency, 52);
			Assert.True(current.MainMenuPopup.A < previousAlpha);
			previousAlpha = current.MainMenuPopup.A;
		}

		Assert.True(transparent.MainMenuPopup.A < opaque.MainMenuPopup.A);
		Assert.Equal((byte)72, transparent.MainMenuPopup.A);
		Assert.Equal(opaque with { MainMenuPopup = transparent.MainMenuPopup }, transparent);
	}

	[AvaloniaFact]
	public void UpdateDynamicThemeBrushes_EveryEffectPublishesCalculatedPalette()
	{
		var app = global::Avalonia.Application.Current;
		Assert.NotNull(app);
		app!.RequestedThemeVariant = ThemeVariant.Dark;
		using var harness = CreateHarness();
		harness.ViewModel.BackgroundTransparency = 63;
		harness.ViewModel.PanelContrast = 41;
		harness.ViewModel.MenuTransparency = 17;
		harness.ViewModel.BorderVisibility = 52;

		foreach (var effect in Enum.GetValues<ThemeEffectMode>())
		{
			SetEffect(harness.ViewModel, effect);
			harness.Coordinator.UpdateDynamicThemeBrushes();
			var isDark = app.ActualThemeVariant == ThemeVariant.Dark;
			var expected = ThemePaletteCalculator.Calculate(isDark, effect, 63, 41, 17, 52);

			Assert.Equal(expected.Background, GetBrush(harness.Window, "AppBackgroundBrush").Color);
			Assert.Equal(expected.Panel, GetBrush(harness.Window, "AppPanelBrush").Color);
			Assert.Equal(expected.MainMenuStrip, GetBrush(harness.Window, "MainMenuStripBrush").Color);
			Assert.Equal(expected.MainMenuPopup, GetBrush(harness.Window, "MainMenuPopupBrush").Color);
			Assert.Equal(expected.Menu, GetBrush(harness.Window, "MenuPopupBrush").Color);
			Assert.Equal(expected.MenuChild, GetBrush(harness.Window, "MenuChildPopupBrush").Color);
			Assert.Equal(expected.Border, GetBrush(harness.Window, "AppBorderBrush").Color);
			Assert.Equal(
				expected.TransparencyFallback,
				Assert.IsType<SolidColorBrush>(harness.Window.TransparencyBackgroundFallback).Color);
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
		Assert.Null(GetPrivateFieldValue(harness.Coordinator, "_mainMenuStripBrush"));
		Assert.Null(GetPrivateFieldValue(harness.Coordinator, "_mainMenuPopupBrush"));
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
