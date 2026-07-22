namespace DevProjex.Tests.Unit;

public sealed class ProjectCopyExportUiContractTests
{
	[Fact]
	public void TopMenu_ProjectCopyIsSiblingOfTextExportAndPrecedesExit()
	{
		var xaml = ReadRepositoryFile("Apps", "Avalonia", "DevProjex.Avalonia", "Views", "TopMenuBarView.axaml");
		var textExportIndex = xaml.IndexOf("Header=\"{Binding MenuFileExport}\"", StringComparison.Ordinal);
		var projectCopyIndex = xaml.IndexOf("Name=\"ExportProjectCopyMenuItem\"", StringComparison.Ordinal);
		var exitIndex = xaml.IndexOf("Header=\"{Binding MenuFileExit}\"", StringComparison.Ordinal);

		Assert.True(textExportIndex >= 0 && textExportIndex < projectCopyIndex);
		Assert.True(projectCopyIndex < exitIndex);
		Assert.Contains("Name=\"ExportProjectCopyFolderMenuItem\"", xaml, StringComparison.Ordinal);
		Assert.Contains("Name=\"ExportProjectCopyZipMenuItem\"", xaml, StringComparison.Ordinal);
	}

	[Fact]
	public void TopMenu_ProjectCopyHelpUsesAccessibleNonButtonTooltipWithSharedBackdrop()
	{
		var xaml = ReadRepositoryFile("Apps", "Avalonia", "DevProjex.Avalonia", "Views", "TopMenuBarView.axaml");
		var start = xaml.IndexOf("Name=\"ExportProjectCopyMenuItem\"", StringComparison.Ordinal);
		var end = xaml.IndexOf("Header=\"{Binding MenuFileExit}\"", start, StringComparison.Ordinal);
		var projectCopyMarkup = xaml[start..end];

		Assert.Contains("AutomationProperties.HelpText=\"{Binding MenuFileExportProjectCopyHelp}\"", projectCopyMarkup, StringComparison.Ordinal);
		Assert.Contains("ToolTip.ShowDelay=\"420\"", projectCopyMarkup, StringComparison.Ordinal);
		Assert.Contains("Loaded=\"OnToolTipLoaded\"", projectCopyMarkup, StringComparison.Ordinal);
		Assert.Contains("MaxWidth=\"340\"", projectCopyMarkup, StringComparison.Ordinal);
		Assert.Contains("TextWrapping=\"Wrap\"", projectCopyMarkup, StringComparison.Ordinal);
		Assert.DoesNotContain("<Button", projectCopyMarkup, StringComparison.Ordinal);
	}

	[Fact]
	public void ProjectCopyExport_UsesCurrentTreeSoInteractiveFilterMatchesExistingExportSemantics()
	{
		var source = ReadRepositoryFile(
			"Apps",
			"Avalonia",
			"DevProjex.Avalonia",
			"MainWindow.ProjectCopyExport.cs");

		Assert.Contains("_currentTree.Root", source, StringComparison.Ordinal);
		Assert.DoesNotContain("_filterBaseTree", source, StringComparison.Ordinal);
		Assert.Contains("GetCheckedPaths()", source, StringComparison.Ordinal);
		Assert.Contains("_toastService.Show", source, StringComparison.Ordinal);
		Assert.DoesNotContain("ShowErrorAsync", source, StringComparison.Ordinal);
	}

	private static string ReadRepositoryFile(params string[] parts) =>
		File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. parts]));

	private static string FindRepositoryRoot()
	{
		var directory = AppContext.BaseDirectory;
		while (!string.IsNullOrWhiteSpace(directory))
		{
			if (File.Exists(Path.Combine(directory, "DevProjex.sln")))
				return directory;

			directory = Directory.GetParent(directory)?.FullName;
		}

		throw new InvalidOperationException("Repository root not found.");
	}
}
