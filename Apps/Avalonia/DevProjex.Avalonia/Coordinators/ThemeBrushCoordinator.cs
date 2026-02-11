using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Styling;
using DevProjex.Avalonia.ViewModels;

namespace DevProjex.Avalonia.Coordinators;

public sealed class ThemeBrushCoordinator
{
    private readonly Window _window;
    private readonly MainWindowViewModel _viewModel;
    private readonly Func<Menu?> _menuProvider;

    private SolidColorBrush _currentMenuBrush = new(Colors.Black);
    private SolidColorBrush _currentMenuChildBrush = new(Colors.Black);
    private SolidColorBrush _currentBorderBrush = new(Colors.Gray);

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

        borderAlpha = (byte)Math.Round(255 * borderStrength);

        var bgColor = Color.FromArgb(bgAlpha, bgBase.R, bgBase.G, bgBase.B);
        var backgroundBrush = new SolidColorBrush(bgColor);
        UpdateResource("AppBackgroundBrush", backgroundBrush);

        var panelColor = Color.FromArgb(panelAlpha, panelBase.R, panelBase.G, panelBase.B);
        var panelBrush = new SolidColorBrush(panelColor);
        UpdateResource("AppPanelBrush", panelBrush);

        var menuColor = Color.FromArgb(menuAlpha, panelBase.R, panelBase.G, panelBase.B);
        _currentMenuBrush = new SolidColorBrush(menuColor);
        UpdateResource("MenuPopupBrush", _currentMenuBrush);

        var menuChildColor = Color.FromArgb(menuChildAlpha, panelBase.R, panelBase.G, panelBase.B);
        _currentMenuChildBrush = new SolidColorBrush(menuChildColor);
        UpdateResource("MenuChildPopupBrush", _currentMenuChildBrush);

        var borderBase = isDark ? Color.Parse("#505050") : Color.Parse("#C0C0C0");
        var borderColor = Color.FromArgb(borderAlpha, borderBase.R, borderBase.G, borderBase.B);
        _currentBorderBrush = new SolidColorBrush(borderColor);
        UpdateResource("AppBorderBrush", _currentBorderBrush);

        var accentColor = isDark ? Color.Parse("#2D8CFF") : Color.Parse("#0078D4");
        UpdateResource("AppAccentBrush", new SolidColorBrush(accentColor));

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
}
