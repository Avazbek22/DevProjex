using System.Reflection;
using DevProjex.Infrastructure.ThemePresets;
using AvaloniaThemeVariant = Avalonia.Styling.ThemeVariant;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowSystemThemeUiTests
{
    [AvaloniaFact]
    public async Task FactoryDefaultAndManualRoundTrip_KeepSystemAsARealSelectionMode()
    {
        using var project = UiTestProject.CreateDefault();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            project,
            waitForInitialSettingsPane: false);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);

            Assert.Equal(ThemeSelectionMode.System, viewModel.SelectedThemeMode);
            Assert.True(viewModel.IsSystemThemeSelected);
            Assert.Equal(
                AvaloniaThemeVariant.Default,
                global::Avalonia.Application.Current!.RequestedThemeVariant);

            InvokeThemeAction(window, "OnSetDarkTheme");

            Assert.Equal(ThemeSelectionMode.Dark, viewModel.SelectedThemeMode);
            Assert.True(viewModel.IsDarkThemeSelected);
            Assert.True(viewModel.IsDarkTheme);
            Assert.Equal(
                AvaloniaThemeVariant.Dark,
                global::Avalonia.Application.Current.RequestedThemeVariant);

            InvokeThemeAction(window, "OnSetSystemTheme");

            Assert.Equal(ThemeSelectionMode.System, viewModel.SelectedThemeMode);
            Assert.True(viewModel.IsSystemThemeSelected);
            Assert.Equal(
                AvaloniaThemeVariant.Default,
                global::Avalonia.Application.Current.RequestedThemeVariant);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    private static void InvokeThemeAction(MainWindow window, string methodName)
    {
        var method = typeof(MainWindow).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(window, [null, new global::Avalonia.Interactivity.RoutedEventArgs()]);
    }
}
