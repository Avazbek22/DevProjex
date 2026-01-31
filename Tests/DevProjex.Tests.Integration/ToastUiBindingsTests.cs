using System;
using System.IO;
using Xunit;

namespace DevProjex.Tests.Integration;

public sealed class ToastUiBindingsTests
{
	[Fact]
	public void MainWindow_ContainsToastItemsControl()
	{
		var file = Path.Combine(FindRepositoryRoot(), "Apps", "Avalonia", "DevProjex.Avalonia", "MainWindow.axaml");
		var content = File.ReadAllText(file);

		Assert.Contains("ItemsSource=\"{Binding ToastItems}\"", content);
		Assert.Contains("Text=\"{Binding Message}\"", content);
		Assert.Contains("TranslateTransform Y=\"{Binding OffsetY}\"", content);
		Assert.Contains("Background=\"{DynamicResource AppPanelBrush}\"", content);
		Assert.Contains("BorderBrush=\"{DynamicResource AppBorderBrush}\"", content);
		Assert.Contains("Foreground=\"{DynamicResource AppTextBrush}\"", content);
	}

	private static string FindRepositoryRoot()
	{
		var dir = AppContext.BaseDirectory;
		while (dir != null)
		{
			if (Directory.Exists(Path.Combine(dir, ".git")) ||
				File.Exists(Path.Combine(dir, "DevProjex.sln")))
				return dir;

			dir = Directory.GetParent(dir)?.FullName;
		}

		throw new InvalidOperationException("Repository root not found.");
	}
}
