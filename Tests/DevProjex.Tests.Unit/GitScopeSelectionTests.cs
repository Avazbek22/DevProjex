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
	public void DotFolderOwnsHiddenDotFolderUntilDotFilteringIsDisabled()
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
		Assert.Equal(0, hiddenOwner.IgnoreOptionCounts.DotFolders);
		Assert.Equal(1, hiddenOwner.IgnoreOptionCounts.HiddenFolders);
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
