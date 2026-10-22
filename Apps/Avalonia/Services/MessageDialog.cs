namespace DevProjex.Avalonia.Services;

public sealed record MessageDialogCommand(string Text, string CopyButtonText);

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
        var dialog = CreateChoiceWindow(owner, title, message, choices, completion);
        dialog.Closed += (_, _) => completion.TrySetResult(0);
        _ = dialog.ShowDialog(owner);
        return await completion.Task.ConfigureAwait(false);
    }

    public static async Task ShowAsync(
        Window owner,
        string title,
        string message,
        string closeButtonText,
        MessageDialogCommand? command = null)
    {
        var dialog = CreateMessageWindow(owner, title, message, closeButtonText, command);

        if (owner is not null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
    }

    public static async Task<bool> ShowConfirmationAsync(
        Window owner,
        string title,
        string message,
        string confirmButtonText,
        string cancelButtonText,
        double width = 520,
        MessageDialogCommand? command = null)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = CreateConfirmationWindow(
            owner,
            title,
            message,
            confirmButtonText,
            cancelButtonText,
            width,
            completion,
            command);

        dialog.Closed += (_, _) => completion.TrySetResult(false);

        if (owner is not null)
            _ = dialog.ShowDialog(owner);
        else
            dialog.Show();

        return await completion.Task.ConfigureAwait(false);
    }

    internal static Window CreateMessageWindow(
        Window? owner,
        string title,
        string message,
        string closeButtonText,
        MessageDialogCommand? command = null) =>
        CreateDialogWindow(
            owner,
            title,
            command is null ? BuildContent(message, closeButtonText) : BuildContentWithCommand(message, closeButtonText, command),
            width: command is null ? 420 : 520);

    internal static Window CreateConfirmationWindow(
        Window? owner,
        string title,
        string message,
        string confirmButtonText,
        string cancelButtonText,
        double width,
        TaskCompletionSource<bool> completion,
        MessageDialogCommand? command = null)
    {
        ArgumentNullException.ThrowIfNull(completion);
        return CreateDialogWindow(
            owner,
            title,
            command is null
                ? BuildConfirmationContent(message, confirmButtonText, cancelButtonText, completion)
                : BuildConfirmationContentWithCommand(message, confirmButtonText, cancelButtonText, completion, command),
            width);
    }

    internal static Window CreateChoiceWindow(
        Window? owner,
        string title,
        string message,
        IReadOnlyList<string> choices,
        TaskCompletionSource<int> completion)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(completion);
        return CreateDialogWindow(owner, title, BuildChoiceContent(message, choices, completion), width: 560);
    }

    private static Window CreateDialogWindow(Window? owner, string title, Control content, double width)
    {
        var themeVariant = DialogSurfaceFactory.ResolveThemeVariant(owner);
        var brushes = DialogSurfaceFactory.ResolveBrushes(owner, themeVariant);
        return DialogSurfaceFactory.CreateContentSizedWindow(owner, title, themeVariant, brushes, content, width);
    }

    private static Control BuildContent(string message, string closeButtonText)
    {
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
        panel.Children.Add(BuildMessageArea(message));

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
        panel.Children.Add(BuildMessageArea(message));

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

    private static Control BuildContentWithCommand(string message, string closeButtonText, MessageDialogCommand command)
    {
        var panel = (DockPanel)BuildContent(message, closeButtonText);
        var closeButton = (Button)panel.Children[0];
        panel.Children.RemoveAt(0);
        closeButton.Margin = new Thickness(6, 12, 12, 12);
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttonPanel.Children.Add(CreateCopyButton(panel, command));
        buttonPanel.Children.Add(closeButton);
        DockPanel.SetDock(buttonPanel, Dock.Bottom);
        panel.Children.Insert(0, buttonPanel);
        ((ScrollViewer)panel.Children[1]).Content = BuildMessageWithCommand(message, command);
        return panel;
    }

    private static Control BuildConfirmationContentWithCommand(
        string message,
        string confirmButtonText,
        string cancelButtonText,
        TaskCompletionSource<bool> completion,
        MessageDialogCommand command)
    {
        var panel = (DockPanel)BuildConfirmationContent(message, confirmButtonText, cancelButtonText, completion);
        var buttonPanel = (StackPanel)panel.Children[0];
        ((Button)buttonPanel.Children[0]).Margin = new Thickness(6, 12, 6, 12);
        buttonPanel.Children.Insert(0, CreateCopyButton(panel, command));
        ((ScrollViewer)panel.Children[1]).Content = BuildMessageWithCommand(message, command);
        return panel;
    }

    private static Control BuildChoiceContent(
        string message,
        IReadOnlyList<string> choices,
        TaskCompletionSource<int> completion)
    {
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
        panel.Children.Add(BuildMessageArea(message));
        return panel;
    }

    private static Button CreateCopyButton(Control dialogContent, MessageDialogCommand command)
    {
        var copyButton = new Button
        {
            Content = command.CopyButtonText,
            Margin = new Thickness(12, 12, 6, 12),
            MinWidth = 110,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        copyButton.Click += async (_, _) =>
        {
            try
            {
                if (TopLevel.GetTopLevel(dialogContent)?.Clipboard is { } clipboard)
                    await clipboard.SetTextAsync(command.Text);
            }
            catch
            {
                // Clipboard access can be unavailable in headless or restricted sessions.
            }
        };
        return copyButton;
    }

    // The window sizes itself to this area until it reaches its height cap; from then on the
    // message scrolls while the docked buttons keep their place.
    private static ScrollViewer BuildMessageArea(string message) =>
        new()
        {
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12),
                VerticalAlignment = VerticalAlignment.Center
            },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

    private static StackPanel BuildMessageWithCommand(string message, MessageDialogCommand command) =>
        new()
        {
            Margin = new Thickness(12),
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new SelectableTextBlock
                {
                    Text = command.Text,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = FontFamily.Parse("Consolas,Menlo,Monospace"),
                    FontSize = 13
                }
            }
        };
}
