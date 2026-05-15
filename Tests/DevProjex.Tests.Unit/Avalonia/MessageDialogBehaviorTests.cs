using Avalonia.Controls;
using Avalonia.Interactivity;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class MessageDialogBehaviorTests
{
    [AvaloniaFact]
    public void BuildConfirmationContent_UsesProvidedMessageAndButtonLabels()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = InvokeBuildConfirmationContent("Reset project data?", "Reset", "Cancel", completion);

        var (_, _, messageText) = ExtractConfirmationElements(content);
        var (confirmButton, cancelButton, _) = ExtractConfirmationElements(content);

        Assert.Equal("Reset project data?", messageText.Text);
        Assert.Equal("Reset", confirmButton.Content);
        Assert.Equal("Cancel", cancelButton.Content);
    }

    [AvaloniaFact]
    public async Task BuildConfirmationContent_ConfirmClick_CompletesWithTrue()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = InvokeBuildConfirmationContent("Message", "Confirm", "Cancel", completion);

        var (confirmButton, _, _) = ExtractConfirmationElements(content);
        confirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(result);
    }

    [AvaloniaFact]
    public async Task BuildConfirmationContent_CancelClick_CompletesWithFalse()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = InvokeBuildConfirmationContent("Message", "Confirm", "Cancel", completion);

        var (_, cancelButton, _) = ExtractConfirmationElements(content);
        cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(result);
    }

    private static Control InvokeBuildConfirmationContent(
        string message,
        string confirmButtonText,
        string cancelButtonText,
        TaskCompletionSource<bool> completion)
    {
        var method = typeof(MessageDialog).GetMethod(
            "BuildConfirmationContent",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var content = (Control?)method!.Invoke(null, [message, confirmButtonText, cancelButtonText, completion]);
        Assert.NotNull(content);
        return content!;
    }

    private static (Button Confirm, Button Cancel, TextBlock Message) ExtractConfirmationElements(Control content)
    {
        var panel = Assert.IsType<DockPanel>(content);
        var buttonPanel = Assert.Single(panel.Children.OfType<StackPanel>());
        var message = Assert.Single(panel.Children.OfType<TextBlock>());

        var buttons = buttonPanel.Children.OfType<Button>().ToArray();
        Assert.Equal(2, buttons.Length);

        return (buttons[0], buttons[1], message);
    }
}
