using DevProjex.Application.Context;
using DevProjex.Application.Services;
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

	[Fact]
	public void TryParseRejectsTokensThatCannotFitPortableProcessArguments()
	{
		var token = GitScopeSelection.DiffPrefix +
		            new string('a', GitScopeSelection.MaximumTokenLength) +
		            "..HEAD";

		Assert.False(GitScopeSelection.TryParse(token, out _, out _));
	}

	[Theory]
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

	[Theory]
	[InlineData(GitFilteringMode.Staged, null)]
	[InlineData(GitFilteringMode.Diff, "main..feature")]
	public void ReferenceBackedScopeDoesNotFilterAgainstTheCurrentIndexBeforeNarrowing(
		GitFilteringMode mode,
		string? diffRange)
	{
		var options = ProjectSelectionAdapter.ToIgnoreOptions(
			ProjectSelectionSpec.Standard with
			{
				GitMode = mode,
				GitDiffRange = diffRange
			});

		Assert.DoesNotContain(IgnoreOptionId.UseGitIgnore, options);
		Assert.DoesNotContain(IgnoreOptionId.TrackedGitFilesOnly, options);
	}

	[Theory]
	[InlineData(GitFilteringMode.None, GitFilteringMode.Staged, GitFilteringMode.None)]
	[InlineData(GitFilteringMode.None, GitFilteringMode.Changes, GitFilteringMode.RespectGitIgnore)]
	[InlineData(GitFilteringMode.None, GitFilteringMode.Diff, GitFilteringMode.None)]
	[InlineData(GitFilteringMode.RespectGitIgnore, GitFilteringMode.Staged, GitFilteringMode.RespectGitIgnore)]
	[InlineData(GitFilteringMode.RespectGitIgnore, GitFilteringMode.Changes, GitFilteringMode.RespectGitIgnore)]
	[InlineData(GitFilteringMode.RespectGitIgnore, GitFilteringMode.Diff, GitFilteringMode.RespectGitIgnore)]
	[InlineData(GitFilteringMode.TrackedFilesOnly, GitFilteringMode.Staged, GitFilteringMode.TrackedFilesOnly)]
	[InlineData(GitFilteringMode.TrackedFilesOnly, GitFilteringMode.Changes, GitFilteringMode.TrackedFilesOnly)]
	[InlineData(GitFilteringMode.TrackedFilesOnly, GitFilteringMode.Diff, GitFilteringMode.TrackedFilesOnly)]
	public void NarrowingUnderlayNeverExpandsThePersistentBaseline(
		GitFilteringMode baseline,
		GitFilteringMode scope,
		GitFilteringMode expected) =>
		Assert.Equal(expected, GitScopeSelection.ComposeNarrowingUnderlay(baseline, scope));

	[Fact]
	public void SelectionRejectsAnUndefinedGitMode() =>
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			GitScopeSelection.WithMode(ProjectSelectionSpec.Standard, (GitFilteringMode)int.MaxValue));

	[Fact]
	public void SelectionAdapterRejectsAnUndefinedGitMode() =>
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			ProjectSelectionAdapter.ToIgnoreOptions(
				ProjectSelectionSpec.Standard with { GitMode = (GitFilteringMode)int.MaxValue }));

	[Fact]
	public void RepositoryBoundaryEvidenceDoesNotPretendThatATrackedIndexWasLoaded()
	{
		var baseline = ProjectContextGitReadiness.Evaluate(
			GitFilteringMode.None,
			discoveredTrackedIndexCount: 0,
			unavailableTrackedIndexCount: 0,
			hasRepositoryBoundaryEvidence: true);
		var tracked = ProjectContextGitReadiness.Evaluate(
			GitFilteringMode.TrackedFilesOnly,
			discoveredTrackedIndexCount: 0,
			unavailableTrackedIndexCount: 0,
			hasRepositoryBoundaryEvidence: true);

		Assert.True(baseline.HasRepositoryBoundary);
		Assert.True(baseline.IsReady);
		Assert.True(tracked.HasRepositoryBoundary);
		Assert.False(tracked.IsReady);
		Assert.Equal(
			ProjectContextGitReadiness.UnavailableDiagnosticCode,
			tracked.CreateDiagnostic("project")?.Code);
	}

	[Fact]
	public void InventoryRepositoryBoundaryContributesReadinessWithoutATrackedIndex()
	{
		var inventory = new ProjectTreeInventorySnapshot(
			[],
			rootAccessDenied: false,
			hadAccessDenied: false,
			discoveredGitRepositoryRoots: [Path.GetFullPath("repository")]);

		var readiness = ProjectContextGitReadiness.Evaluate(
			GitFilteringMode.None,
			inventory);

		Assert.True(readiness.HasRepositoryBoundary);
		Assert.True(readiness.IsReady);
		Assert.Equal(0, readiness.LoadedTrackedIndexCount);
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
	public void GitScopeDiffCommandsDisableExternalDriversAndTextConversion()
	{
		var staged = GitScopePathProvider.CreateDiffArguments(cached: true, diffRange: null);
		var unstaged = GitScopePathProvider.CreateDiffArguments(cached: false, diffRange: null);
		var compared = GitScopePathProvider.CreateDiffArguments(cached: false, diffRange: "main..feature");

		foreach (var arguments in new[] { staged, unstaged, compared })
		{
			Assert.Contains("--no-ext-diff", arguments);
			Assert.Contains("--no-textconv", arguments);
			Assert.Equal("--", arguments[^1]);
		}
		Assert.Contains("--cached", staged);
		Assert.DoesNotContain("--cached", unstaged);
		Assert.Contains("main..feature", compared);
	}

	[Fact]
	public void GitScopeProcessDisablesFsMonitorAndLazyFetch()
	{
		var repositoryRoot = Path.GetFullPath("repository");
		var startInfo = GitScopePathProvider.CreateStartInfo(
			repositoryRoot,
			GitScopePathProvider.CreateDiffArguments(cached: false, diffRange: null));
		var arguments = startInfo.ArgumentList.ToArray();
		var quotePathIndex = Array.IndexOf(arguments, "core.quotepath=false");
		var fsMonitorIndex = Array.IndexOf(arguments, "core.fsmonitor=false");
		var diffIndex = Array.IndexOf(arguments, "diff");

		Assert.True(quotePathIndex > 0);
		Assert.Equal("-c", arguments[quotePathIndex - 1]);
		Assert.True(fsMonitorIndex > quotePathIndex);
		Assert.Equal("-c", arguments[fsMonitorIndex - 1]);
		Assert.True(diffIndex > fsMonitorIndex);
		Assert.Contains(repositoryRoot, arguments);
		Assert.Equal("0", startInfo.Environment["GIT_OPTIONAL_LOCKS"]);
		Assert.Equal("1", startInfo.Environment["GIT_NO_LAZY_FETCH"]);
	}

	[Fact]
	public void ChangesOutputBudgetIsAtomicAcrossConcurrentReaders()
	{
		var budget = new GitScopePathProvider.GitScopeOutputBudget(64);
		var reservations = 0;

		Parallel.For(0, 128, _ =>
		{
			if (budget.TryReserve(1))
				Interlocked.Increment(ref reservations);
		});

		Assert.Equal(64, reservations);
		Assert.Equal(0, budget.RemainingBytes);
		Assert.False(budget.TryReserve(1));
	}

	[Fact]
	public void NameStatusParserPreservesUnicodeSpacesWhitespaceAndCopySources()
	{
		using var project = new TemporaryDirectory();
		var root = project.Path;
		var included = new HashSet<string>(PathComparer.Default);
		var deleted = new HashSet<string>(PathComparer.Default);

		var parsed = GitScopePathProvider.TryParseNameStatus(
			[
				"M", "src/file name.cs",
				"A", " ",
				"R087", "old/日本語.cs", "new/日本語 renamed.cs",
				"C100", "source/копия.cs", "copy/копия.cs"
			],
			root,
			root,
			included,
			deleted);

		Assert.True(parsed);
		var expected = new List<string>
		{
			Path.Combine(root, "copy", "копия.cs"),
			Path.Combine(root, "new", "日本語 renamed.cs"),
			Path.Combine(root, "src", "file name.cs")
		};
		if (!OperatingSystem.IsWindows())
			expected.Add(Path.Combine(root, " "));

		Assert.Equal(expected.Order(PathComparer.Default), included.Order(PathComparer.Default));
		Assert.Equal([Path.Combine(root, "old", "日本語.cs")], deleted);
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

	[Fact]
	public void TreeNarrowingUsesRepositoryPathIdentityWithoutChangingSetSemantics()
	{
		var rootPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dpx-git-scope-identity"));
		var gitPath = Path.Combine(rootPath, "MixedCase.cs");
		var inventoriedPath = Path.Combine(rootPath, "mixedcase.cs");
		var root = new TreeNodeDescriptor(
			"project",
			rootPath,
			true,
			false,
			"folder",
			[new TreeNodeDescriptor("mixedcase.cs", inventoriedPath, false, false, "csharp", [])]);
		var scope = new GitScopePathResult(
			true,
			new HashSet<string>([gitPath], StringComparer.Ordinal),
			0,
			PathMatchers:
			[
				new GitTrackedPathIndex(
					rootPath,
					["MixedCase.cs"],
					new GitPathComparisonSemantics(IgnoreCase: true, NormalizeUnicode: false))
			]);

		var narrowed = GitScopeFilter.ApplyToTree(
			new BuildTreeResult(root, false, false, [inventoriedPath]),
			scope,
			TestContext.Current.CancellationToken);

		Assert.DoesNotContain(inventoriedPath, scope.IncludedPaths);
		Assert.True(scope.ContainsPath(inventoriedPath));
		Assert.Equal([inventoriedPath], narrowed.OrderedFilePaths);
	}

	[Fact]
	public void RepositoryMatcherDoesNotAllowPlatformComparerToOverrideCaseSensitiveGit()
	{
		var rootPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dpx-git-scope-case-sensitive"));
		var exactPath = Path.Combine(rootPath, "MixedCase.cs");
		var differentlyCasedPath = Path.Combine(rootPath, "mixedcase.cs");
		var scope = new GitScopePathResult(
			true,
			new HashSet<string>([exactPath], PathComparer.Default),
			0,
			PathMatchers:
			[
				new GitTrackedPathIndex(
					rootPath,
					["MixedCase.cs"],
					new GitPathComparisonSemantics(IgnoreCase: false, NormalizeUnicode: false))
			]);

		Assert.True(scope.ContainsPath(exactPath));
		Assert.False(scope.ContainsPath(differentlyCasedPath));
	}

	[Fact]
	public void TreeNarrowingPreservesCaseDistinctPathsForCaseSensitiveGit()
	{
		var rootPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dpx-git-scope-case-tree"));
		var upperPath = Path.Combine(rootPath, "Foo.cs");
		var lowerPath = Path.Combine(rootPath, "foo.cs");
		var root = new TreeNodeDescriptor(
			"project",
			rootPath,
			true,
			false,
			"folder",
			[
				new TreeNodeDescriptor("Foo.cs", upperPath, false, false, "csharp", []),
				new TreeNodeDescriptor("foo.cs", lowerPath, false, false, "csharp", [])
			]);
		var scope = new GitScopePathResult(
			true,
			new HashSet<string>([upperPath, lowerPath], StringComparer.Ordinal),
			0,
			PathMatchers:
			[
				new GitTrackedPathIndex(
					rootPath,
					["Foo.cs", "foo.cs"],
					new GitPathComparisonSemantics(IgnoreCase: false, NormalizeUnicode: false))
			]);

		var narrowed = GitScopeFilter.ApplyToTree(
			new BuildTreeResult(root, false, false, [upperPath, lowerPath]),
			scope,
			TestContext.Current.CancellationToken);

		Assert.Equal([upperPath, lowerPath], narrowed.OrderedFilePaths, StringComparer.Ordinal);
		Assert.Equal(["Foo.cs", "foo.cs"], narrowed.Root.Children.Select(static child => child.DisplayName));
	}

	[Fact]
	public void ExplicitRootSelectionDoesNotResolveUnselectedRepositoryBoundaries()
	{
		var sourceRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dpx-git-scope-roots"));
		var firstRepository = Path.Combine(sourceRoot, "first");
		var secondRepository = Path.Combine(sourceRoot, "second");
		var inventory = new ProjectTreeInventorySnapshot(
			[],
			rootAccessDenied: false,
			hadAccessDenied: false,
			discoveredGitRepositoryRoots: [firstRepository, secondRepository]);

		var roots = GitScopeFilter.GetDiscoveredRepositoryRoots(
			inventory,
			sourceRoot,
			["first"],
			rootSelectionIsExplicit: true);

		Assert.Equal([firstRepository], roots, PathComparer.Default);
	}

	[Fact]
	public void ExplicitPathSelectionDoesNotResolveUnrelatedRepositoryBoundaries()
	{
		var sourceRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dpx-git-scope-paths"));
		var firstRepository = Path.Combine(sourceRoot, "first");
		var secondRepository = Path.Combine(sourceRoot, "second");
		var inventory = new ProjectTreeInventorySnapshot(
			[],
			rootAccessDenied: false,
			hadAccessDenied: false,
			discoveredGitRepositoryRoots: [firstRepository, secondRepository]);

		var roots = GitScopeFilter.GetDiscoveredRepositoryRoots(
			inventory,
			sourceRoot,
			selectedRootFolders: [],
			rootSelectionIsExplicit: false,
			selectedFullPaths: [Path.Combine(firstRepository, "src", "selected.cs")]);

		Assert.Equal([firstRepository], roots, PathComparer.Default);
	}

	[Fact]
	public void ExplicitPathSelectionUsesTheDeepestContainingRepositoryBoundary()
	{
		var sourceRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dpx-git-scope-owner"));
		var nestedRepository = Path.Combine(sourceRoot, "nested");
		var inventory = new ProjectTreeInventorySnapshot(
			[],
			rootAccessDenied: false,
			hadAccessDenied: false,
			discoveredGitRepositoryRoots: [sourceRoot, nestedRepository]);

		var roots = GitScopeFilter.GetDiscoveredRepositoryRoots(
			inventory,
			sourceRoot,
			selectedRootFolders: [],
			rootSelectionIsExplicit: false,
			selectedFullPaths: [Path.Combine(nestedRepository, "src", "App.cs")]);

		Assert.Equal([nestedRepository], roots, PathComparer.Default);
	}

	[Fact]
	public void ExplicitDirectorySelectionKeepsItsOwnerAndRepositoriesNestedInsideIt()
	{
		var sourceRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dpx-git-scope-subtree"));
		var selectedDirectory = Path.Combine(sourceRoot, "src");
		var nestedRepository = Path.Combine(selectedDirectory, "nested");
		var unrelatedRepository = Path.Combine(sourceRoot, "docs", "samples");
		var inventory = new ProjectTreeInventorySnapshot(
			[],
			rootAccessDenied: false,
			hadAccessDenied: false,
			discoveredGitRepositoryRoots: [sourceRoot, nestedRepository, unrelatedRepository]);

		var roots = GitScopeFilter.GetDiscoveredRepositoryRoots(
			inventory,
			sourceRoot,
			["src"],
			rootSelectionIsExplicit: true);

		Assert.Equal(
			[sourceRoot, nestedRepository],
			roots,
			PathComparer.Default);
	}

	[Fact]
	public void RepositoryDiscoveryPreservesCaseDistinctPhysicalRootsBeforeGitResolution()
	{
		var sourceRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dpx-git-scope-case-roots"));
		var upperRepository = Path.Combine(sourceRoot, "Repo");
		var lowerRepository = Path.Combine(sourceRoot, "repo");
		var inventory = new ProjectTreeInventorySnapshot(
			[],
			rootAccessDenied: false,
			hadAccessDenied: false,
			discoveredGitRepositoryRoots: [lowerRepository, upperRepository]);

		var roots = GitScopeFilter.GetDiscoveredRepositoryRoots(inventory);

		Assert.Equal([upperRepository, lowerRepository], roots, StringComparer.Ordinal);
	}

	[Fact]
	public void TreeNarrowingDoesNotExpandScopedDirectoryPaths()
	{
		var rootPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dpx-git-scope-gitlink"));
		var gitlinkPath = Path.Combine(rootPath, "submodule");
		var nestedFile = Path.Combine(gitlinkPath, "nested.cs");
		var root = new TreeNodeDescriptor(
			"project",
			rootPath,
			true,
			false,
			"folder",
			[
				new TreeNodeDescriptor(
					"submodule",
					gitlinkPath,
					true,
					false,
					"folder",
					[new TreeNodeDescriptor("nested.cs", nestedFile, false, false, "csharp", [])])
			]);

		var narrowed = GitScopeFilter.ApplyToTree(
			new BuildTreeResult(root, false, false, [nestedFile]),
			new GitScopePathResult(
				true,
				new HashSet<string>([gitlinkPath], PathComparer.Default),
				0),
			TestContext.Current.CancellationToken);

		Assert.Empty(narrowed.OrderedFilePaths!);
		Assert.Empty(narrowed.Root.Children);
	}

	[Fact]
	public void EmptyScopeDoesNotAdvertiseUnrelatedExtensionsOrIgnoreImpacts()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("regular.cs", "class Regular {}\n");
		project.CreateFile(".generated/hidden.cs", "class Hidden {}\n");
		project.CreateFile(".metadata", "value\n");
		project.CreateFile("NOTICE", "value\n");
		var (inventory, rules, roots) = BuildInventory(project.Path);

		var projection = GitScopePresentationProjector.Build(
			project.Path,
			inventory,
			new HashSet<string>(PathComparer.Default),
			roots,
			roots,
			ExtensionPolicy(".cs", ".metadata"),
			rules,
			TestContext.Current.CancellationToken);

		Assert.Empty(projection.AvailableExtensions);
		Assert.Equal(IgnoreOptionCounts.Empty, projection.IgnoreOptionCounts);
		Assert.Equal(IgnoreControllerImpactCounts.Empty, projection.ControllerImpactCounts);
	}

	[Fact]
	public void ScopeWithoutRequestedImpactCountsStillProjectsAvailableExtensions()
	{
		using var project = new TemporaryDirectory();
		var regular = PathUtility.Normalize(project.CreateFile("regular.cs", "class Regular {}\n"));
		var hidden = PathUtility.Normalize(project.CreateFile(".hidden.cs", "class Hidden {}\n"));
		var (inventory, rules, roots) = BuildInventory(project.Path);

		var projection = GitScopePresentationProjector.Build(
			project.Path,
			inventory,
			new HashSet<string>([regular, hidden], PathComparer.Default),
			roots,
			roots,
			ExtensionPolicy(".cs"),
			rules,
			TestContext.Current.CancellationToken,
			includeIgnoreImpactCounts: false);

		Assert.Equal([".cs"], projection.AvailableExtensions);
		Assert.Equal(IgnoreOptionCounts.Empty, projection.IgnoreOptionCounts);
		Assert.Equal(IgnoreControllerImpactCounts.Empty, projection.ControllerImpactCounts);
	}

	[Fact]
	public void ScopeCountsOnlyRulesThatCanChangeItsFiles()
	{
		using var project = new TemporaryDirectory();
		var dotFolderFile = PathUtility.Normalize(
			project.CreateFile(".generated/staged.cs", "class Staged {}\n"));
		var dotFile = PathUtility.Normalize(project.CreateFile(".metadata", "value\n"));
		var extensionlessFile = PathUtility.Normalize(project.CreateFile("NOTICE", "value\n"));
		project.CreateFile(".unrelated/ignored.cs", "class Unrelated {}\n");
		project.CreateFile("ordinary.txt", "unrelated\n");
		var (inventory, rules, roots) = BuildInventory(project.Path);
		var scope = new HashSet<string>(
			[dotFolderFile, dotFile, extensionlessFile],
			PathComparer.Default);

		var projection = GitScopePresentationProjector.Build(
			project.Path,
			inventory,
			scope,
			roots,
			roots,
			ExtensionPolicy(".cs", ".metadata"),
			rules,
			TestContext.Current.CancellationToken);

		Assert.Empty(projection.AvailableExtensions);
		Assert.Equal(1, projection.IgnoreOptionCounts.DotFolders);
		Assert.Equal(1, projection.IgnoreOptionCounts.DotFiles);
		Assert.Equal(1, projection.IgnoreOptionCounts.ExtensionlessFiles);
		Assert.Equal(0, projection.IgnoreOptionCounts.HiddenFolders);
		Assert.Equal(0, projection.IgnoreOptionCounts.HiddenFiles);
		Assert.Equal(0, projection.IgnoreOptionCounts.EmptyFolders);
		Assert.Equal(0, projection.IgnoreOptionCounts.EmptyFiles);

		var dotFoldersVisible = GitScopePresentationProjector.Build(
			project.Path,
			inventory,
			scope,
			roots,
			roots,
			ExtensionPolicy(".cs"),
			rules with { IgnoreDotFolders = false },
			TestContext.Current.CancellationToken);

		Assert.Equal([".cs"], dotFoldersVisible.AvailableExtensions);
		Assert.Equal(1, dotFoldersVisible.IgnoreOptionCounts.DotFolders);
	}

	[Fact]
	public void RepositoryPathAliasDoesNotDoubleCountIgnoreOwnerEvidence()
	{
		using var project = new TemporaryDirectory();
		var inventoriedPath = PathUtility.Normalize(
			project.CreateFile(".Hidden.cs", "class Hidden {}\n"));
		var gitPath = Path.Combine(project.Path, ".hidden.cs");
		var (inventory, rules, roots) = BuildInventory(project.Path);
		var scope = new GitScopePathResult(
			true,
			new HashSet<string>([gitPath], StringComparer.Ordinal),
			0,
			PathMatchers:
			[
				new GitTrackedPathIndex(
					project.Path,
					[".hidden.cs"],
					new GitPathComparisonSemantics(IgnoreCase: true, NormalizeUnicode: false))
			]);

		var projection = GitScopePresentationProjector.Build(
			project.Path,
			inventory,
			scope,
			roots,
			roots,
			ExtensionPolicy(".cs"),
			rules,
			TestContext.Current.CancellationToken);

		Assert.True(scope.ContainsPath(inventoriedPath));
		Assert.Equal(1, projection.IgnoreOptionCounts.DotFiles);
	}

	[Fact]
	public void ActiveFileRulesExposeOnlyTheCurrentDecisionOwner()
	{
		using var project = new TemporaryDirectory();
		var scopedFile = PathUtility.Normalize(project.CreateFile(".empty.cs", string.Empty));
		var (inventory, rules, roots) = BuildInventory(project.Path);
		var scope = new HashSet<string>([scopedFile], PathComparer.Default);
		var inventoryFile = Assert.Single(inventory.Entries, static entry => !entry.IsDirectory);
		Assert.Contains(inventoryFile.FullPath, scope);
		Assert.Equal(0, inventoryFile.ParentIndex);
		Assert.Equal(".empty.cs", inventoryFile.Name);
		Assert.Equal(
			IgnoreDecisionOwner.DotFiles,
			IgnoreDecisionEngine.EvaluateFile(
				inventoryFile.FullPath,
				inventoryFile.Name,
				inventoryFile.IsHidden,
				inventoryFile.Length,
				rules,
				shouldApplySmartIgnore: false,
				IgnoreRules.GitIgnoreEvaluation.NotIgnored).Owner);

		var dotOwner = GitScopePresentationProjector.Build(
			project.Path,
			inventory,
			scope,
			roots,
			roots,
			ExtensionPolicy(".cs"),
			rules,
			TestContext.Current.CancellationToken);

		Assert.Equal(1, dotOwner.IgnoreOptionCounts.DotFiles);
		Assert.Equal(0, dotOwner.IgnoreOptionCounts.ExtensionlessFiles);
		Assert.Equal(0, dotOwner.IgnoreOptionCounts.EmptyFiles);

		var emptyOwner = GitScopePresentationProjector.Build(
			project.Path,
			inventory,
			scope,
			roots,
			roots,
			ExtensionPolicy(".cs"),
			rules with { IgnoreDotFiles = false },
			TestContext.Current.CancellationToken);

		Assert.Equal(0, emptyOwner.IgnoreOptionCounts.DotFiles);
		Assert.Equal(0, emptyOwner.IgnoreOptionCounts.ExtensionlessFiles);
		Assert.Equal(1, emptyOwner.IgnoreOptionCounts.EmptyFiles);
	}

	[Fact]
	public void NestedDirectoryImpactCountsOnlyReachableBlockersInTheActiveState()
	{
		using var project = new TemporaryDirectory();
		var scopedFile = PathUtility.Normalize(project.CreateFile(".outer/.inner/file.cs", "class File {}\n"));
		var (inventory, rules, roots) = BuildInventory(project.Path);
		var scope = new HashSet<string>([scopedFile], PathComparer.Default);
		var extensions = new HashSet<string>([".cs"], StringComparer.OrdinalIgnoreCase);

		var active = GitScopePresentationProjector.Build(
			project.Path,
			inventory,
			scope,
			roots,
			roots,
			new ExtensionSetInclusionPolicy(extensions),
			rules,
			TestContext.Current.CancellationToken);
		var inactive = GitScopePresentationProjector.Build(
			project.Path,
			inventory,
			scope,
			roots,
			roots,
			new ExtensionSetInclusionPolicy(extensions),
			rules with { IgnoreDotFolders = false },
			TestContext.Current.CancellationToken);

		Assert.Equal(1, active.IgnoreOptionCounts.DotFolders);
		Assert.Equal(2, inactive.IgnoreOptionCounts.DotFolders);
	}

	[Fact]
	public void DotAndHiddenFolderOwnershipMatchesPlatformSemantics()
	{
		using var project = new TemporaryDirectory();
		var directoryPath = Path.Combine(project.Path, ".hidden");
		var filePath = Path.Combine(directoryPath, "file.cs");
		var entries = new List<ProjectTreeInventoryEntry>
		{
			new(Path.GetFileName(project.Path), project.Path, string.Empty, -1, true, false, 0)
			{
				FirstChildIndex = 1,
				ChildCount = 1
			},
			new(".hidden", directoryPath, ".hidden", 0, true, true, 0)
			{
				FirstChildIndex = 2,
				ChildCount = 1
			},
			new("file.cs", filePath, Path.Combine(".hidden", "file.cs"), 1, false, false, 12)
		};
		var inventory = new ProjectTreeInventorySnapshot(entries, false, false);
		var roots = new HashSet<string>([".hidden"], PathComparer.Default);
		var extensions = new HashSet<string>([".cs"], StringComparer.OrdinalIgnoreCase);
		var scope = new HashSet<string>([filePath], PathComparer.Default);
		var emptyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var rules = new IgnoreRules(true, false, true, false, emptyNames, emptyNames);

		var dotOwner = GitScopePresentationProjector.Build(
			project.Path,
			inventory,
			scope,
			roots,
			roots,
			new ExtensionSetInclusionPolicy(extensions),
			rules,
			TestContext.Current.CancellationToken);
		var hiddenOwner = GitScopePresentationProjector.Build(
			project.Path,
			inventory,
			scope,
			roots,
			roots,
			new ExtensionSetInclusionPolicy(extensions),
			rules with { IgnoreDotFolders = false },
			TestContext.Current.CancellationToken);

		Assert.Equal(1, dotOwner.IgnoreOptionCounts.DotFolders);
		Assert.Equal(0, dotOwner.IgnoreOptionCounts.HiddenFolders);
		Assert.Equal(
			OperatingSystem.IsWindows() ? 0 : 1,
			hiddenOwner.IgnoreOptionCounts.DotFolders);
		Assert.Equal(
			OperatingSystem.IsWindows() ? 1 : 0,
			hiddenOwner.IgnoreOptionCounts.HiddenFolders);
	}

	[Fact]
	public void ImplicitRootSelectionKeepsScopedOwnerEvidenceOutsideTheFilteredInventory()
	{
		using var project = new TemporaryDirectory();
		var scopedFile = PathUtility.Normalize(
			project.CreateFile(".scope/Staged.cs", "class Staged {}\n"));
		var inventory = new ProjectTreeInventorySnapshot(
			[
				new ProjectTreeInventoryEntry(
					Path.GetFileName(project.Path),
					project.Path,
					string.Empty,
					-1,
					true,
					false,
					0)
			],
			false,
			false);
		var emptyRoots = new HashSet<string>(PathComparer.Default);
		var availableRoots = new HashSet<string>([".scope"], PathComparer.Default);
		var rules = new IgnoreRules(
			IgnoreDotFolders: true,
			IgnoreDotFiles: false,
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

		var projection = GitScopePresentationProjector.Build(
			project.Path,
			inventory,
			new HashSet<string>([scopedFile], PathComparer.Default),
			emptyRoots,
			availableRoots,
			ExtensionPolicy(".cs"),
			rules,
			TestContext.Current.CancellationToken);

		Assert.Equal(1, projection.IgnoreOptionCounts.DotFolders);
	}

	[Fact]
	public void ExplicitRootSelectionDoesNotAdvertiseScopedFilesFromOtherRoots()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("src/in.cs", "class Included {}\n");
		var excluded = PathUtility.Normalize(project.CreateFile(".cache/out.txt", "excluded\n"));
		var (inventory, rules, roots) = BuildInventory(project.Path);
		var selectedRoots = new HashSet<string>(["src"], PathComparer.Default);

		var projection = GitScopePresentationProjector.Build(
			project.Path,
			inventory,
			new HashSet<string>([excluded], PathComparer.Default),
			selectedRoots,
			roots,
			ExtensionPolicy(".cs"),
			rules,
			TestContext.Current.CancellationToken,
			rootSelectionIsExplicit: true);

		Assert.Empty(projection.AvailableExtensions);
		Assert.Equal(IgnoreOptionCounts.Empty, projection.IgnoreOptionCounts);
		Assert.Equal(IgnoreControllerImpactCounts.Empty, projection.ControllerImpactCounts);
	}

	[Fact]
	public void ActiveFolderRuleRemainsActionableBeforeKnownUncheckedExtension()
	{
		using var project = new TemporaryDirectory();
		var scopedFile = PathUtility.Normalize(
			project.CreateFile(".generated/staged.cs", "class Staged {}\n"));
		var (inventory, rules, roots) = BuildInventory(project.Path);
		var policy = new ExtensionSelectionInclusionPolicy(
			new SelectionStateResolver(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
				{
					[".cs"] = false
				}),
			defaultForNewExtension: true);

		var projection = GitScopePresentationProjector.Build(
			project.Path,
			inventory,
			new HashSet<string>([scopedFile], PathComparer.Default),
			roots,
			roots,
			policy,
			rules,
			TestContext.Current.CancellationToken);

		Assert.Empty(projection.AvailableExtensions);
		Assert.Equal(1, projection.IgnoreOptionCounts.DotFolders);
		Assert.Equal(0, projection.IgnoreOptionCounts.HiddenFolders);
		Assert.Equal(0, projection.IgnoreOptionCounts.HiddenFiles);
		Assert.Equal(0, projection.IgnoreOptionCounts.DotFiles);
		Assert.Equal(0, projection.IgnoreOptionCounts.EmptyFiles);
		Assert.Equal(0, projection.IgnoreOptionCounts.ExtensionlessFiles);
	}

	[Fact]
	public void NewlyDiscoveredExtensionUsesTheOpenWorldDefaultForFolderImpact()
	{
		using var project = new TemporaryDirectory();
		var scopedFile = PathUtility.Normalize(
			project.CreateFile(".generated/staged.xyz", "value\n"));
		var (inventory, rules, roots) = BuildInventory(project.Path);
		var policy = new ExtensionSelectionInclusionPolicy(
			new SelectionStateResolver(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)),
			defaultForNewExtension: true);

		var hidden = GitScopePresentationProjector.Build(
			project.Path,
			inventory,
			new HashSet<string>([scopedFile], PathComparer.Default),
			roots,
			roots,
			policy,
			rules,
			TestContext.Current.CancellationToken);
		var revealed = GitScopePresentationProjector.Build(
			project.Path,
			inventory,
			new HashSet<string>([scopedFile], PathComparer.Default),
			roots,
			roots,
			policy,
			rules with { IgnoreDotFolders = false },
			TestContext.Current.CancellationToken);

		Assert.Equal(1, hidden.IgnoreOptionCounts.DotFolders);
		Assert.Equal([".xyz"], revealed.AvailableExtensions);
	}

	[Fact]
	public void ExplicitEmptyExtensionSetKeepsActiveFolderRuleWithoutRevivingItsType()
	{
		using var project = new TemporaryDirectory();
		var scopedFile = PathUtility.Normalize(
			project.CreateFile(".generated/staged.cs", "class Staged {}\n"));
		var (inventory, rules, roots) = BuildInventory(project.Path);

		var projection = GitScopePresentationProjector.Build(
			project.Path,
			inventory,
			new HashSet<string>([scopedFile], PathComparer.Default),
			roots,
			roots,
			ExtensionPolicy(),
			rules,
			TestContext.Current.CancellationToken);

		Assert.Equal(1, projection.IgnoreOptionCounts.DotFolders);
		Assert.Empty(projection.AvailableExtensions);
	}

	private static IExtensionInclusionPolicy ExtensionPolicy(params string[] extensions) =>
		new ExtensionSetInclusionPolicy(
			extensions.ToHashSet(StringComparer.OrdinalIgnoreCase));

	private static (ProjectTreeInventorySnapshot Inventory, IgnoreRules Rules, IReadOnlySet<string> Roots)
		BuildInventory(string rootPath)
	{
		var roots = Directory.GetDirectories(rootPath)
			.Select(Path.GetFileName)
			.Where(static name => !string.IsNullOrEmpty(name))
			.Cast<string>()
			.ToHashSet(PathComparer.Default);
		var emptyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: true,
			IgnoreDotFiles: true,
			emptyNames,
			emptyNames)
		{
			IgnoreEmptyFiles = true,
			IgnoreExtensionlessFiles = true
		};
		var discoveryRules = rules with
		{
			IgnoreDotFolders = false,
			IgnoreDotFiles = false,
			IgnoreEmptyFiles = false,
			IgnoreExtensionlessFiles = false
		};
		var inventory = new TreeBuilder().ReadCompositeInventory(
			rootPath,
			roots,
			discoveryRules,
			rules,
			TestContext.Current.CancellationToken);
		return (inventory, rules, roots);
	}
}
