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
    private readonly HashSet<string> _publishedResourceKeys = new(StringComparer.Ordinal);
    private int _dynamicUpdateScheduled;
    private bool _disposed;

    public void HandleSubmenuOpened(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not MenuItem menuItem)
            return;

        var mainMenu = menuProvider();
        if (mainMenu is null || !menuItem.GetLogicalAncestors().Contains(mainMenu))
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
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Blur,
                WindowTransparencyLevel.Transparent,
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
                WindowTransparencyLevel.Transparent,
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
        var effect = viewModel.ActiveThemeEffect;
        var palette = ThemePaletteCalculator.Calculate(
            isDark,
            effect,
            viewModel.MaterialIntensity,
            viewModel.PanelContrast,
            viewModel.MenuTransparency,
            viewModel.BorderStrength);

        // Mutate existing brush colors instead of allocating new instances
        UpdateBrushResource("AppBackgroundBrush", ref _backgroundBrush, palette.Background);
        UpdateBrushResource("AppPanelBrush", ref _panelBrush, palette.Panel);
        UpdateBrushResource("MainMenuStripBrush", ref _mainMenuStripBrush, palette.MainMenuStrip);
        var mainMenuPopupPublished = UpdateBrushResource(
            "MainMenuPopupBrush",
            ref _mainMenuPopupBrush,
            palette.MainMenuPopup);
        UpdateBrushResource("MenuPopupBrush", _currentMenuBrush, palette.Menu);
        UpdateBrushResource("MenuChildPopupBrush", _currentMenuChildBrush, palette.MenuChild);
        UpdateBrushResource("MenuHoverBrush", _currentMenuHoverBrush, palette.MenuHover);
        UpdateBrushResource("MenuPressedBrush", _currentMenuPressedBrush, palette.MenuPressed);
        UpdateBrushResource("MenuChildHoverBrush", _currentMenuChildHoverBrush, palette.MenuHover);
        UpdateBrushResource("MenuChildPressedBrush", _currentMenuChildPressedBrush, palette.MenuPressed);
        var borderPublished = UpdateBrushResource("AppBorderBrush", _currentBorderBrush, palette.Border);
        UpdateBrushResource("AppAccentBrush", ref _accentBrush, palette.Accent);

        if (_transparencyFallbackBrush.Color != palette.TransparencyFallback)
            _transparencyFallbackBrush.Color = palette.TransparencyFallback;
        if (!ReferenceEquals(window.TransparencyBackgroundFallback, _transparencyFallbackBrush))
            window.TransparencyBackgroundFallback = _transparencyFallbackBrush;

        if (mainMenuPopupPublished || borderPublished)
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
                viewModel.ActiveThemeEffect,
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

    private bool UpdateBrushResource(string key, ref SolidColorBrush? brush, Color color)
    {
        brush ??= new SolidColorBrush(color);
        return UpdateBrushResource(key, brush, color);
    }

    private bool UpdateBrushResource(string key, SolidColorBrush brush, Color color)
    {
        var colorChanged = brush.Color != color;
        if (colorChanged)
            brush.Color = color;

        var published = _publishedResourceKeys.Add(key);
        if (published)
            UpdateResource(key, brush);

        return published;
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
        _publishedResourceKeys.Clear();
    }
}
