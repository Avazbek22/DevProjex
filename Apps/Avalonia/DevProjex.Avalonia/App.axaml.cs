using Avalonia.Controls.ApplicationLifetimes;
using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia;

public sealed class App : global::Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = CommandLineOptions.Parse(desktop.Args ?? Array.Empty<string>());
            var services = AvaloniaCompositionRoot.CreateDefault(options);
            desktop.MainWindow = new MainWindow(options, services);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
