using Avalonia.LogicalTree;

namespace DevProjex.Avalonia.Coordinators;

public sealed class ThemeBrushCoordinator : IDisposable
{
    private readonly Window _window;
    private readonly MainWindowViewModel _viewModel;
    private readonly Func<Menu?> _menuProvider;

    // Reusable brushes - mutate Color instead of allocating new instances
    private SolidColorBrush _currentMenuBrush = new(Colors.Black);
    private SolidColorBrush _currentMenuChildBrush = new(Colors.Black);
    private SolidColorBrush _currentMenuHoverBrush = new(Colors.Gray);
    private SolidColorBrush _currentMenuPressedBrush = new(Colors.DimGray);
    private SolidColorBrush _currentMenuChildHoverBrush = new(Colors.Gray);
    private SolidColorBrush _currentMenuChildPressedBrush = new(Colors.DimGray);
    private SolidColorBrush _currentBorderBrush = new(Colors.Gray);
    private SolidColorBrush? _backgroundBrush;
    private SolidColorBrush? _panelBrush;
    private SolidColorBrush? _accentBrush;

    public ThemeBrushCoordinator(Window window, MainWindowViewModel viewModel, Func<Menu?> menuProvider)
    {
        _window = window;
        _viewModel = viewModel;
        _menuProvider = menuProvider;
    }

    public void HandleSubmenuOpened(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not MenuItem menuItem)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            // Guard: if the element is already detached from the visual tree (menu/window closed), do nothing.
            if (menuItem.GetVisualRoot() is null)
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
        if (!_viewModel.HasAnyEffect)
        {
            _window.TransparencyLevelHint = new[]
            {
                WindowTransparencyLevel.None
            };

            UpdateDynamicThemeBrushes();
            return;
        }

        if (_viewModel.IsMicaEnabled)
        {
            _window.TransparencyLevelHint = new[]
            {
                WindowTransparencyLevel.Mica,
                WindowTransparencyLevel.Blur,
                WindowTransparencyLevel.None
            };

            UpdateDynamicThemeBrushes();
            return;
        }

        if (_viewModel.IsAcrylicEnabled)
        {
            _window.TransparencyLevelHint = new[]
            {
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Blur,
                WindowTransparencyLevel.Transparent,
                WindowTransparencyLevel.None
            };

            UpdateDynamicThemeBrushes();
            return;
        }

        var blur = Math.Clamp(_viewModel.BlurRadius / 100.0, 0.0, 1.0);

        if (blur <= 0.0001)
        {
            _window.TransparencyLevelHint = new[]
            {
                WindowTransparencyLevel.Transparent,
                WindowTransparencyLevel.None
            };
        }
        else
        {
            _window.TransparencyLevelHint = new[]
            {
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Blur,
                WindowTransparencyLevel.Transparent,
                WindowTransparencyLevel.None
            };
        }

        UpdateDynamicThemeBrushes();
    }

    public void UpdateDynamicThemeBrushes()
    {
        if (global::Avalonia.Application.Current is not { } app)
            return;

        var theme = app.ActualThemeVariant ?? ThemeVariant.Dark;
        var isDark = theme == ThemeVariant.Dark;

        var baseBg = isDark ? Color.Parse("#121214") : Color.Parse("#FFFFFF");
        var basePanel = isDark ? Color.Parse("#17171A") : Color.Parse("#F3F3F3");

        var material = Math.Clamp(_viewModel.MaterialIntensity / 100.0, 0.0, 1.0);
        var contrast = Math.Clamp(_viewModel.PanelContrast / 100.0, 0.0, 1.0);
        var borderStrength = Math.Clamp(_viewModel.BorderStrength / 100.0, 0.0, 1.0);
        var menuChild = Math.Clamp(_viewModel.MenuChildIntensity / 100.0, 0.0, 1.0);
        var blur = Math.Clamp(_viewModel.BlurRadius / 100.0, 0.0, 1.0);

        Color bgBase = baseBg;
        Color panelBase = basePanel;

        byte bgAlpha;
        byte panelAlpha;
        byte borderAlpha;
        byte menuAlpha;
        byte menuChildAlpha = 255;
        Color menuBase = panelBase;
        Color menuChildBase = panelBase;
        if (!_viewModel.HasAnyEffect)
        {
            bgAlpha = 255;
            panelAlpha = 255;
            menuAlpha = 255;
            menuChildAlpha = 255;
        }
        else if (_viewModel.IsMicaEnabled)
        {
            var micaStrength = Math.Pow(material, 0.7);

            bgAlpha = (byte)Math.Round(255 * (1.0 - (micaStrength * 0.9)));

            var panelMinAlpha = bgAlpha;
            var panelMaxAlpha = 170 + (contrast * 70);
            panelAlpha = (byte)Math.Clamp(
                panelMinAlpha + (panelMaxAlpha - panelMinAlpha) * contrast - (micaStrength * 60),
                panelMinAlpha,
                255);

            menuAlpha = (byte)Math.Clamp(panelAlpha + 35, 160, 255);
            menuChildAlpha = (byte)Math.Clamp(menuAlpha - (menuChild * 40), 140, 255);

            if (isDark)
            {
                bgBase = Color.Parse("#0D0E10");
                panelBase = Color.Parse("#14161A");
            }
            else
            {
                bgBase = Color.Parse("#FFFFFF");
                panelBase = Color.Parse("#F7F7F7");
            }
        }
        else if (_viewModel.IsAcrylicEnabled)
        {
            bgAlpha = (byte)Math.Round(240 - (material * 200));
            panelAlpha = (byte)Math.Round(235 - (material * 150));

            panelAlpha = (byte)Math.Clamp(panelAlpha + (contrast * 40), 70, 255);

            menuAlpha = (byte)Math.Clamp(panelAlpha + 30, 150, 255);
            menuChildAlpha = (byte)Math.Clamp(menuAlpha - (menuChild * 40), 130, 255);
        }
        else
        {
            bgAlpha = (byte)Math.Round(255 * (1.0 - material));

            var blurVisibility = Math.Pow(blur, 2.2);

            var panelBaseAlpha = 90 + (contrast * 130);
            panelAlpha = (byte)Math.Clamp(panelBaseAlpha + (blurVisibility * 25), 70, 255);

            menuAlpha = (byte)Math.Clamp(panelAlpha + 45, 170, 255);
            menuChildAlpha = (byte)Math.Clamp(menuAlpha - (menuChild * 40), 150, 255);
        }

        if (_viewModel.HasAnyEffect)
        {
            // Keep window surface denser than content islands and preserve visible submenu contrast response.
            bgAlpha = (byte)Math.Clamp(bgAlpha + 22, 90, 255);

            const int minAlphaGap = 12;
            var maxPanelAlpha = Math.Max(60, bgAlpha - minAlphaGap);
            panelAlpha = (byte)Math.Clamp(panelAlpha, 60, maxPanelAlpha);

            if (isDark)
            {
                menuAlpha = (byte)Math.Clamp(panelAlpha + 28 + (contrast * 16), 120, 255);
                var submenuDelta = 10 + (menuChild * 80);
                menuChildAlpha = (byte)Math.Clamp(menuAlpha - submenuDelta, 45, 255);
            }
            else
            {
                // Light theme requires lower alpha and subtle cool tint to make blur/material visible.
                menuAlpha = (byte)Math.Clamp(panelAlpha + 12 + (contrast * 8), 96, 215);
                var submenuDelta = 12 + (menuChild * 72);
                menuChildAlpha = (byte)Math.Clamp(menuAlpha - submenuDelta, 72, 205);

                menuBase = Color.Parse("#F8FBFF");
                menuChildBase = Color.Parse("#F2F7FD");
            }
        }

        borderAlpha = (byte)Math.Round(255 * borderStrength);

        // Mutate existing brush colors instead of allocating new instances
        var bgColor = Color.FromArgb(bgAlpha, bgBase.R, bgBase.G, bgBase.B);
        _backgroundBrush ??= new SolidColorBrush(bgColor);
        _backgroundBrush.Color = bgColor;
        UpdateResource("AppBackgroundBrush", _backgroundBrush);

        var panelColor = Color.FromArgb(panelAlpha, panelBase.R, panelBase.G, panelBase.B);
        _panelBrush ??= new SolidColorBrush(panelColor);
        _panelBrush.Color = panelColor;
        UpdateResource("AppPanelBrush", _panelBrush);

        var menuColor = Color.FromArgb(menuAlpha, menuBase.R, menuBase.G, menuBase.B);
        _currentMenuBrush.Color = menuColor;
        UpdateResource("MenuPopupBrush", _currentMenuBrush);

        var menuChildColor = Color.FromArgb(menuChildAlpha, menuChildBase.R, menuChildBase.G, menuChildBase.B);
        _currentMenuChildBrush.Color = menuChildColor;
        UpdateResource("MenuChildPopupBrush", _currentMenuChildBrush);

        var hoverColor = isDark ? Color.Parse("#343B46") : Color.Parse("#DCE7F4");
        var pressedColor = isDark ? Color.Parse("#3B4452") : Color.Parse("#CFDDF0");

        _currentMenuHoverBrush.Color = hoverColor;
        _currentMenuPressedBrush.Color = pressedColor;
        _currentMenuChildHoverBrush.Color = hoverColor;
        _currentMenuChildPressedBrush.Color = pressedColor;

        UpdateResource("MenuHoverBrush", _currentMenuHoverBrush);
        UpdateResource("MenuPressedBrush", _currentMenuPressedBrush);
        UpdateResource("MenuChildHoverBrush", _currentMenuChildHoverBrush);
        UpdateResource("MenuChildPressedBrush", _currentMenuChildPressedBrush);

        var borderBase = isDark ? Color.Parse("#505050") : Color.Parse("#C0C0C0");
        var borderColor = Color.FromArgb(borderAlpha, borderBase.R, borderBase.G, borderBase.B);
        _currentBorderBrush.Color = borderColor;
        UpdateResource("AppBorderBrush", _currentBorderBrush);

        var accentColor = isDark ? Color.Parse("#2D8CFF") : Color.Parse("#0078D4");
        _accentBrush ??= new SolidColorBrush(accentColor);
        _accentBrush.Color = accentColor;
        UpdateResource("AppAccentBrush", _accentBrush);

        ApplyMenuBrushesDirect();
    }

    public void ApplyMenuBrushesDirect()
    {
        var mainMenu = _menuProvider();
        if (mainMenu is null) return;

        foreach (var menuItem in mainMenu.GetLogicalDescendants().OfType<MenuItem>())
        {
            UpdateMenuItemPopup(menuItem);
        }
    }

    private void ApplyBrushesToMenuItemPopup(MenuItem menuItem)
    {
        var isChildMenu = menuItem.Parent is MenuItem;

        foreach (var popup in menuItem.GetVisualDescendants().OfType<Popup>().Where(p => p.IsOpen))
        {
            ApplyPopupHostEffect(popup);

            if (popup.Child is not Border border)
                continue;

            border.Background = isChildMenu ? _currentMenuChildBrush : _currentMenuBrush;
            border.BorderBrush = _currentBorderBrush;
            border.BorderThickness = new Thickness(1);
            border.CornerRadius = new CornerRadius(8);
            border.Padding = new Thickness(4);
        }
    }

    private void ApplyPopupHostEffect(Popup popup)
    {
        if (!popup.IsOpen)
            return;

        if (popup.Child is null)
            return;

        if (popup.Child.GetVisualRoot() is null)
            return;

        if (TopLevel.GetTopLevel(popup.Child) is not TopLevel topLevel)
            return;

        if (ReferenceEquals(topLevel, _window))
            return;

        try
        {
            if (_viewModel.HasAnyEffect)
            {
                topLevel.TransparencyLevelHint = new[]
                {
                    WindowTransparencyLevel.AcrylicBlur,
                    WindowTransparencyLevel.Blur,
                    WindowTransparencyLevel.Transparent,
                    WindowTransparencyLevel.None
                };

                topLevel.Background = Brushes.Transparent;
            }
            else
            {
                topLevel.TransparencyLevelHint = new[]
                {
                    WindowTransparencyLevel.None
                };
            }
        }
        catch
        {
            // Ignore: popup could have closed.
        }
    }

    private void UpdateMenuItemPopup(MenuItem menuItem)
    {
        var isChildMenu = menuItem.Parent is MenuItem;

        var popup = menuItem.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
        if (popup?.Child is Border border)
        {
            border.Background = isChildMenu ? _currentMenuChildBrush : _currentMenuBrush;
            border.BorderBrush = _currentBorderBrush;
        }

        foreach (var subItem in menuItem.GetLogicalDescendants().OfType<MenuItem>())
        {
            var subPopup = subItem.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
            if (subPopup?.Child is Border subBorder)
            {
                subBorder.Background = _currentMenuChildBrush;
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
            _window.Resources[key] = value;
        }
        catch
        {
            // Ignore errors
        }
    }

    public void Dispose()
    {
        // Null out brush references to break any resource dictionary ties
        _backgroundBrush = null;
        _panelBrush = null;
        _accentBrush = null;
    }
}
