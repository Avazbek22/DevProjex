using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Avalonia.Services;

internal static class StoreMigrationRecoveryWindow
{
    internal static Window Create(
        LocalizationService localization,
        Func<StoreUserDataMigrationStatus> retryMigration,
        Action onReady,
        Action onExit)
    {
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(retryMigration);
        ArgumentNullException.ThrowIfNull(onReady);
        ArgumentNullException.ThrowIfNull(onExit);

        var message = new TextBlock
        {
            Text = localization["Dialog.StoreMigration.Message"],
            TextWrapping = TextWrapping.Wrap
        };
        var retry = new Button
        {
            Name = "StoreMigrationRetryButton",
            Content = localization["Terminal.Tui.Retry"],
            MinWidth = 100
        };
        var exit = new Button
        {
            Name = "StoreMigrationExitButton",
            Content = localization["Menu.File.Exit"],
            MinWidth = 100
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        buttons.Children.Add(retry);
        buttons.Children.Add(exit);
        var content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 20
        };
        content.Children.Add(message);
        content.Children.Add(buttons);

        var themeVariant = DialogSurfaceFactory.ResolveThemeVariant(owner: null);
        var brushes = DialogSurfaceFactory.ResolveBrushes(owner: null, themeVariant);
        var window = DialogSurfaceFactory.CreateWindow(
            localization["Dialog.StoreMigration.Title"],
            themeVariant,
            brushes,
            content,
            width: 560,
            height: null);
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var ready = false;
        var exiting = false;
        window.Closed += (_, _) =>
        {
            if (!ready && !exiting)
            {
                exiting = true;
                onExit();
            }
        };
        exit.Click += (_, _) => window.Close();
        retry.Click += async (_, _) =>
        {
            retry.IsEnabled = false;
            exit.IsEnabled = false;
            StoreUserDataMigrationStatus status;
            try
            {
                status = await Task.Run(retryMigration);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Trace.TraceWarning("Store migration retry failed: {0}", exception.GetType().Name);
                if (!exiting)
                {
                    retry.IsEnabled = true;
                    exit.IsEnabled = true;
                }
                return;
            }
            if (exiting)
                return;
            if (!StoreUserDataMigrationAdmission.IsReady(status))
            {
                retry.IsEnabled = true;
                exit.IsEnabled = true;
                return;
            }

            try
            {
                onReady();
                ready = true;
                window.Close();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Trace.TraceError("Store migration admission could not open the application: {0}", exception.GetType().Name);
                if (!exiting)
                {
                    exiting = true;
                    onExit();
                }
            }
        };
        return window;
    }
}
