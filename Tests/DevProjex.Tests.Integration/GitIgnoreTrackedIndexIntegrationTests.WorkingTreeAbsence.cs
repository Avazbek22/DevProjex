using DevProjex.Application.Context;
using DevProjex.Application.Diff;

namespace DevProjex.Tests.Integration;

public sealed partial class GitIgnoreTrackedIndexIntegrationTests
{
	[Fact]
	public async Task RangeFileAddedOnTheNewerCommitIsCountedAsAbsentNotDeleted()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/kept.txt", "kept\n");
		temp.CreateFile("repo/removed.txt", "removed\n");
		InitializeCommittedRepository(repositoryRoot, "kept.txt", "removed.txt");
		var mainCommit = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
		RunGit(repositoryRoot, "checkout", "--quiet", "-b", "feature");
		temp.CreateFile("repo/added.txt", "added on feature\n");
		RunGit(repositoryRoot, "add", "--", "added.txt");
		RunGit(repositoryRoot, "rm", "--quiet", "--", "removed.txt");
		RunGit(repositoryRoot, "commit", "--quiet", "-m", "feature");
		RunGit(repositoryRoot, "checkout", "--quiet", mainCommit);
		Assert.False(File.Exists(Path.Combine(repositoryRoot, "added.txt")));

		var result = await new GitScopePathProvider().ResolveAsync(
			repositoryRoot,
			GitFilteringMode.Diff,
			$"{mainCommit}..feature",
			TestContext.Current.CancellationToken);

		Assert.True(result.IsAvailable, result.FailureReason);
		// The file the range deletes is still in this older working tree, so it is shown, not counted.
		Assert.Equal(
			PathUtility.Normalize(Path.Combine(repositoryRoot, "removed.txt")),
			Assert.Single(result.IncludedPaths),
			PathComparer.Default);
		Assert.Equal(0, result.DeletedPathCount);
		Assert.Equal(
			PathUtility.Normalize(Path.Combine(repositoryRoot, "added.txt")),
			Assert.Single(result.NotInWorkingTreePaths),
			PathComparer.Default);
	}

	[Theory]
	[InlineData(null, 1, 1)]
	[InlineData("src", 1, 0)]
	[InlineData("docs", 0, 1)]
	public async Task MissingFileCountsFollowTheSelectedPaths(string? selectedPath, int deleted, int absent)
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/src/removed.cs", "class Removed {}\n");
		temp.CreateFile("repo/src/kept.cs", "class Kept {}\n");
		temp.CreateFile("repo/docs/guide.md", "guide\n");
		InitializeCommittedRepository(repositoryRoot, "src/removed.cs", "src/kept.cs", "docs/guide.md");
		var baseCommit = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
		RunGit(repositoryRoot, "rm", "--quiet", "--", "src/removed.cs");
		temp.CreateFile("repo/docs/added.md", "added\n");
		RunGit(repositoryRoot, "add", "--", "docs/added.md");
		RunGit(repositoryRoot, "commit", "--quiet", "-m", "change");
		var newerCommit = RunGit(repositoryRoot, "rev-parse", "HEAD").Trim();
		RunGit(repositoryRoot, "checkout", "--quiet", baseCommit);
		RunGit(repositoryRoot, "rm", "--quiet", "--cached", "--", "src/removed.cs");
		File.Delete(Path.Combine(repositoryRoot, "src", "removed.cs"));
		var planner = new ProjectContextPlanner(CreateProjectAnalysisService());
		var plan = await planner.BuildStructureAsync(
			new ProjectContextRequest(
				repositoryRoot,
				ProjectSelectionSpec.Standard with
				{
					GitMode = GitFilteringMode.Diff,
					GitDiffRange = $"{baseCommit}..{newerCommit}",
					SelectedPaths = selectedPath is null ? null : [selectedPath]
				}),
			TestContext.Current.CancellationToken);

		var scoped = await GitScopeFilter.ApplyAsync(
			planner,
			plan,
			new GitScopePathProvider(),
			TestContext.Current.CancellationToken);

		Assert.False(scoped.HasErrors);
		Assert.Equal(
			deleted,
			scoped.Diagnostics
				.Where(static diagnostic => diagnostic.Code == GitScopeFilter.DeletedDiagnosticCode)
				.Sum(static diagnostic => diagnostic.Count ?? 0));
		var diff = await new ProjectDiffService(new GitFileVersionReader()).CompareAsync(
			new ProjectDiffRequest(
				scoped,
				GitFilteringMode.Diff,
				$"{baseCommit}..{newerCommit}",
				3,
				null,
				static (_, _) => ValueTask.FromResult(new FileContentReadResult(FileContentClassification.Missing))),
			TestContext.Current.CancellationToken);
		Assert.Equal(deleted, diff.DeletedOutsideSelection);
		Assert.Equal(absent, diff.NotInWorkingTree);
	}

	[Fact]
	public async Task CallPathFilterDecidesWhichMissingFilesAreCounted()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		temp.CreateFile("repo/src/removed.cs", "class Removed {}\n");
		temp.CreateFile("repo/src/kept.cs", "class Kept {}\n");
		InitializeCommittedRepository(repositoryRoot, "src/removed.cs", "src/kept.cs");
		RunGit(repositoryRoot, "rm", "--quiet", "--", "src/removed.cs");
		var planner = new ProjectContextPlanner(CreateProjectAnalysisService());
		var plan = await planner.BuildStructureAsync(
			new ProjectContextRequest(
				repositoryRoot,
				ProjectSelectionSpec.Standard with { GitMode = GitFilteringMode.Staged }),
			TestContext.Current.CancellationToken);

		var excluded = await GitScopeFilter.ApplyAsync(
			planner,
			plan,
			new GitScopePathProvider(),
			GitFilteringMode.Staged,
			diffRange: null,
			repositoryScopeFullPaths: null,
			TestContext.Current.CancellationToken,
			missingPathFilter: static path => !path.EndsWith("removed.cs", StringComparison.Ordinal));
		var included = await GitScopeFilter.ApplyAsync(
			planner,
			plan,
			new GitScopePathProvider(),
			GitFilteringMode.Staged,
			diffRange: null,
			repositoryScopeFullPaths: null,
			TestContext.Current.CancellationToken,
			missingPathFilter: static _ => true);

		Assert.DoesNotContain(
			excluded.Diagnostics,
			static diagnostic => diagnostic.Code == GitScopeFilter.DeletedDiagnosticCode);
		Assert.Equal(
			1,
			Assert.Single(
				included.Diagnostics,
				static diagnostic => diagnostic.Code == GitScopeFilter.DeletedDiagnosticCode).Count);
	}
}
