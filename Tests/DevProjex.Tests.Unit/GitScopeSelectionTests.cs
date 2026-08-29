using DevProjex.Application.Context;
using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit;

public sealed class GitScopeSelectionTests
{
	[Theory]
	[InlineData("none", GitFilteringMode.None, null)]
	[InlineData("gitignore", GitFilteringMode.RespectGitIgnore, null)]
	[InlineData("tracked", GitFilteringMode.TrackedFilesOnly, null)]
	[InlineData("staged", GitFilteringMode.Staged, null)]
	[InlineData("changes", GitFilteringMode.Changes, null)]
	[InlineData("diff:main..feature/x", GitFilteringMode.Diff, "main..feature/x")]
	public void TryParseAcceptsTheCompleteGitAxis(
		string token,
		GitFilteringMode expectedMode,
		string? expectedRange)
	{
		Assert.True(GitScopeSelection.TryParse(token, out var mode, out var range));
		Assert.Equal(expectedMode, mode);
		Assert.Equal(expectedRange, range);
	}

	[Theory]
	[InlineData("diff:")]
	[InlineData("diff:main..")]
	[InlineData("diff:..feature")]
	[InlineData("diff:main...feature")]
	[InlineData("diff:-main..feature")]
	[InlineData("diff:main..feature branch")]
	public void TryParseRejectsInvalidDiffRanges(string token) =>
		Assert.False(GitScopeSelection.TryParse(token, out _, out _));

	[Theory]
	[InlineData(GitFilteringMode.Staged, IgnoreOptionId.TrackedGitFilesOnly)]
	[InlineData(GitFilteringMode.Diff, IgnoreOptionId.TrackedGitFilesOnly)]
	[InlineData(GitFilteringMode.Changes, IgnoreOptionId.UseGitIgnore)]
	public void MomentaryModesUseTheRequiredScannerUnderlay(
		GitFilteringMode mode,
		IgnoreOptionId expectedOption)
	{
		var options = ProjectSelectionAdapter.ToIgnoreOptions(
			ProjectSelectionSpec.Standard with { GitMode = mode, GitDiffRange = mode == GitFilteringMode.Diff ? "a..b" : null });

		Assert.Contains(expectedOption, options);
		Assert.False(options.Contains(IgnoreOptionId.UseGitIgnore) &&
		             options.Contains(IgnoreOptionId.TrackedGitFilesOnly));
	}

	[Fact]
	public void NameStatusParserHandlesDeletesAndRenamesWithoutLeakingTheOldPath()
	{
		var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dpx-git-scope-parser"));
		var included = new HashSet<string>(PathComparer.Default);
		var deleted = new HashSet<string>(PathComparer.Default);

		var parsed = GitScopePathProvider.TryParseNameStatus(
			["M", "src/changed.cs", "D", "src/deleted.cs", "R100", "old.cs", "new.cs"],
			root,
			root,
			included,
			deleted);

		Assert.True(parsed);
		Assert.Equal(
			[Path.Combine(root, "new.cs"), Path.Combine(root, "src", "changed.cs")],
			included.Order(PathComparer.Default));
		Assert.Equal(
			[Path.Combine(root, "old.cs"), Path.Combine(root, "src", "deleted.cs")],
			deleted.Order(PathComparer.Default));
	}

	[Fact]
	public void TreeNarrowingKeepsOnlyExistingScopedFiles()
	{
		var rootPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dpx-git-scope-tree"));
		var kept = Path.Combine(rootPath, "kept.cs");
		var dropped = Path.Combine(rootPath, "dropped.cs");
		var root = new TreeNodeDescriptor(
			"project",
			rootPath,
			true,
			false,
			"folder",
			[
				new TreeNodeDescriptor("kept.cs", kept, false, false, "csharp", []),
				new TreeNodeDescriptor("dropped.cs", dropped, false, false, "csharp", [])
			]);

		var narrowed = GitScopeFilter.ApplyToTree(
			new BuildTreeResult(root, false, false, [kept, dropped]),
			new GitScopePathResult(
				true,
				new HashSet<string>([kept, Path.Combine(rootPath, "missing.cs")], PathComparer.Default),
				0),
			TestContext.Current.CancellationToken);

		Assert.Equal([kept], narrowed.OrderedFilePaths);
		Assert.Equal("kept.cs", Assert.Single(narrowed.Root.Children).DisplayName);
	}
}
