using DevProjex.Application.Context;

namespace DevProjex.Tests.Integration;

public sealed partial class DeepGitWorkspaceEvidenceIntegrationTests
{
	[Theory]
	[InlineData(GitFilteringMode.RespectGitIgnore, false)]
	[InlineData(GitFilteringMode.TrackedFilesOnly, false)]
	[InlineData(GitFilteringMode.None, true)]
	public async Task RepositoryBoundaries_EmbeddedCloneAndWorktreeAreOpaque(GitFilteringMode mode, bool visible)
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		temp.CreateFile("anchor.cs", "anchor");
		InitializeIndex(temp.Path, "anchor.cs");
		RunGit(temp.Path, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-qm", "Initial");
		var embedded = temp.CreateDirectory("libs/SomeLib");
		temp.CreateFile("libs/SomeLib/embedded.cs", "embedded");
		InitializeIndex(embedded, "embedded.cs");
		RunGit(temp.Path, "worktree", "add", "--detach", ".claude/worktrees/x");

		var plan = await BuildBoundaryPlan(temp.Path, mode);
		Assert.Equal(visible, plan.IncludedFiles.Any(path => path.EndsWith("embedded.cs", StringComparison.Ordinal)));
		Assert.Equal(visible, plan.IncludedFiles.Any(path => path.Contains("worktrees", StringComparison.Ordinal)));
		Assert.Contains(plan.IncludedFiles, path => path == Path.Combine(temp.Path, "anchor.cs"));
	}

	[Theory]
	[InlineData(GitFilteringMode.RespectGitIgnore)]
	[InlineData(GitFilteringMode.TrackedFilesOnly)]
	public async Task RepositoryBoundaries_DeclaredSubmodulesOwnRecursiveScopes(GitFilteringMode mode)
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		temp.CreateFile("anchor.cs", "anchor");
		temp.CreateFile(".gitignore", "*.log\n");
		temp.CreateFile(".gitmodules", "[submodule \"x\"]\n path = third_party/x\n url = ignored\n");
		InitializeIndex(temp.Path, "anchor.cs", ".gitignore", ".gitmodules");
		var submodule = temp.CreateDirectory("third_party/x");
		temp.CreateFile("third_party/x/a.log", "visible");
		temp.CreateFile("third_party/x/drop.secret", "hidden");
		temp.CreateFile("third_party/x/.gitignore", "*.secret\n");
		temp.CreateFile("third_party/x/.gitmodules", "[submodule \"nested\"]\n path = nested\n");
		InitializeIndex(submodule, "a.log", ".gitignore", ".gitmodules");
		var nested = temp.CreateDirectory("third_party/x/nested");
		temp.CreateFile("third_party/x/nested/nested.secret", "visible");
		InitializeIndex(nested, "nested.secret");

		var plan = await BuildBoundaryPlan(temp.Path, mode);
		Assert.Contains(plan.IncludedFiles, path => path.EndsWith("a.log", StringComparison.Ordinal));
		Assert.Contains(plan.IncludedFiles, path => path.EndsWith("nested.secret", StringComparison.Ordinal));
		Assert.DoesNotContain(plan.IncludedFiles, path => path.EndsWith("drop.secret", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData(GitFilteringMode.RespectGitIgnore)]
	[InlineData(GitFilteringMode.TrackedFilesOnly)]
	public async Task RepositoryBoundaries_WorkspaceOwnsIndependentRepositories(GitFilteringMode mode)
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		foreach (var name in new[] { "A", "B", "A/vendor/x" })
		{
			var repository = temp.CreateDirectory(name);
			temp.CreateFile(name + "/source.cs", "source");
			InitializeIndex(repository, "source.cs");
		}
		var plan = await BuildBoundaryPlan(temp.Path, mode);
		Assert.Equal(2, plan.IncludedFiles.Count);
		Assert.DoesNotContain(plan.IncludedFiles, path => path.Contains("vendor", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RepositoryBoundaries_InfoExcludeAppliesAtOrdinaryAndWorktreeRoots(bool worktree)
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repository = temp.CreateDirectory("repository");
		temp.CreateFile("repository/anchor.cs", "anchor");
		InitializeIndex(repository, "anchor.cs");
		RunGit(repository, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-qm", "Initial");
		var root = repository;
		if (worktree)
		{
			root = Path.Combine(temp.Path, "worktree");
			RunGit(repository, "worktree", "add", "--detach", root);
		}
		temp.CreateFile("repository/.git/info/exclude", "notes/\n*.local\n");
		Directory.CreateDirectory(Path.Combine(root, "notes"));
		File.WriteAllText(Path.Combine(root, "notes", "hidden.md"), "hidden");
		File.WriteAllText(Path.Combine(root, "notes", "keep.md"), "visible");
		File.WriteAllText(Path.Combine(root, "visible.local"), "visible");
		File.WriteAllText(Path.Combine(root, ".gitignore"), "!visible.local\n!notes/keep.md\n");
		var plan = await BuildBoundaryPlan(root, GitFilteringMode.RespectGitIgnore);
		Assert.DoesNotContain(plan.IncludedFiles, path => path.EndsWith("hidden.md", StringComparison.Ordinal));
		Assert.Contains(plan.IncludedFiles, path => path.EndsWith("visible.local", StringComparison.Ordinal));
		Assert.Contains(plan.IncludedFiles, path => path.EndsWith("keep.md", StringComparison.Ordinal));
	}

	[Fact]
	public async Task RepositoryBoundaries_SharedWorktreeExcludesRemainRelativeToEachOwner()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repository = temp.CreateDirectory("repository");
		temp.CreateFile("repository/anchor.cs", "anchor");
		InitializeIndex(repository, "anchor.cs");
		RunGit(repository, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-qm", "Initial");
		var worktree = Path.Combine(temp.Path, "worktree");
		RunGit(repository, "worktree", "add", "--detach", worktree);
		temp.CreateFile("repository/.git/info/exclude", "*.local\n");
		temp.CreateFile("repository/hidden.local", "hidden");
		temp.CreateFile("worktree/hidden.local", "hidden");
		temp.CreateFile("repository/.gitignore", ".gitignore\n");
		temp.CreateFile("worktree/.gitignore", ".gitignore\n");
		foreach (var root in new[] { repository, worktree, temp.Path, repository })
		{
			var plan = await BuildBoundaryPlan(root, GitFilteringMode.RespectGitIgnore);
			Assert.Equal(root == temp.Path ? 2 : 1, plan.IncludedFiles.Count);
			Assert.All(plan.IncludedFiles, path => Assert.EndsWith("anchor.cs", path));
		}
	}

	[Fact]
	public async Task RepositoryBoundaries_DeclaredUninitializedSubmoduleIsAnOrdinaryEmptyDirectory()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitmodules", "[submodule \"empty\"]\n path = empty\n");
		temp.CreateDirectory("empty");
		InitializeIndex(temp.Path, ".gitmodules");
		var plan = await BuildBoundaryPlan(temp.Path, GitFilteringMode.RespectGitIgnore);
		Assert.Contains(plan.EffectiveTree.Children, node => node.IsDirectory && Path.GetFileName(node.FullPath) == "empty");
	}

	[Fact]
	public async Task RepositoryBoundaries_SubmoduleGitdirUsesItsOwnInfoExclude()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitmodules", "[submodule \"x\"]\n path = third_party/x\n");
		InitializeIndex(temp.Path, ".gitmodules");
		var submodule = temp.CreateDirectory("third_party/x");
		var metadata = Path.Combine(temp.Path, ".git", "modules", "x");
		Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
		RunGit(submodule, "init", "--quiet", "--separate-git-dir", metadata);
		temp.CreateFile("third_party/x/visible.cs", "visible");
		temp.CreateFile("third_party/x/hidden.local", "hidden");
		File.WriteAllText(Path.Combine(metadata, "info", "exclude"), "*.local\n");
		var plan = await BuildBoundaryPlan(temp.Path, GitFilteringMode.RespectGitIgnore);
		Assert.Contains(plan.IncludedFiles, path => path.EndsWith("visible.cs", StringComparison.Ordinal));
		Assert.DoesNotContain(plan.IncludedFiles, path => path.EndsWith("hidden.local", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void RepositoryBoundaries_EmbeddedDirectoryContributesOneGitIgnoreControllerImpact(bool enabled)
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		temp.CreateFile("anchor.cs", "anchor");
		InitializeIndex(temp.Path, "anchor.cs");
		var scanner = new FileSystemScanner();
		var rules = new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>())
		{
			UseGitIgnore = enabled, EnableGitIgnoreTraversal = enabled, GitIgnoreCandidateMatchesActiveRules = enabled
		};
		var before = scanner.GetExtensionsWithIgnoreOptionCounts(temp.Path, rules, TestContext.Current.CancellationToken);
		var beforeBatch = scanner.GetIgnoreSectionSnapshot(temp.Path, rules, rules,
			effectiveAllowedExtensions: null, TestContext.Current.CancellationToken);
		var embedded = temp.CreateDirectory("libs/SomeLib");
		temp.CreateFile("libs/SomeLib/a.cs", "a");
		temp.CreateFile("libs/SomeLib/deep/b.cs", "b");
		InitializeIndex(embedded, "a.cs");
		var after = scanner.GetExtensionsWithIgnoreOptionCounts(temp.Path, rules, TestContext.Current.CancellationToken);
		Assert.Equal(before.Value.ControllerImpactCounts.GitIgnore + 1, after.Value.ControllerImpactCounts.GitIgnore);
		var afterBatch = scanner.GetIgnoreSectionSnapshot(temp.Path, rules, rules,
			effectiveAllowedExtensions: null, TestContext.Current.CancellationToken);
		Assert.Equal(beforeBatch.Value.ControllerImpactCounts.GitIgnore + 1, afterBatch.Value.ControllerImpactCounts.GitIgnore);
		var selectedRoots = scanner.GetProjectWorkspaceSnapshotForRootSelection(temp.Path, ["libs"], rules, rules,
			effectiveExtensionPolicy: null, cancellationToken: TestContext.Current.CancellationToken);
		Assert.Equal(1, selectedRoots.Value.IgnoreSection.ControllerImpactCounts.GitIgnore);
	}

	[Theory]
	[InlineData(GitFilteringMode.RespectGitIgnore)]
	[InlineData(GitFilteringMode.TrackedFilesOnly)]
	public async Task RepositoryBoundaries_InvalidManifestCannotAuthorizeNestedRepository(GitFilteringMode mode)
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		temp.CreateFile("anchor.cs", "anchor");
		temp.CreateFile(".gitmodules", "[submodule \"bad\"]\n path = ../nested\n");
		InitializeIndex(temp.Path, "anchor.cs");
		var nested = temp.CreateDirectory("nested");
		temp.CreateFile("nested/source.cs", "source");
		InitializeIndex(nested, "source.cs");
		var plan = await BuildBoundaryPlan(temp.Path, mode);
		Assert.DoesNotContain(plan.IncludedFiles, path => path.EndsWith("source.cs", StringComparison.Ordinal));
		Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == "DPX-PROJECT-PARTIAL-ACCESS");
	}

	private static Task<ProjectContextPlan> BuildBoundaryPlan(string root, GitFilteringMode mode)
	{
		var analysis = new ProjectAnalysisService(
			new ScanOptionsUseCase(new FileSystemScanner()), ProjectLoadWorkflowRuntime.CreateBuildTreeUseCase(),
			new FilterOptionSelectionService(), ProjectLoadWorkflowRuntime.CreateIgnoreOptionsService(),
			ProjectLoadWorkflowRuntime.CreateIgnoreRulesService(), new TreeExportService(), new FileContentAnalyzer());
		return new ProjectContextPlanner(analysis).BuildAsync(new ProjectContextRequest(root,
			new ProjectSelectionSpec(GitMode: mode, Exclusions: [])), TestContext.Current.CancellationToken);
	}
}
