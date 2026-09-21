namespace DevProjex.Avalonia.Services;

public static class MessageDialog
{
    public static async Task<int> ShowChoiceAsync(
        Window owner,
        string title,
        string message,
        params string[] choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentOutOfRangeException.ThrowIfLessThan(choices.Length, 2);
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var themeVariant = DialogSurfaceFactory.ResolveThemeVariant(owner);
        var brushes = DialogSurfaceFactory.ResolveBrushes(owner, themeVariant);
        var dialog = DialogSurfaceFactory.CreateWindow(
            title,
            themeVariant,
            brushes,
            BuildChoiceContent(message, choices, completion),
            width: 560,
            height: 280);
        dialog.Closed += (_, _) => completion.TrySetResult(0);
        _ = dialog.ShowDialog(owner);
        return await completion.Task.ConfigureAwait(false);
    }
    public static async Task ShowAsync(
        Window owner,
        string title,
        string message,
        string closeButtonText,
        double height = 200)
    {
        var themeVariant = DialogSurfaceFactory.ResolveThemeVariant(owner);
        var brushes = DialogSurfaceFactory.ResolveBrushes(owner, themeVariant);
        var dialog = DialogSurfaceFactory.CreateWindow(
            title,
            themeVariant,
            brushes,
            BuildContent(message, closeButtonText),
            width: 420,
            height: height);

        if (owner is not null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
    }

    public static Task<bool> ShowConfirmationAsync(
        Window owner,
        string title,
        string message,
        string confirmButtonText,
        string cancelButtonText,
        double width = 520,
        double height = 260) =>
        ShowConfirmationCoreAsync(
            owner,
            title,
            message,
            confirmButtonText,
            cancelButtonText,
            width,
            height,
            fitContentHeight: false);

    internal static Task<bool> ShowContentSizedConfirmationAsync(
        Window owner,
        string title,
        string message,
        string confirmButtonText,
        string cancelButtonText,
        double width = 520) =>
        ShowConfirmationCoreAsync(
            owner,
            title,
            message,
            confirmButtonText,
            cancelButtonText,
            width,
            height: 0,
            fitContentHeight: true);

    private static async Task<bool> ShowConfirmationCoreAsync(
        Window owner,
        string title,
        string message,
        string confirmButtonText,
        string cancelButtonText,
        double width,
        double height,
        bool fitContentHeight)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = CreateConfirmationWindow(
            owner,
            title,
            message,
            confirmButtonText,
            cancelButtonText,
            width,
            height,
            fitContentHeight,
            completion);

        dialog.Closed += (_, _) => completion.TrySetResult(false);

        if (owner is not null)
            _ = dialog.ShowDialog(owner);
        else
            dialog.Show();

        return await completion.Task.ConfigureAwait(false);
    }

    internal static Window CreateConfirmationWindow(
        Window? owner,
        string title,
        string message,
        string confirmButtonText,
        string cancelButtonText,
        double width,
        double height,
        bool fitContentHeight,
        TaskCompletionSource<bool> completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var themeVariant = DialogSurfaceFactory.ResolveThemeVariant(owner);
        var brushes = DialogSurfaceFactory.ResolveBrushes(owner, themeVariant);
        return DialogSurfaceFactory.CreateWindow(
            title,
            themeVariant,
            brushes,
            BuildConfirmationContent(message, confirmButtonText, cancelButtonText, completion),
            width,
            fitContentHeight ? null : height);
    }

    private static Control BuildContent(string message, string closeButtonText)
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
            Content = closeButtonText,
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

    private static Control BuildChoiceContent(
        string message,
        IReadOnlyList<string> choices,
        TaskCompletionSource<int> completion)
    {
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12),
            VerticalAlignment = VerticalAlignment.Center
        };
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(6)
        };
        for (var index = 0; index < choices.Count; index++)
        {
            var choiceIndex = index;
            var button = new Button
            {
                Content = choices[index],
                MinWidth = 110,
                Margin = new Thickness(6),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            if (index == choices.Count - 1)
                button.Classes.Add("primary-action");
            button.Click += (_, _) =>
            {
                completion.TrySetResult(choiceIndex);
                (TopLevel.GetTopLevel(buttonPanel) as Window)?.Close();
            };
            buttonPanel.Children.Add(button);
        }

        var panel = new DockPanel();
        DockPanel.SetDock(buttonPanel, Dock.Bottom);
        panel.Children.Add(buttonPanel);
        panel.Children.Add(text);
        return panel;
    }
}
