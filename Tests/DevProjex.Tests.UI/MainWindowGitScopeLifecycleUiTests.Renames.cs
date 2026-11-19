namespace DevProjex.Tests.UI;

public sealed partial class MainWindowGitScopeLifecycleUiTests
{
	[AvaloniaFact]
	public async Task StagedRenameAndRestoredDeletionPublishCurrentFilesAcrossModeSwitches()
	{
		using var project = UiTestProject.CreateWithCleanGitAndSmartWorkspace();
		using var repository = new GitModeRepository(project.RootPath);
		repository.Commit();
		repository.Git("mv", "Program.cs", "Renamed ü.cs");
		repository.Git("rm", "--quiet", "App.csproj");
		await File.WriteAllTextAsync(Path.Combine(project.RootPath, "App.csproj"), "<Project><PropertyGroup /></Project>\n", TestContext.Current.CancellationToken);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		try
		{
			foreach (var mode in new[] { GitFilteringMode.Staged, GitFilteringMode.Changes, GitFilteringMode.TrackedFilesOnly, GitFilteringMode.None })
			{
				await SelectAndApplyGitModeAsync(window, mode);
				await WaitForProjectTreePathStateAsync(window, exists: true, "Renamed ü.cs");
				await WaitForProjectTreePathStateAsync(window, exists: false, "Program.cs");
				await WaitForProjectTreePathStateAsync(window, exists: mode != GitFilteringMode.TrackedFilesOnly, "App.csproj");
			}
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}
}
