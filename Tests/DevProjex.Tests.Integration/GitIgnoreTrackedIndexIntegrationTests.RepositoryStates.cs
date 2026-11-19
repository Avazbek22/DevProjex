using DevProjex.Application.Context;

namespace DevProjex.Tests.Integration;

public sealed partial class GitIgnoreTrackedIndexIntegrationTests
{
	[Fact]
	public async Task UnbornRepositoryDistinguishesEmptyStagedAndUntrackedChangesFromAnUnavailableDiff()
	{
		using var repository = new GitModeRepository();
		var provider = new GitScopePathProvider(null, static (_, _) => Task.FromResult<IReadOnlyList<string>>([]));
		var staged = await provider.ResolveAsync(repository.Root, GitFilteringMode.Staged, null, TestContext.Current.CancellationToken);
		Assert.True(staged.IsAvailable, staged.FailureReason);
		Assert.Empty(staged.IncludedPaths);
		repository.Write("untracked.txt", "untracked\n");
		var changes = await provider.ResolveAsync(repository.Root, GitFilteringMode.Changes, null, TestContext.Current.CancellationToken);
		Assert.True(changes.IsAvailable, changes.FailureReason);
		Assert.Equal("untracked.txt", Path.GetFileName(Assert.Single(changes.IncludedPaths)));
		repository.Git("add", "untracked.txt");
		staged = await provider.ResolveAsync(repository.Root, GitFilteringMode.Staged, null, TestContext.Current.CancellationToken);
		Assert.True(staged.IsAvailable, staged.FailureReason);
		Assert.Equal("untracked.txt", Path.GetFileName(Assert.Single(staged.IncludedPaths)));
		var diff = await provider.ResolveAsync(repository.Root, GitFilteringMode.Diff, "HEAD~1..HEAD", TestContext.Current.CancellationToken);
		Assert.False(diff.IsAvailable);
		Assert.Empty(diff.IncludedPaths);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RenameDeletionRestorationAndIntentToAddHaveDistinctStagedAndWorkingScopes(bool detectRenames)
	{
		using var repository = new GitModeRepository();
		repository.Write("old name.txt", "rename payload\n");
		repository.Write("deleted.txt", "deleted\n");
		repository.Write("restored.txt", "restored\n");
		repository.Write("both.txt", "baseline\n");
		repository.Commit();
		repository.Git("config", "diff.renames", detectRenames ? "true" : "false");
		repository.Git("mv", "old name.txt", "renamed ü.txt");
		repository.Git("rm", "--quiet", "deleted.txt", "restored.txt");
		repository.Write("restored.txt", "current restored\n");
		repository.Write("both.txt", "staged\n");
		repository.Git("add", "both.txt");
		repository.Write("both.txt", "worktree\n");
		repository.Write("intent.txt", "intent\n");
		repository.Git("add", "-N", "intent.txt");
		Assert.Contains("intent.txt", repository.Git("diff", "--name-only"), StringComparison.Ordinal);
		Assert.DoesNotContain("intent.txt", repository.Git("diff", "--cached", "--name-only"), StringComparison.Ordinal);
		var provider = new GitScopePathProvider(null, static (_, _) => Task.FromResult<IReadOnlyList<string>>([]));
		foreach (var mode in new[] { GitFilteringMode.Staged, GitFilteringMode.Changes })
		{
			var result = await provider.ResolveAsync(repository.Root, mode, null, TestContext.Current.CancellationToken);
			Assert.True(result.IsAvailable, result.FailureReason);
			var expected = mode == GitFilteringMode.Staged
				? new[] { "both.txt", "renamed ü.txt", "restored.txt" }
				: new[] { "both.txt", "intent.txt", "renamed ü.txt", "restored.txt" };
			Assert.Equal(expected, result.IncludedPaths.Select(Path.GetFileName).Order(StringComparer.Ordinal));
			Assert.Equal(2, result.DeletedPathCount);
		}
	}

	[Fact]
	public async Task ShallowCloneWithoutParentFailsClosedWhileDetachedHeadWithParentRemainsReadable()
	{
		using var repository = new GitModeRepository();
		repository.Write("file.txt", "before\n");
		repository.Commit();
		repository.Write("file.txt", "after\n");
		repository.Commit();
		var clone = Path.Combine(Path.GetDirectoryName(repository.Root)!, "shallow");
		repository.Git("clone", "--quiet", "--depth", "1", new Uri(repository.Root + Path.DirectorySeparatorChar).AbsoluteUri, clone);
		Assert.Equal("true", repository.GitAt(clone, "rev-parse", "--is-shallow-repository").Trim());
		var provider = new GitScopePathProvider(null, static (_, _) => Task.FromResult<IReadOnlyList<string>>([]));
		var missing = await provider.ResolveAsync(clone, GitFilteringMode.Diff, "HEAD~1..HEAD", TestContext.Current.CancellationToken);
		Assert.False(missing.IsAvailable);
		Assert.Empty(missing.IncludedPaths);
		repository.Git("checkout", "--quiet", "--detach", "HEAD");
		var detached = await provider.ResolveAsync(repository.Root, GitFilteringMode.Diff, "HEAD~1..HEAD", TestContext.Current.CancellationToken);
		Assert.True(detached.IsAvailable, detached.FailureReason);
		Assert.Equal("file.txt", Path.GetFileName(Assert.Single(detached.IncludedPaths)));
	}

	[Fact]
	public async Task IndexFlagsAndSparseCheckoutDoNotInventWorkingTreeChanges()
	{
		using var repository = new GitModeRepository();
		repository.Write("visible/changed.txt", "baseline\n");
		repository.Write("visible/assumed.txt", "baseline\n");
		repository.Write("visible/skipped.txt", "baseline\n");
		repository.Write("hidden/absent.txt", "baseline\n");
		repository.Commit();
		repository.Git("sparse-checkout", "init", "--cone");
		repository.Git("sparse-checkout", "set", "visible");
		Assert.False(File.Exists(Path.Combine(repository.Root, "hidden", "absent.txt")));
		var provider = new GitScopePathProvider(null, static (_, _) => Task.FromResult<IReadOnlyList<string>>([]));
		var sparse = await provider.ResolveAsync(repository.Root, GitFilteringMode.Changes, null, TestContext.Current.CancellationToken);
		Assert.True(sparse.IsAvailable, sparse.FailureReason);
		Assert.Empty(sparse.IncludedPaths);
		Assert.Equal(0, sparse.DeletedPathCount);
		repository.Git("sparse-checkout", "disable");
		repository.Git("update-index", "--assume-unchanged", "visible/assumed.txt");
		repository.Git("update-index", "--skip-worktree", "visible/skipped.txt");
		repository.Write("visible/changed.txt", "changed content\n");
		repository.Write("visible/assumed.txt", "ignored by index flag\n");
		repository.Write("visible/skipped.txt", "ignored by index flag\n");
		Assert.Equal("visible/changed.txt", repository.Git("diff", "--name-only").Trim());
		var result = await provider.ResolveAsync(repository.Root, GitFilteringMode.Changes, null, TestContext.Current.CancellationToken);
		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Equal("changed.txt", Path.GetFileName(Assert.Single(result.IncludedPaths)));
		Assert.Equal(0, result.DeletedPathCount);
	}

	[Fact]
	public async Task WorktreeUsesItsOwnIndexAndRetainsSharedRepositoryHistory()
	{
		using var repository = new GitModeRepository();
		repository.Write("file.txt", "baseline\n");
		repository.Commit();
		var worktree = Path.Combine(Path.GetDirectoryName(repository.Root)!, "linked");
		repository.Git("worktree", "add", "--quiet", "--detach", worktree, "HEAD");
		File.WriteAllText(Path.Combine(worktree, "file.txt"), "worktree edit\n");
		repository.GitAt(worktree, "add", "file.txt");
		var provider = new GitScopePathProvider(null, static (_, _) => Task.FromResult<IReadOnlyList<string>>([]));
		var original = await provider.ResolveAsync(repository.Root, GitFilteringMode.Staged, null, TestContext.Current.CancellationToken);
		var linked = await provider.ResolveAsync(worktree, GitFilteringMode.Staged, null, TestContext.Current.CancellationToken);
		Assert.True(original.IsAvailable, original.FailureReason);
		Assert.True(linked.IsAvailable, linked.FailureReason);
		Assert.Empty(original.IncludedPaths);
		Assert.Equal(Path.Combine(worktree, "file.txt"), Assert.Single(linked.IncludedPaths));
	}

	[Fact]
	public async Task GitIgnoreNegationsAndNestedRulesAgreeWithNativeUntrackedDiscovery()
	{
		using var repository = new GitModeRepository();
		repository.Write(".gitignore", "*.tmp\n!keep.tmp\ncache/*\n!cache/keep.txt\n");
		repository.Write("nested/.gitignore", "*.txt\n!visible.txt\n");
		repository.Commit();
		foreach (var path in new[] { "drop.tmp", "keep.tmp", "cache/drop.txt", "cache/keep.txt", "nested/drop.txt", "nested/visible.txt", "ordinary.txt" })
			repository.Write(path, "untracked\n");
		var expected = repository.Git("ls-files", "--others", "--exclude-standard", "-z").Split('\0', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray();
		Assert.Equal(["cache/keep.txt", "keep.tmp", "nested/visible.txt", "ordinary.txt"], expected);
		var result = await new GitScopePathProvider(null, static (_, _) => Task.FromResult<IReadOnlyList<string>>([]))
			.ResolveAsync(repository.Root, GitFilteringMode.Changes, null, TestContext.Current.CancellationToken);
		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Equal(expected, result.IncludedPaths.Select(path => Path.GetRelativePath(repository.Root, path).Replace('\\', '/')).Order(StringComparer.Ordinal));
	}

	[Fact]
	public async Task StagedPathNamesSurviveSpacesUnicodeLeadingDashAndLongDirectories()
	{
		using var repository = new GitModeRepository();
		var names = new[] { "with spaces.txt", "-leading.txt", "café.txt", "日本語.txt", "emoji-😀.txt", new string('a', 70) + "/" + new string('b', 70) + "/long.txt" };
		foreach (var name in names)
			repository.Write(name, "baseline\n");
		repository.Commit();
		foreach (var name in names)
			repository.Write(name, "changed content\n");
		repository.Git("add", "--all");
		var native = repository.Git("diff", "--cached", "--name-only", "-z").Split('\0', StringSplitOptions.RemoveEmptyEntries);
		Assert.Equal(names.Length, native.Length);
		var result = await new GitScopePathProvider().ResolveAsync(repository.Root, GitFilteringMode.Staged, null, TestContext.Current.CancellationToken);
		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Equal(native.Select(static path => path.Normalize(NormalizationForm.FormC)).Order(StringComparer.Ordinal),
			result.IncludedPaths.Select(path => Path.GetRelativePath(repository.Root, path).Replace('\\', '/').Normalize(NormalizationForm.FormC)).Order(StringComparer.Ordinal));
	}

	[Fact]
	public async Task PureCaseRenameDoesNotDuplicateCurrentFilesOrDeletedDiagnostics()
	{
		using var repository = new GitModeRepository();
		repository.Write("Original.txt", "same content\n");
		repository.Commit();
		repository.Git("mv", "Original.txt", "temporary.txt");
		repository.Git("mv", "temporary.txt", "original.txt");
		var result = await new GitScopePathProvider().ResolveAsync(repository.Root, GitFilteringMode.Staged, null, TestContext.Current.CancellationToken);
		Assert.True(result.IsAvailable, result.FailureReason);
		var index = new GitConfigPathComparisonSemanticsResolver().Resolve(repository.Root);
		var planner = new ProjectContextPlanner(CreateProjectAnalysisService());
		var plan = await planner.BuildStructureAsync(new ProjectContextRequest(repository.Root,
			ProjectSelectionSpec.Standard with { GitMode = GitFilteringMode.Staged, Exclusions = [] }), TestContext.Current.CancellationToken);
		plan = await GitScopeFilter.ApplyAsync(planner, plan, new GitScopePathProvider(), TestContext.Current.CancellationToken);
		Assert.False(plan.HasErrors);
		Assert.Equal("original.txt", Path.GetFileName(Assert.Single(plan.IncludedFiles)));
		Assert.Equal(index.IgnoreCase ? 0 : 1, result.DeletedPathCount);
	}

	[Fact]
	public async Task PromisorRepositoryWithMissingBlobsKeepsStagedInspectionOffline()
	{
		using var repository = new GitModeRepository();
		repository.Write("file.txt", "committed blob\n");
		repository.Commit();
		repository.Git("config", "uploadpack.allowFilter", "true");
		var clone = Path.Combine(Path.GetDirectoryName(repository.Root)!, "partial");
		repository.Git("clone", "--quiet", "--no-checkout", "--filter=blob:none", new Uri(repository.Root + Path.DirectorySeparatorChar).AbsoluteUri, clone);
		Assert.Contains("?", repository.GitAt(clone, "rev-list", "--objects", "--missing=print", "HEAD"), StringComparison.Ordinal);
		repository.GitAt(clone, "read-tree", "HEAD");
		repository.GitAt(clone, "config", "remote.origin.url", Path.Combine(clone, "missing-origin"));
		var result = await new GitScopePathProvider().ResolveAsync(clone, GitFilteringMode.Staged, null, TestContext.Current.CancellationToken);
		if (GitRuntime.IsAtLeastVersion(2, 45))
		{
			Assert.True(result.IsAvailable, result.FailureReason);
			Assert.Empty(result.IncludedPaths);
		}
		else
		{
			Assert.False(result.IsAvailable);
		}
		Assert.Contains("?", repository.GitAt(clone, "rev-list", "--objects", "--missing=print", "HEAD"), StringComparison.Ordinal);
	}

	[Fact]
	public async Task StagedExecutableBitChangeRemainsAFileScopeOnEveryPlatform()
	{
		using var repository = new GitModeRepository();
		repository.Write("script.sh", "echo fixture\n");
		repository.Commit();
		repository.Git("update-index", "--chmod=+x", "script.sh");
		Assert.Contains("100755", repository.Git("ls-files", "--stage"), StringComparison.Ordinal);
		Assert.Equal("script.sh", repository.Git("diff", "--cached", "--name-only").Trim());
		var result = await new GitScopePathProvider().ResolveAsync(repository.Root, GitFilteringMode.Staged, null, TestContext.Current.CancellationToken);
		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Equal("script.sh", Path.GetFileName(Assert.Single(result.IncludedPaths)));
	}

	[Fact]
	public async Task SymbolicLinkIndexEntryRemainsOnePathWithoutExpandingItsTarget()
	{
		using var repository = new GitModeRepository();
		repository.Write("target.txt", "target\n");
		repository.Commit();
		var linkPath = Path.Combine(repository.Root, "linked.txt");
		try
		{
			File.CreateSymbolicLink(linkPath, "target.txt");
		}
		catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException ||
										 exception is IOException && (exception.HResult & 0xffff) == 1314)
		{
			Assert.Skip("The filesystem cannot create symbolic links: " + exception.Message);
		}
		repository.Git("config", "core.symlinks", "true");
		repository.Git("add", "linked.txt");
		Assert.Contains("120000", repository.Git("ls-files", "--stage", "linked.txt"), StringComparison.Ordinal);
		var result = await new GitScopePathProvider().ResolveAsync(repository.Root, GitFilteringMode.Staged, null, TestContext.Current.CancellationToken);
		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Equal("linked.txt", Path.GetFileName(Assert.Single(result.IncludedPaths)));
	}

	[Theory]
	[InlineData(GitFilteringMode.Staged)]
	[InlineData(GitFilteringMode.Changes)]
	[InlineData(GitFilteringMode.Diff)]
	public async Task UnmergedIndexSelectsTheCurrentConflictFileOnlyOnce(GitFilteringMode mode)
	{
		using var repository = new GitModeRepository();
		repository.WriteMergeConflict();
		Assert.True(GitTrackedPathIndexCache.TryLoad(repository.Root, Path.Combine(repository.Root, ".git"),
			TestContext.Current.CancellationToken, out var tracked));
		Assert.Equal(2, tracked.Count);
		var provider = new GitScopePathProvider(null, static (_, _) => Task.FromResult<IReadOnlyList<string>>([]));
		var result = await provider.ResolveAsync(repository.Root, mode, mode == GitFilteringMode.Diff ? "HEAD~1..HEAD" : null,
			TestContext.Current.CancellationToken);
		Assert.True(result.IsAvailable, result.FailureReason);
		Assert.Equal("conflict.txt", Path.GetFileName(Assert.Single(result.IncludedPaths)));
		Assert.Equal(0, result.DeletedPathCount);
	}

	[Theory]
	[InlineData(GitFilteringMode.Staged)]
	[InlineData(GitFilteringMode.Changes)]
	[InlineData(GitFilteringMode.Diff)]
	public async Task CorruptIndexCannotPublishPartialStateAndRepairRecoversWithoutRestart(GitFilteringMode mode)
	{
		using var repository = new GitModeRepository();
		repository.Write("selected.txt", "baseline\n");
		repository.Commit();
		repository.Write("selected.txt", "second revision\n");
		repository.Commit();
		repository.Write("selected.txt", "staged content\n");
		repository.Git("add", "selected.txt");
		var indexPath = Path.Combine(repository.Root, ".git", "index");
		var original = File.ReadAllBytes(indexPath);
		var timestamp = File.GetLastWriteTimeUtc(indexPath);
		var provider = new GitScopePathProvider(null, static (_, _) => Task.FromResult<IReadOnlyList<string>>([]));
		File.WriteAllBytes(indexPath, Encoding.ASCII.GetBytes("not an index"));
		var corrupted = await provider.ResolveAsync(repository.Root, mode, mode == GitFilteringMode.Diff ? "HEAD~1..HEAD" : null,
			TestContext.Current.CancellationToken);
		if (mode == GitFilteringMode.Diff)
		{
			Assert.True(corrupted.IsAvailable, corrupted.FailureReason);
			Assert.Equal("selected.txt", Path.GetFileName(Assert.Single(corrupted.IncludedPaths)));
		}
		else
		{
			Assert.False(corrupted.IsAvailable);
			Assert.Empty(corrupted.IncludedPaths);
		}
		File.WriteAllBytes(indexPath, original);
		File.SetLastWriteTimeUtc(indexPath, timestamp);
		var repaired = await provider.ResolveAsync(repository.Root, mode, mode == GitFilteringMode.Diff ? "HEAD~1..HEAD" : null,
			TestContext.Current.CancellationToken);
		Assert.True(repaired.IsAvailable, repaired.FailureReason);
		Assert.Equal("selected.txt", Path.GetFileName(Assert.Single(repaired.IncludedPaths)));
	}

	[Fact]
	public async Task CancellationDuringLineEndingDiscoveryDoesNotPublishAnEmptySuccessfulScope()
	{
		using var repository = new GitModeRepository();
		repository.Write("file.txt", "baseline\n");
		repository.Commit();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var provider = new GitScopePathProvider(null, async (_, token) =>
		{
			entered.TrySetResult();
			return await release.Task.WaitAsync(token);
		});
		var pending = provider.ResolveAsync(repository.Root, GitFilteringMode.Changes, null, cancellation.Token);
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
		await cancellation.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
		release.TrySetResult([]);
		var resumed = await provider.ResolveAsync(repository.Root, GitFilteringMode.Changes, null, TestContext.Current.CancellationToken);
		Assert.True(resumed.IsAvailable, resumed.FailureReason);
		Assert.Empty(resumed.IncludedPaths);
	}
}
