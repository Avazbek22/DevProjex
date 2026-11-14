namespace DevProjex.Tests.Terminal;

public sealed partial class TerminalSettingsStateContractTests
{
	[Fact]
	public async Task GitModeRoundTripMatchesTheSharedFileSetWithoutLosingSelection()
	{
		using var repository = new GitModeRepository();
		using var appData = new TemporaryDirectory();
		repository.WriteScopeMatrix();
		var (controller, state) = await OpenAsync(repository.Root, appData.Path);
		using (state)
		{
			await controller.SetPathFilteringAsync(state, GitFilteringMode.None, [], TestContext.Current.CancellationToken);
			await controller.SetExtensionsAsync(state, [".cs"], TestContext.Current.CancellationToken);
			foreach (var token in new[] { "none", "gitignore", "tracked", "staged", "changes", "none" })
			{
				Assert.True(GitScopeSelection.TryParse(token, out var mode, out _));
				await controller.SetPathFilteringAsync(state, mode, [], TestContext.Current.CancellationToken);
				Assert.False(state.Plan.HasErrors);
				Assert.Equal(GitModeRepository.ExpectedScopeFiles(token), IncludedRelativePaths(state));
			}
			await controller.RebuildAsync(state, state.BuildSelection() with
			{
				GitMode = GitFilteringMode.Diff,
				GitDiffRange = "HEAD~1..HEAD"
			}, TestContext.Current.CancellationToken);
			Assert.False(state.Plan.HasErrors);
			Assert.Equal(GitModeRepository.ExpectedScopeFiles("diff:HEAD~1..HEAD"), IncludedRelativePaths(state));
			await controller.RefreshProjectAsync(state, TestContext.Current.CancellationToken);
			Assert.Equal(GitModeRepository.ExpectedScopeFiles("diff:HEAD~1..HEAD"), IncludedRelativePaths(state));
			await controller.SetGitModeAsync(state, GitFilteringMode.None, TestContext.Current.CancellationToken);
			Assert.Null(state.Plan.Selection.GitDiffRange);
			Assert.Equal(GitModeRepository.ExpectedScopeFiles("none"), IncludedRelativePaths(state));
		}
	}
}
