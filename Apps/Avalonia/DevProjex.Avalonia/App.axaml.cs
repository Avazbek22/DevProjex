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
            var parseResult = CommandLineOptions.Parse(desktop.Args ?? []);
            var services = AvaloniaCompositionRoot.CreateDefault(parseResult.Options);
            desktop.MainWindow = new MainWindow(parseResult.Options, services, parseResult.Errors);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
