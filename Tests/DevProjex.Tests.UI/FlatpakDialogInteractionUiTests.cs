using System.Reflection;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class FlatpakDialogInteractionUiTests
{
	[AvaloniaTheory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CopyPreservesTheCompleteWrappedCommandAndDoesNotCloseOrConfirm(bool confirmation)
	{
		var owner = new Window { Width = 900, Height = 700 };
		UiTestDriver.TrackTopLevelWindow(owner);
		owner.Show();
		var command = new MessageDialogCommand("flatpak override --user '--filesystem=/home/O’Connor/" + string.Join(' ', Enumerable.Repeat("project🚀", 150)) + "' test.app", "Copy command");
		var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var dialog = confirmation
			? MessageDialog.CreateConfirmationWindow(owner, "Confirm", "Run:", "Remove", "Keep", 560, completion, command)
			: MessageDialog.CreateMessageWindow(owner, "Error", "Run:", "Close", command);
		UiTestDriver.TrackTopLevelWindow(dialog);
		try
		{
			dialog.Show();
			Dispatcher.UIThread.RunJobs();
			var text = Assert.Single(dialog.GetVisualDescendants().OfType<SelectableTextBlock>());
			Assert.Equal(command.Text, text.Text);
			Assert.Equal(global::Avalonia.Media.TextWrapping.Wrap, text.TextWrapping);
			Assert.True(text.Bounds.Width <= dialog.ClientSize.Width);
			var copy = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Copy command"));
			await UiTestDriver.RaiseButtonClickAsync(copy);
			Assert.Equal(command.Text, await ClipboardExtensions.TryGetTextAsync(dialog.Clipboard!));
			Assert.True(dialog.IsVisible);
			Assert.False(completion.Task.IsCompleted);
			foreach (var button in dialog.GetVisualDescendants().OfType<Button>().Where(button => button is not RepeatButton))
			{
				var point = button.TranslatePoint(default, dialog);
				Assert.NotNull(point);
				Assert.True(new Rect(dialog.ClientSize).Inflate(1).Contains(new Rect(point.Value, button.Bounds.Size)));
			}
		}
		finally { dialog.Close(); owner.Close(); }
	}

	[AvaloniaFact]
	public void CopyWithoutATopLevelOrClipboardIsHarmless()
	{
		var create = typeof(MessageDialog).GetMethod("CreateCopyButton", BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(create);
		var copy = Assert.IsType<Button>(create.Invoke(null, [new DockPanel(), new MessageDialogCommand("command", "Copy")]));
		Assert.Null(TopLevel.GetTopLevel(copy));
		copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Dispatcher.UIThread.RunJobs();
		Assert.Null(TopLevel.GetTopLevel(copy));
	}

	[AvaloniaFact]
	public void ClosingTheOwnerClosesItsCommandDialog()
	{
		var owner = new Window { Width = 900, Height = 700 };
		UiTestDriver.TrackTopLevelWindow(owner);
		var dialog = MessageDialog.CreateMessageWindow(owner, "Error", "Run:", "Close", new("command", "Copy"));
		UiTestDriver.TrackTopLevelWindow(dialog);
		try
		{
			owner.Show();
			dialog.Show(owner);
			Dispatcher.UIThread.RunJobs();
			Assert.True(dialog.IsVisible);
			owner.Close();
			Dispatcher.UIThread.RunJobs();
			Assert.False(owner.IsVisible);
			Assert.False(dialog.IsVisible);
		}
		finally { dialog.Close(); owner.Close(); }
	}
}
