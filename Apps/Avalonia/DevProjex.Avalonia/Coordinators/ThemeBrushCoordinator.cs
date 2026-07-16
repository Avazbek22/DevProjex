using Avalonia.LogicalTree;
using DevProjex.Avalonia.Services;
using ThemeEffectMode = DevProjex.Infrastructure.ThemePresets.ThemeEffectMode;

namespace DevProjex.Avalonia.Coordinators;

public sealed class ThemeBrushCoordinator(Window window, MainWindowViewModel viewModel, Func<Menu?> menuProvider)
    : IDisposable
{
    // Reusable brushes - mutate Color instead of allocating new instances
    private SolidColorBrush _currentMenuBrush = new(Colors.Black);
    private SolidColorBrush _currentMenuChildBrush = new(Colors.Black);
    private SolidColorBrush _currentMenuHoverBrush = new(Colors.Gray);
    private SolidColorBrush _currentMenuPressedBrush = new(Colors.DimGray);
    private SolidColorBrush _currentMenuChildHoverBrush = new(Colors.Gray);
    private SolidColorBrush _currentMenuChildPressedBrush = new(Colors.DimGray);
    private SolidColorBrush _currentBorderBrush = new(Colors.Gray);
    private readonly SolidColorBrush _transparencyFallbackBrush = new(Colors.Black);
    private SolidColorBrush? _backgroundBrush;
    private SolidColorBrush? _panelBrush;
    private SolidColorBrush? _mainMenuStripBrush;
    private SolidColorBrush? _mainMenuPopupBrush;
    private SolidColorBrush? _accentBrush;
    private int _dynamicUpdateScheduled;
    private bool _disposed;

    public void HandleSubmenuOpened(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not MenuItem menuItem)
            return;

        window.Dispatcher.Post(() =>
        {
            // Guard: if the element is already detached from the visual tree (menu/window closed), do nothing.
            if (TopLevel.GetTopLevel(menuItem) is null)
                return;

            ApplyBrushesToMenuItemPopup(menuItem);

            // Nested menus: apply recursively, but it effectively updates only popups that are currently IsOpen (see below).
            foreach (var child in menuItem.GetVisualDescendants().OfType<MenuItem>())
            {
                ApplyBrushesToMenuItemPopup(child);
            }
        }, DispatcherPriority.Loaded);
    }

    public void UpdateTransparencyEffect()
    {
        CompositionBackdropCornerRadiusCoordinator.UseSharpCornersForDecoratedWindow();
        UpdateDynamicThemeBrushes();

        if (!viewModel.HasAnyEffect)
        {
            window.TransparencyLevelHint =
            [
                WindowTransparencyLevel.None
            ];

            return;
        }

        if (viewModel.IsMicaEnabled)
        {
            window.TransparencyLevelHint =
            [
                WindowTransparencyLevel.Mica,
                WindowTransparencyLevel.None
            ];

            return;
        }

        if (viewModel.IsAcrylicEnabled)
        {
            window.TransparencyLevelHint =
            [
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Blur,
                WindowTransparencyLevel.None
            ];

            return;
        }

        window.TransparencyLevelHint =
        [
            WindowTransparencyLevel.Transparent,
            WindowTransparencyLevel.None
        ];

    }

    public void ScheduleDynamicThemeBrushUpdate()
    {
        if (_disposed || Interlocked.Exchange(ref _dynamicUpdateScheduled, 1) != 0)
            return;

        window.Dispatcher.Post(() =>
        {
            if (Interlocked.Exchange(ref _dynamicUpdateScheduled, 0) != 0 && !_disposed)
                UpdateDynamicThemeBrushes();
        }, DispatcherPriority.Render);
    }

    public void UpdateDynamicThemeBrushes()
    {
        Volatile.Write(ref _dynamicUpdateScheduled, 0);
        if (_disposed)
            return;

        if (global::Avalonia.Application.Current is not { } app)
            return;

        var theme = app.ActualThemeVariant ?? ThemeVariant.Dark;
        var isDark = theme == ThemeVariant.Dark;
        var effect = !viewModel.HasAnyEffect
            ? ThemeEffectMode.Solid
            : viewModel.IsMicaEnabled
                ? ThemeEffectMode.Mica
                : viewModel.IsAcrylicEnabled
                    ? ThemeEffectMode.Acrylic
                    : ThemeEffectMode.Transparent;
        var palette = ThemePaletteCalculator.Calculate(
            isDark,
            effect,
            viewModel.MaterialIntensity,
            viewModel.PanelContrast,
            viewModel.MenuChildIntensity,
            viewModel.BorderStrength);

        // Mutate existing brush colors instead of allocating new instances
        var bgColor = palette.Background;
        _backgroundBrush ??= new SolidColorBrush(bgColor);
        _backgroundBrush.Color = bgColor;
        UpdateResource("AppBackgroundBrush", _backgroundBrush);

        var panelColor = palette.Panel;
        _panelBrush ??= new SolidColorBrush(panelColor);
        _panelBrush.Color = panelColor;
        UpdateResource("AppPanelBrush", _panelBrush);

        var mainMenuStripColor = palette.MainMenuStrip;
        _mainMenuStripBrush ??= new SolidColorBrush(mainMenuStripColor);
        _mainMenuStripBrush.Color = mainMenuStripColor;
        UpdateResource("MainMenuStripBrush", _mainMenuStripBrush);

        var mainMenuPopupColor = palette.MainMenuPopup;
        _mainMenuPopupBrush ??= new SolidColorBrush(mainMenuPopupColor);
        _mainMenuPopupBrush.Color = mainMenuPopupColor;
        UpdateResource("MainMenuPopupBrush", _mainMenuPopupBrush);

        var menuColor = palette.Menu;
        _currentMenuBrush.Color = menuColor;
        UpdateResource("MenuPopupBrush", _currentMenuBrush);

        var menuChildColor = palette.MenuChild;
        _currentMenuChildBrush.Color = menuChildColor;
        UpdateResource("MenuChildPopupBrush", _currentMenuChildBrush);

        _currentMenuHoverBrush.Color = palette.MenuHover;
        _currentMenuPressedBrush.Color = palette.MenuPressed;
        _currentMenuChildHoverBrush.Color = palette.MenuHover;
        _currentMenuChildPressedBrush.Color = palette.MenuPressed;

        UpdateResource("MenuHoverBrush", _currentMenuHoverBrush);
        UpdateResource("MenuPressedBrush", _currentMenuPressedBrush);
        UpdateResource("MenuChildHoverBrush", _currentMenuChildHoverBrush);
        UpdateResource("MenuChildPressedBrush", _currentMenuChildPressedBrush);

        _currentBorderBrush.Color = palette.Border;
        UpdateResource("AppBorderBrush", _currentBorderBrush);

        _accentBrush ??= new SolidColorBrush(palette.Accent);
        _accentBrush.Color = palette.Accent;
        UpdateResource("AppAccentBrush", _accentBrush);

        _transparencyFallbackBrush.Color = palette.TransparencyFallback;
        window.TransparencyBackgroundFallback = _transparencyFallbackBrush;

        ApplyMenuBrushesDirect();
    }

    public void ApplyMenuBrushesDirect()
    {
        var mainMenu = menuProvider();
        if (mainMenu is null) return;

        foreach (var menuItem in mainMenu.GetLogicalDescendants().OfType<MenuItem>())
        {
            UpdateMenuItemPopup(menuItem);
        }
    }

    private void ApplyBrushesToMenuItemPopup(MenuItem menuItem)
    {
        foreach (var popup in menuItem.GetVisualDescendants().OfType<Popup>().Where(p => p.IsOpen))
        {
            PopupBackdropConfigurator.TryApply(
                popup.Child,
                window,
                viewModel.HasAnyEffect,
                PopupBackdropTransparencyFallback.Transparent);

            if (popup.Child is not Border border)
                continue;

            border.Background = _mainMenuPopupBrush;
            border.BorderBrush = _currentBorderBrush;
            border.BorderThickness = new Thickness(1);
            border.CornerRadius = new CornerRadius(8);
            border.ClipToBounds = true;
            border.BoxShadow = default;
            border.Padding = new Thickness(4);
        }
    }

    private void UpdateMenuItemPopup(MenuItem menuItem)
    {
        var popup = menuItem.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
        if (popup?.Child is Border border)
        {
            border.Background = _mainMenuPopupBrush;
            border.BorderBrush = _currentBorderBrush;
        }

        foreach (var subItem in menuItem.GetLogicalDescendants().OfType<MenuItem>())
        {
            var subPopup = subItem.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
            if (subPopup?.Child is Border subBorder)
            {
                subBorder.Background = _mainMenuPopupBrush;
                subBorder.BorderBrush = _currentBorderBrush;
            }
        }
    }

    private void UpdateResource(string key, object value)
    {
        var app = global::Avalonia.Application.Current;

        if (app?.Resources is not null)
        {
            try
            {
                app.Resources[key] = value;
            }
            catch
            {
                // Ignore errors
            }
        }

        try
        {
            window.Resources[key] = value;
        }
        catch
        {
            // Ignore errors
        }
    }

    public void Dispose()
    {
        _disposed = true;
        // Null out brush references to break any resource dictionary ties
        _backgroundBrush = null;
        _panelBrush = null;
        _mainMenuStripBrush = null;
        _mainMenuPopupBrush = null;
        _accentBrush = null;
    }
}
