using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace DevProjex.Tests.Integration;

public sealed class CopyMenuBindingsTests
{
	private static readonly Lazy<string> RepoRoot = new(() => FindRepositoryRoot());

	[Fact]
	public void TopMenuBarView_UsesThreeCopyMenuBindings()
	{
		var file = Path.Combine(RepoRoot.Value, "Apps", "Avalonia", "DevProjex.Avalonia", "Views", "TopMenuBarView.axaml");
		var content = File.ReadAllText(file);

		Assert.Contains("MenuCopyTree", content);
		Assert.Contains("MenuCopyContent", content);
		Assert.Contains("MenuCopyTreeAndContent", content);

		Assert.DoesNotContain("MenuCopyFullTree", content);
		Assert.DoesNotContain("MenuCopySelectedTree", content);
		Assert.DoesNotContain("MenuCopySelectedContent", content);

		var treeMatches = Regex.Matches(content, "Header=\\\"\\{Binding MenuCopyTree\\}");
		var contentMatches = Regex.Matches(content, "Header=\\\"\\{Binding MenuCopyContent\\}");
		var treeAndContentMatches = Regex.Matches(content, "Header=\\\"\\{Binding MenuCopyTreeAndContent\\}");
		Assert.Equal(1, treeMatches.Count);
		Assert.Equal(1, contentMatches.Count);
		Assert.Equal(1, treeAndContentMatches.Count);
	}

	[Fact]
	public void MainWindow_WiresNewCopyEvents()
	{
		var file = Path.Combine(RepoRoot.Value, "Apps", "Avalonia", "DevProjex.Avalonia", "MainWindow.axaml");
		var content = File.ReadAllText(file);

		Assert.Contains("CopyTreeRequested=\"OnCopyTree\"", content);
		Assert.Contains("CopyContentRequested=\"OnCopyContent\"", content);
		Assert.Contains("CopyTreeAndContentRequested=\"OnCopyTreeAndContent\"", content);

		Assert.DoesNotContain("CopyFullTreeRequested", content);
		Assert.DoesNotContain("CopySelectedTreeRequested", content);
		Assert.DoesNotContain("CopySelectedContentRequested", content);
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
