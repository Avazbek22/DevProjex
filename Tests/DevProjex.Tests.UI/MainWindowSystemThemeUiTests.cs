using DevProjex.Infrastructure.ThemePresets;
using DevProjex.Avalonia.Views;
using AvaloniaThemeVariant = Avalonia.Styling.ThemeVariant;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowSystemThemeUiTests
{
    [AvaloniaFact]
    public async Task FactoryDefaultAndManualRoundTrip_KeepSystemAsARealSelectionMode()
    {
        using var project = UiTestProject.CreateDefault();
        var appDataPath = Path.Combine(project.AppDataPath, "system-theme-round-trip");
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            project,
            waitForInitialSettingsPane: false,
            appDataPathOverride: appDataPath);
        var closed = false;

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);

            Assert.Equal(ThemeSelectionMode.System, viewModel.SelectedThemeMode);
            Assert.True(viewModel.IsSystemThemeSelected);
            Assert.Equal(
                AvaloniaThemeVariant.Default,
                global::Avalonia.Application.Current!.RequestedThemeVariant);

            await RaiseThemeSelectionClickAsync(window, "DarkThemeCheckBox");

            Assert.Equal(ThemeSelectionMode.Dark, viewModel.SelectedThemeMode);
            Assert.True(viewModel.IsDarkThemeSelected);
            Assert.True(viewModel.IsDarkTheme);
            Assert.Equal(
                AvaloniaThemeVariant.Dark,
                global::Avalonia.Application.Current.RequestedThemeVariant);

            await RaiseThemeSelectionClickAsync(window, "SystemThemeCheckBox");

            Assert.Equal(ThemeSelectionMode.System, viewModel.SelectedThemeMode);
            Assert.True(viewModel.IsSystemThemeSelected);
            Assert.Equal(
                AvaloniaThemeVariant.Default,
                global::Avalonia.Application.Current.RequestedThemeVariant);

            await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
            closed = true;

            var persisted = new ThemeSettingsStore(() => appDataPath).Load();
            Assert.Equal(ThemeSelectionMode.System, persisted.SelectedThemeMode);
            Assert.Equal(ThemeEffectMode.Solid, persisted.LightThemeEffect);
            Assert.Equal(ThemeEffectMode.Acrylic, persisted.DarkThemeEffect);
        }
        finally
        {
            if (!closed)
                await UiTestDriver.CloseWindowAsync(window);
        }
    }

    private static async Task RaiseThemeSelectionClickAsync(
        MainWindow window,
        string controlName)
    {
        var popover = UiTestDriver.GetRequiredTopMenuControl<ThemePopoverView>(window, "ThemePopover");
        var checkBox = Assert.IsType<CheckBox>(popover.FindControl<CheckBox>(controlName));
        checkBox.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
    }
}
