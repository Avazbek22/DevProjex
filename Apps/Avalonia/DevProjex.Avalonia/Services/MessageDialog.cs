namespace DevProjex.Avalonia.Services;

public static class MessageDialog
{
    public static async Task ShowAsync(Window owner, string title, string message)
    {
        var themeVariant = DialogSurfaceFactory.ResolveThemeVariant(owner);
        var brushes = DialogSurfaceFactory.ResolveBrushes(owner, themeVariant);
        var dialog = DialogSurfaceFactory.CreateWindow(
            title,
            themeVariant,
            brushes,
            BuildContent(message),
            width: 420,
            height: 200);

        if (owner is not null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
    }

    public static async Task<bool> ShowConfirmationAsync(
        Window owner,
        string title,
        string message,
        string confirmButtonText = "Да",
        string cancelButtonText = "Отмена",
        double height = 260)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var themeVariant = DialogSurfaceFactory.ResolveThemeVariant(owner);
        var brushes = DialogSurfaceFactory.ResolveBrushes(owner, themeVariant);
        var dialog = DialogSurfaceFactory.CreateWindow(
            title,
            themeVariant,
            brushes,
            BuildConfirmationContent(message, confirmButtonText, cancelButtonText, completion),
            width: 520,
            height: height);

        dialog.Closed += (_, _) => completion.TrySetResult(false);

        if (owner is not null)
            _ = dialog.ShowDialog(owner);
        else
            dialog.Show();

        return await completion.Task.ConfigureAwait(false);
    }

    private static Control BuildContent(string message)
    {
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12),
            VerticalAlignment = VerticalAlignment.Center
        };

        var button = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12),
            Width = 80
        };

        var panel = new DockPanel();
        DockPanel.SetDock(button, Dock.Bottom);

        panel.Children.Add(button);
        panel.Children.Add(text);

        button.Click += (_, _) =>
            (TopLevel.GetTopLevel(panel) as Window)?.Close();

        return panel;
    }

    private static Control BuildConfirmationContent(
        string message,
        string confirmButtonText,
        string cancelButtonText,
        TaskCompletionSource<bool> completion)
    {
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12),
            VerticalAlignment = VerticalAlignment.Center
        };

        var confirmButton = new Button
        {
            Content = confirmButtonText,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 12, 6, 12),
            MinWidth = 110,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        var cancelButton = new Button
        {
            Content = cancelButtonText,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(6, 12, 12, 12),
            MinWidth = 110,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        cancelButton.Classes.Add("primary-action");

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttonPanel.Children.Add(confirmButton);
        buttonPanel.Children.Add(cancelButton);

        var panel = new DockPanel();
        DockPanel.SetDock(buttonPanel, Dock.Bottom);

        panel.Children.Add(buttonPanel);
        panel.Children.Add(text);

        confirmButton.Click += (_, _) =>
        {
            completion.TrySetResult(true);
            (TopLevel.GetTopLevel(panel) as Window)?.Close();
        };

        cancelButton.Click += (_, _) =>
        {
            completion.TrySetResult(false);
            (TopLevel.GetTopLevel(panel) as Window)?.Close();
        };

        return panel;
    }
}
