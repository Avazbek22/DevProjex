using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class TerminalCommandSetupDialogVisualTests
{
	[AvaloniaFact]
	public void ResolveDialogBrushes_UsesSolidBackgroundColorInsteadOfTransparentThemeBrush()
	{
		var app = global::Avalonia.Application.Current;
		Assert.NotNull(app);

		var resources = app!.Resources;
		var originalBackgroundBrush = CaptureResource(resources, "AppBackgroundBrush", out var hadBackgroundBrush);
		var originalBackgroundColor = CaptureResource(resources, "AppBackgroundColor", out var hadBackgroundColor);

		try
		{
			resources["AppBackgroundBrush"] = Brushes.Transparent;
			resources["AppBackgroundColor"] = Colors.White;

			var brushes = TerminalCommandSetupDialog.ResolveDialogBrushes(null, ThemeVariant.Default);
			var background = Assert.IsType<SolidColorBrush>(brushes.Background);

			Assert.Equal(Colors.White, background.Color);
		}
		finally
		{
			RestoreResource(resources, "AppBackgroundBrush", originalBackgroundBrush, hadBackgroundBrush);
			RestoreResource(resources, "AppBackgroundColor", originalBackgroundColor, hadBackgroundColor);
		}
	}

	private static object? CaptureResource(IResourceDictionary resources, string key, out bool exists)
	{
		exists = resources.ContainsKey(key);
		return exists ? resources[key] : null;
	}

	private static void RestoreResource(IResourceDictionary resources, string key, object? value, bool exists)
	{
		if (exists)
			resources[key] = value;
		else
			resources.Remove(key);
	}
}
