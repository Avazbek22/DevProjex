using Avalonia.VisualTree;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MessageDialogSizingUiTests
{
	[AvaloniaFact]
	public void ShortMessageWindowIsExactlyAsTallAsItsContent()
	{
		var owner = ShowOwner();
		var dialog = MessageDialog.CreateConfirmationWindow(
			owner,
			"Recent folder unavailable",
			"The folder is no longer available. Remove it from the recent list?",
			"Remove",
			"Keep",
			width: 450,
			new TaskCompletionSource<bool>());
		try
		{
			ShowAndLayout(dialog);

			Assert.Equal(450, dialog.ClientSize.Width);
			Assert.True(dialog.ClientSize.Height < dialog.MaxHeight);
			Assert.Equal(MeasureUnconstrainedContentHeight(dialog), dialog.ClientSize.Height, 0.5);
			Assert.False(MessageScrolls(dialog));
			AssertButtonsInsideWindow(dialog, expectedCount: 2);
		}
		finally
		{
			dialog.Close();
			owner.Close();
		}
	}

	[AvaloniaFact]
	public void LongMessageWindowStopsAtItsCapAndScrollsTheMessage()
	{
		var owner = ShowOwner();
		var message = string.Join(Environment.NewLine, Enumerable.Range(1, 300).Select(line => $"Line {line}"));
		var dialogs = new (Window Window, int ButtonCount)[]
		{
			(MessageDialog.CreateMessageWindow(owner, "Error", message, "OK"), 1),
			(MessageDialog.CreateConfirmationWindow(
				owner, "Confirm", message, "Continue", "Cancel", width: 520, new TaskCompletionSource<bool>()), 2),
			(MessageDialog.CreateChoiceWindow(
				owner, "Choose", message, ["Stay", "Leave", "Retry"], new TaskCompletionSource<int>()), 3)
		};
		try
		{
			foreach (var (dialog, buttonCount) in dialogs)
			{
				ShowAndLayout(dialog);

				Assert.True(double.IsFinite(dialog.MaxHeight));
				Assert.True(MeasureUnconstrainedContentHeight(dialog) > dialog.MaxHeight);
				Assert.Equal(dialog.MaxHeight, dialog.ClientSize.Height, 0.5);
				Assert.True(MessageScrolls(dialog));
				AssertButtonsInsideWindow(dialog, buttonCount);
			}
		}
		finally
		{
			foreach (var (dialog, _) in dialogs)
				dialog.Close();
			owner.Close();
		}
	}

	[AvaloniaFact]
	public async Task CommandBlockKeepsEveryActionInsideTheWindowAndCopyDoesNotDecide()
	{
		var owner = ShowOwner();
		var command = new MessageDialogCommand(
			"flatpak override --user '--filesystem=/mnt/data/a rather long project folder name that has to wrap' io.github.Avazbek22.DevProjex",
			"Copy command");
		var completion = new TaskCompletionSource<bool>();
		var message = MessageDialog.CreateMessageWindow(
			owner, "Error", "The folder may be inaccessible in the Flatpak sandbox. To grant access, run:", "OK", command);
		var confirmation = MessageDialog.CreateConfirmationWindow(
			owner, "Recent folder unavailable", "The folder may be inaccessible in the Flatpak sandbox. To grant access, run:",
			"Remove", "Keep", width: 560, completion, command);
		try
		{
			foreach (var (dialog, buttonCount) in new[] { (message, 2), (confirmation, 3) })
			{
				ShowAndLayout(dialog);

				Assert.False(MessageScrolls(dialog));
				AssertButtonsInsideWindow(dialog, buttonCount);
				Assert.Equal(command.Text, Assert.Single(dialog.GetVisualDescendants().OfType<SelectableTextBlock>()).Text);
			}

			var copy = Assert.Single(confirmation.GetVisualDescendants().OfType<Button>(),
				static button => Equals(button.Content, "Copy command"));
			await UiTestDriver.RaiseButtonClickAsync(copy);
			Dispatcher.UIThread.RunJobs();

			Assert.False(completion.Task.IsCompleted);
			Assert.True(confirmation.IsVisible);
		}
		finally
		{
			message.Close();
			confirmation.Close();
			owner.Close();
		}
	}

	private static Window ShowOwner()
	{
		var owner = new Window { Width = 900, Height = 700 };
		UiTestDriver.TrackTopLevelWindow(owner);
		owner.Show();
		return owner;
	}

	private static void ShowAndLayout(Window dialog)
	{
		UiTestDriver.TrackTopLevelWindow(dialog);
		dialog.Show();
		Dispatcher.UIThread.RunJobs();
	}

	private static double MeasureUnconstrainedContentHeight(Window dialog)
	{
		var content = Assert.IsAssignableFrom<Control>(dialog.Content);
		content.Measure(new Size(dialog.ClientSize.Width, double.PositiveInfinity));
		return content.DesiredSize.Height;
	}

	private static bool MessageScrolls(Window dialog)
	{
		var scrollViewer = Assert.Single(dialog.GetVisualDescendants().OfType<ScrollViewer>());
		return scrollViewer.Extent.Height > scrollViewer.Viewport.Height + 0.5;
	}

	private static void AssertButtonsInsideWindow(Window dialog, int expectedCount)
	{
		// The scroll bar template carries its own repeat buttons; only the dialog's actions count here.
		var buttons = dialog.GetVisualDescendants()
			.OfType<Button>()
			.Where(static button => button is not RepeatButton)
			.ToArray();
		Assert.Equal(expectedCount, buttons.Length);
		var client = new Rect(dialog.ClientSize);
		Assert.All(buttons, button =>
		{
			var origin = button.TranslatePoint(new Point(0, 0), dialog);
			Assert.NotNull(origin);
			var bounds = new Rect(origin.Value, button.Bounds.Size);
			Assert.True(bounds.Width > 0 && bounds.Height > 0);
			Assert.True(client.Inflate(0.5).Contains(bounds), $"Button '{button.Content}' at {bounds} is outside {client}.");
		});
	}
}
