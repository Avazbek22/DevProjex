using System.Globalization;
using Avalonia.Controls;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class MessageDialogCompatibilityTests
{
	[AvaloniaFact]
	public void OriginalPrivateBuildersRetainTheirExactSignatures()
	{
		var message = typeof(MessageDialog).GetMethod("BuildContent", BindingFlags.NonPublic | BindingFlags.Static);
		var confirmation = typeof(MessageDialog).GetMethod("BuildConfirmationContent", BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(message);
		Assert.NotNull(confirmation);
		Assert.Equal([typeof(string), typeof(string)], message.GetParameters().Select(parameter => parameter.ParameterType));
		Assert.Equal([typeof(string), typeof(string), typeof(string), typeof(TaskCompletionSource<bool>)],
			confirmation.GetParameters().Select(parameter => parameter.ParameterType));
	}

	[AvaloniaFact]
	public void OrdinaryMessageRetainsItsOriginalControlTreeAndWidth()
	{
		var window = MessageDialog.CreateMessageWindow(null, "Message", "Saved", "Close");
		try
		{
			Assert.Equal(420, window.Width);
			Assert.Equal("DockPanel[Button(Close;80;0;Right;12,12,12,12;Bottom;)" +
				",ScrollViewer(Auto;Disabled)[TextBlock(Saved;Wrap;Center;12,12,12,12)]]", Describe((Control)window.Content!));
		}
		finally { window.Close(); }
	}

	[AvaloniaFact]
	public void OrdinaryConfirmationRetainsItsOriginalControlTreeAndWidth()
	{
		var window = MessageDialog.CreateConfirmationWindow(null, "Confirm", "Continue?", "Continue", "Cancel", 450,
			new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
		try
		{
			Assert.Equal(450, window.Width);
			Assert.Equal("DockPanel[StackPanel(Horizontal;Right;Bottom)[" +
				"Button(Continue;NaN;110;Right;12,12,6,12;Left;)," +
				"Button(Cancel;NaN;110;Right;6,12,12,12;Left;primary-action)]," +
				"ScrollViewer(Auto;Disabled)[TextBlock(Continue?;Wrap;Center;12,12,12,12)]]", Describe((Control)window.Content!));
		}
		finally { window.Close(); }
	}

	private static string Describe(Control control) => control switch
	{
		Button button => $"Button({button.Content};{Number(button.Width)};{Number(button.MinWidth)};{button.HorizontalAlignment};{Margin(button)};{DockPanel.GetDock(button)};{string.Join(',', button.Classes)})",
		TextBlock text => $"TextBlock({text.Text};{text.TextWrapping};{text.VerticalAlignment};{Margin(text)})",
		ScrollViewer scroll => $"ScrollViewer({scroll.VerticalScrollBarVisibility};{scroll.HorizontalScrollBarVisibility})[{Describe((Control)scroll.Content!)}]",
		StackPanel stack => $"StackPanel({stack.Orientation};{stack.HorizontalAlignment};{DockPanel.GetDock(stack)})[{string.Join(',', stack.Children.Select(Describe))}]",
		DockPanel dock => $"DockPanel[{string.Join(',', dock.Children.Select(Describe))}]",
		_ => throw new InvalidOperationException("Unexpected dialog control: " + control.GetType().Name)
	};

	private static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);
	private static string Margin(Control control) => string.Join(',', new[]
	{
		control.Margin.Left, control.Margin.Top, control.Margin.Right, control.Margin.Bottom
	}.Select(Number));
}
