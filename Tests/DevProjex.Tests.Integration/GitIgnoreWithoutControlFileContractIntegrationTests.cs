using DevProjex.Application.Context;

namespace DevProjex.Tests.Integration;

[Trait("Category", "IgnoreContract")]
public sealed class GitIgnoreWithoutControlFileContractIntegrationTests
{
	[Fact]
	public async Task ExplicitGitIgnoreModeWithoutPatterns_ExcludesOnlyAdministrativeGitEntriesAcrossConsumers()
	{
		using var workspace = new TemporaryDirectory();
		workspace.CreateFile(".git/config", "repository metadata");
		workspace.CreateFile(".git/HEAD", "ref: refs/heads/main\n");
		workspace.CreateFile(".git/objects/leak.bin", "must never leave the repository view");
		workspace.CreateFile(".github/workflows/build.yml", "name: build\n");
		workspace.CreateFile(".git-owned/keep.txt", "ordinary lookalike\n");
		workspace.CreateFile("src/App.cs", "public sealed class App {}\n");
		workspace.CreateFile("src/nested/.git/config", "nested repository metadata");
		workspace.CreateFile("src/nested/Feature.cs", "public sealed class Feature {}\n");
		workspace.CreateFile("worktree/.git", "gitdir: ../.git/worktrees/worktree\n");
		workspace.CreateFile("worktree/Tracked.cs", "public sealed class Tracked {}\n");

		var analysisService = CreateProjectAnalysisService();
		var analysis = analysisService.Load(
			new ProjectAnalysisRequest(
				workspace.Path,
				SelectedIgnoreOptions: [IgnoreOptionId.UseGitIgnore]),
			TestContext.Current.CancellationToken);
		var context = await new ProjectContextPlanner(analysisService).BuildAsync(
			new ProjectContextRequest(
				workspace.Path,
				new ProjectSelectionSpec(
					GitMode: GitFilteringMode.RespectGitIgnore,
					Exclusions: [])),
			TestContext.Current.CancellationToken);

		var expected = new HashSet<string>(StringComparer.Ordinal)
		{
			".git-owned/keep.txt",
			".github/workflows/build.yml",
			"src/App.cs",
			"src/nested/Feature.cs",
			"worktree/Tracked.cs"
		};
		AssertFileSet(expected, workspace.Path, analysis.Tree.Root, "analysis");
		Assert.Equal(expected, Normalize(workspace.Path, context.IncludedFiles));
		Assert.Equal(GitFilteringMode.RespectGitIgnore, context.GitReadiness.Mode);
		Assert.DoesNotContain(context.AvailableRoots, static root => root == ".git");
	}

	[Fact]
	public void SelectedPosixRootName_PreservesLeadingAndTrailingWhitespace()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("Windows normalizes trailing spaces in ordinary directory names.");

		using var workspace = new TemporaryDirectory();
		workspace.CreateFile(" source /App.cs", "public sealed class App {}\n");
		workspace.CreateFile("source/Other.cs", "public sealed class Other {}\n");
		var analysis = CreateProjectAnalysisService().Load(
			new ProjectAnalysisRequest(
				workspace.Path,
				SelectedRootFolders: [" source "],
				SelectedIgnoreOptions: []),
			TestContext.Current.CancellationToken);

		Assert.Equal([" source "], analysis.SelectedRootFolders);
		Assert.Equal(
			new HashSet<string>(StringComparer.Ordinal) { " source /App.cs" },
			Normalize(workspace.Path, analysis.Tree.OrderedFilePaths ?? []));
	}

	private static ProjectAnalysisService CreateProjectAnalysisService() =>
		new(
			new ScanOptionsUseCase(new FileSystemScanner()),
			ProjectLoadWorkflowRuntime.CreateBuildTreeUseCase(),
			new FilterOptionSelectionService(),
			ProjectLoadWorkflowRuntime.CreateIgnoreOptionsService(),
			ProjectLoadWorkflowRuntime.CreateIgnoreRulesService(),
			new TreeExportService(),
			new FileContentAnalyzer());

	private static void AssertFileSet(
		IReadOnlySet<string> expected,
		string rootPath,
		TreeNodeDescriptor root,
		string surface)
	{
		var paths = new List<string>();
		var pending = new Stack<TreeNodeDescriptor>();
		pending.Push(root);
		while (pending.Count > 0)
		{
			var node = pending.Pop();
			if (!node.IsDirectory)
				paths.Add(node.FullPath);
			for (var index = node.Children.Count - 1; index >= 0; index--)
				pending.Push(node.Children[index]);
		}

		var actual = Normalize(rootPath, paths);
		Assert.True(
			expected.SetEquals(actual),
			$"{surface} manifest mismatch. Expected: {string.Join(", ", expected.Order())}. " +
			$"Actual: {string.Join(", ", actual.Order())}.");
	}

	private static HashSet<string> Normalize(string rootPath, IEnumerable<string> paths) =>
		paths
			.Select(path => Path.GetRelativePath(rootPath, path).Replace('\\', '/'))
			.ToHashSet(StringComparer.Ordinal);
}
