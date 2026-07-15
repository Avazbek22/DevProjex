using DevProjex.Tests.Shared.ProjectLoadWorkflow;

namespace DevProjex.Tests.Integration;

public sealed class GitCloneIgnoreLifecycleIntegrationTests
{
	[Fact]
	public void ManagedCloneMetadata_IndividualDirectoryToggleJourney_KeepsOptionsCountsAndRootsStable()
	{
		using var workspace = CreateDirectoryToggleWorkspace();
		var services = CreateManagedCloneServices();
		var snapshot = RefreshFull(
			workspace.Path,
			services,
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(workspace.Path));

		AssertDirectoryToggleState(
			workspace.Path,
			services,
			snapshot,
			dotFoldersChecked: true,
			emptyFoldersChecked: true,
			expectedRoots: ["src"]);

		snapshot = ToggleIgnoreOption(
			workspace.Path,
			services,
			snapshot,
			IgnoreOptionId.DotFolders,
			isChecked: false);
		AssertDirectoryToggleState(
			workspace.Path,
			services,
			snapshot,
			dotFoldersChecked: false,
			emptyFoldersChecked: true,
			expectedRoots: [".workspace", "src"]);

		snapshot = ToggleIgnoreOption(
			workspace.Path,
			services,
			snapshot,
			IgnoreOptionId.EmptyFolders,
			isChecked: false);
		AssertDirectoryToggleState(
			workspace.Path,
			services,
			snapshot,
			dotFoldersChecked: false,
			emptyFoldersChecked: false,
			expectedRoots: [".workspace", "empty-root", "src"]);

		snapshot = ToggleIgnoreOption(
			workspace.Path,
			services,
			snapshot,
			IgnoreOptionId.DotFolders,
			isChecked: true);
		AssertDirectoryToggleState(
			workspace.Path,
			services,
			snapshot,
			dotFoldersChecked: true,
			emptyFoldersChecked: false,
			expectedRoots: ["empty-root", "src"]);

		snapshot = ToggleIgnoreOption(
			workspace.Path,
			services,
			snapshot,
			IgnoreOptionId.EmptyFolders,
			isChecked: true);
		AssertDirectoryToggleState(
			workspace.Path,
			services,
			snapshot,
			dotFoldersChecked: true,
			emptyFoldersChecked: true,
			expectedRoots: ["src"]);
	}

	[Fact]
	public void ManagedCloneMetadata_FullRefreshAfterRemoteMutation_DiscoversOnlyRealIgnoreCandidates()
	{
		using var workspace = CreateWorkspace(CloneFixtureKind.Python);
		var services = CreateManagedCloneServices();
		var baseline = RefreshFull(
			workspace.Path,
			services,
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(workspace.Path));

		workspace.CreateFile(Path.Combine(".github", "workflows", "build.yml"), "name: build\n");
		workspace.CreateDirectory("empty-cache");
		workspace.CreateFile(Path.Combine("worker", "worker.py"), "print('worker')\n");

		var refreshed = RefreshFull(
			workspace.Path,
			services,
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(workspace.Path, baseline));

		AssertIgnoreOption(refreshed, IgnoreOptionId.DotFolders, expectedChecked: true, expectedCount: 1);
		AssertIgnoreOption(refreshed, IgnoreOptionId.EmptyFolders, expectedChecked: true, expectedCount: 1);
		AssertCloneMetadataExcluded(refreshed);
		Assert.Equal(["app", "worker"], SnapshotRootNames(refreshed));
		Assert.DoesNotContain(refreshed.IgnoreOptions, option => option.Id is
			IgnoreOptionId.HiddenFolders or
			IgnoreOptionId.UseGitIgnore or
			IgnoreOptionId.SmartIgnore);
		SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
			workspace.Path,
			services.IgnoreRulesService,
			refreshed);

		var allDisabled = ApplyAll(workspace.Path, services, refreshed, isChecked: false);

		AssertIgnoreOption(allDisabled, IgnoreOptionId.DotFolders, expectedChecked: false, expectedCount: 1);
		AssertIgnoreOption(allDisabled, IgnoreOptionId.EmptyFolders, expectedChecked: false, expectedCount: 1);
		AssertCloneMetadataExcluded(allDisabled);
		Assert.Equal([".github", "app", "empty-cache", "worker"], SnapshotRootNames(allDisabled));
		Assert.DoesNotContain(allDisabled.IgnoreOptions, option => option.Id is
			IgnoreOptionId.HiddenFolders or
			IgnoreOptionId.UseGitIgnore or
			IgnoreOptionId.SmartIgnore);
		SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
			workspace.Path,
			services.IgnoreRulesService,
			allDisabled);

		var allReenabled = ApplyAll(workspace.Path, services, allDisabled, isChecked: true);

		AssertIgnoreOption(allReenabled, IgnoreOptionId.DotFolders, expectedChecked: true, expectedCount: 1);
		AssertIgnoreOption(allReenabled, IgnoreOptionId.EmptyFolders, expectedChecked: true, expectedCount: 1);
		AssertCloneMetadataExcluded(allReenabled);
		Assert.Equal(["app", "worker"], SnapshotRootNames(allReenabled));
		Assert.Equal(
			refreshed.IgnoreOptions.Select(static option => option.Id),
			allReenabled.IgnoreOptions.Select(static option => option.Id));
		SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
			workspace.Path,
			services.IgnoreRulesService,
			allReenabled);
	}

	[Fact]
	public void ManagedCloneMetadata_AllOffBeforeCandidatesAppear_KeepsNewCandidatesUncheckedAndRootsVisible()
	{
		using var workspace = CreateBareManagedCloneWorkspace();
		var services = CreateManagedCloneServices();
		var baseline = RefreshFull(
			workspace.Path,
			services,
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(workspace.Path));

		Assert.Empty(baseline.IgnoreOptions);
		AssertCloneMetadataExcluded(baseline);
		Assert.Equal(["src"], SnapshotRootNames(baseline));

		var allOffIntent = ApplyAll(workspace.Path, services, baseline, isChecked: false);

		Assert.Empty(allOffIntent.IgnoreOptions);

		workspace.CreateFile(Path.Combine(".settings", "config.json"), "{}\n");
		workspace.CreateDirectory("empty-root");
		workspace.CreateFile(Path.Combine("src", ".env"), "APP_ENV=test\n");
		workspace.CreateFile(Path.Combine("src", "LICENSE"), "MIT\n");
		workspace.CreateFile(Path.Combine("src", "empty.txt"), string.Empty);

		var discovered = RefreshFull(
			workspace.Path,
			services,
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(workspace.Path, allOffIntent));

		AssertIgnoreOption(discovered, IgnoreOptionId.DotFolders, expectedChecked: false, expectedCount: 1);
		AssertIgnoreOption(discovered, IgnoreOptionId.DotFiles, expectedChecked: false, expectedCount: 1);
		AssertIgnoreOption(discovered, IgnoreOptionId.EmptyFolders, expectedChecked: false, expectedCount: 1);
		AssertIgnoreOption(discovered, IgnoreOptionId.EmptyFiles, expectedChecked: false, expectedCount: 1);
		AssertIgnoreOption(discovered, IgnoreOptionId.ExtensionlessFiles, expectedChecked: false, expectedCount: 1);
		Assert.DoesNotContain(discovered.IgnoreOptions, static option => option.IsChecked);
		Assert.DoesNotContain(discovered.IgnoreOptions, option => option.Id is
			IgnoreOptionId.HiddenFolders or
			IgnoreOptionId.HiddenFiles or
			IgnoreOptionId.UseGitIgnore or
			IgnoreOptionId.SmartIgnore);
		AssertCloneMetadataExcluded(discovered);
		Assert.Equal([".settings", "empty-root", "src"], SnapshotRootNames(discovered));
		AssertTreePathVisible(workspace.Path, services, discovered, Path.Combine(".settings", "config.json"));
		AssertTreePathVisible(workspace.Path, services, discovered, "empty-root");
		AssertTreePathVisible(workspace.Path, services, discovered, Path.Combine("src", ".env"));
		AssertTreePathVisible(workspace.Path, services, discovered, Path.Combine("src", "LICENSE"));
		AssertTreePathVisible(workspace.Path, services, discovered, Path.Combine("src", "empty.txt"));
		SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
			workspace.Path,
			services.IgnoreRulesService,
			discovered);

		var allReenabled = ApplyAll(workspace.Path, services, discovered, isChecked: true);

		AssertIgnoreOption(allReenabled, IgnoreOptionId.DotFolders, expectedChecked: true, expectedCount: 1);
		AssertIgnoreOption(allReenabled, IgnoreOptionId.DotFiles, expectedChecked: true, expectedCount: 1);
		AssertIgnoreOption(allReenabled, IgnoreOptionId.EmptyFolders, expectedChecked: true, expectedCount: 1);
		AssertIgnoreOption(allReenabled, IgnoreOptionId.EmptyFiles, expectedChecked: true, expectedCount: 1);
		AssertIgnoreOption(allReenabled, IgnoreOptionId.ExtensionlessFiles, expectedChecked: true, expectedCount: 1);
		AssertCloneMetadataExcluded(allReenabled);
		Assert.Equal(["src"], SnapshotRootNames(allReenabled));
		AssertTreePathHidden(workspace.Path, services, allReenabled, Path.Combine(".settings", "config.json"));
		AssertTreePathHidden(workspace.Path, services, allReenabled, "empty-root");
		AssertTreePathHidden(workspace.Path, services, allReenabled, Path.Combine("src", ".env"));
		AssertTreePathHidden(workspace.Path, services, allReenabled, Path.Combine("src", "LICENSE"));
		AssertTreePathHidden(workspace.Path, services, allReenabled, Path.Combine("src", "empty.txt"));
		SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
			workspace.Path,
			services.IgnoreRulesService,
			allReenabled);
	}

	[Fact]
	public void ManagedCloneMetadata_UncheckedEmptyRootDisappearsAndReappears_RestoresStateAndProjection()
	{
		using var workspace = CreateBareManagedCloneWorkspace();
		workspace.CreateDirectory("empty-first");
		var services = CreateManagedCloneServices();
		var baseline = RefreshFull(
			workspace.Path,
			services,
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(workspace.Path));

		AssertIgnoreOption(baseline, IgnoreOptionId.EmptyFolders, expectedChecked: true, expectedCount: 1);
		Assert.Equal(["src"], SnapshotRootNames(baseline));

		var disabled = ToggleIgnoreOption(
			workspace.Path,
			services,
			baseline,
			IgnoreOptionId.EmptyFolders,
			isChecked: false);

		AssertIgnoreOption(disabled, IgnoreOptionId.EmptyFolders, expectedChecked: false, expectedCount: 1);
		Assert.Equal(["empty-first", "src"], SnapshotRootNames(disabled));
		AssertTreePathVisible(workspace.Path, services, disabled, "empty-first");
		SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
			workspace.Path,
			services.IgnoreRulesService,
			disabled);

		Directory.Delete(Path.Combine(workspace.Path, "empty-first"));
		var absent = RefreshFull(
			workspace.Path,
			services,
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(workspace.Path, disabled));

		Assert.Equal(0, absent.IgnoreOptionCounts.EmptyFolders);
		Assert.DoesNotContain(absent.IgnoreOptions, option => option.Id == IgnoreOptionId.EmptyFolders);
		Assert.False(absent.IgnoreOptionStateCache[IgnoreOptionId.EmptyFolders]);
		Assert.Equal(["src"], SnapshotRootNames(absent));
		SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
			workspace.Path,
			services.IgnoreRulesService,
			absent);

		workspace.CreateDirectory("empty-second");
		var reappeared = RefreshFull(
			workspace.Path,
			services,
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(workspace.Path, absent));

		AssertIgnoreOption(reappeared, IgnoreOptionId.EmptyFolders, expectedChecked: false, expectedCount: 1);
		Assert.Equal(["empty-second", "src"], SnapshotRootNames(reappeared));
		AssertTreePathVisible(workspace.Path, services, reappeared, "empty-second");
		SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
			workspace.Path,
			services.IgnoreRulesService,
			reappeared);

		var reenabled = ToggleIgnoreOption(
			workspace.Path,
			services,
			reappeared,
			IgnoreOptionId.EmptyFolders,
			isChecked: true);

		AssertIgnoreOption(reenabled, IgnoreOptionId.EmptyFolders, expectedChecked: true, expectedCount: 1);
		Assert.Equal(["src"], SnapshotRootNames(reenabled));
		AssertTreePathHidden(workspace.Path, services, reenabled, "empty-second");
		SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
			workspace.Path,
			services.IgnoreRulesService,
			reenabled);
	}

	[Fact]
	public void ManagedCloneMetadata_RootGitIsExcludedButNestedGitFollowsDotFolderToggle()
	{
		using var workspace = CreateBareManagedCloneWorkspace();
		workspace.CreateFile(Path.Combine("src", ".git", "metadata.txt"), "nested repository metadata\n");
		var services = CreateManagedCloneServices();
		var baseline = RefreshFull(
			workspace.Path,
			services,
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(workspace.Path));

		AssertIgnoreOption(baseline, IgnoreOptionId.DotFolders, expectedChecked: true, expectedCount: 1);
		AssertCloneMetadataExcluded(baseline);
		Assert.Equal(["src"], SnapshotRootNames(baseline));
		AssertTreePathHidden(workspace.Path, services, baseline, Path.Combine("src", ".git", "metadata.txt"));

		var dotFoldersDisabled = ToggleIgnoreOption(
			workspace.Path,
			services,
			baseline,
			IgnoreOptionId.DotFolders,
			isChecked: false);

		AssertIgnoreOption(dotFoldersDisabled, IgnoreOptionId.DotFolders, expectedChecked: false, expectedCount: 1);
		AssertCloneMetadataExcluded(dotFoldersDisabled);
		Assert.Equal(["src"], SnapshotRootNames(dotFoldersDisabled));
		AssertTreePathVisible(workspace.Path, services, dotFoldersDisabled, Path.Combine("src", ".git"));
		AssertTreePathVisible(workspace.Path, services, dotFoldersDisabled, Path.Combine("src", ".git", "metadata.txt"));
		SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
			workspace.Path,
			services.IgnoreRulesService,
			dotFoldersDisabled);

		var dotFoldersReenabled = ToggleIgnoreOption(
			workspace.Path,
			services,
			dotFoldersDisabled,
			IgnoreOptionId.DotFolders,
			isChecked: true);

		AssertIgnoreOption(dotFoldersReenabled, IgnoreOptionId.DotFolders, expectedChecked: true, expectedCount: 1);
		AssertCloneMetadataExcluded(dotFoldersReenabled);
		Assert.Equal(["src"], SnapshotRootNames(dotFoldersReenabled));
		AssertTreePathHidden(workspace.Path, services, dotFoldersReenabled, Path.Combine("src", ".git", "metadata.txt"));
		SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
			workspace.Path,
			services.IgnoreRulesService,
			dotFoldersReenabled);
	}

	[Theory]
	[InlineData(CloneFixtureKind.Python, 2)]
	[InlineData(CloneFixtureKind.DotNet, 1)]
	public void ManagedCloneMetadata_AllAndFileToggleCycles_KeepIgnoreRootsAndExtensionsCoherent(
		CloneFixtureKind fixtureKind,
		int expectedDotFileCount)
	{
		using var workspace = CreateWorkspace(fixtureKind);
		var services = CreateManagedCloneServices();
		var baseline = RefreshFull(
			workspace.Path,
			services,
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(workspace.Path));

		AssertStableCloneBoundary(baseline, expectedDotFileCount);
		var baselineRootNames = SnapshotRootNames(baseline);
		var baselineIgnoreOptions = baseline.IgnoreOptions.ToArray();

		var current = baseline;
		for (var cycle = 0; cycle < 3; cycle++)
		{
			current = ApplyAll(workspace.Path, services, current, isChecked: false);
			AssertStableCloneBoundary(
				current,
				expectedDotFileCount,
				expectedDotFilesChecked: false,
				expectedExtensionlessChecked: false);
			Assert.Equal(baselineRootNames, SnapshotRootNames(current));
			Assert.Contains(current.EffectiveExtensionOptions, option => option.Name == ".gitignore");
			Assert.Contains(current.TreeInventory!.Entries, entry =>
				!entry.IsDirectory && entry.RelativePath == ".gitignore");

			current = ApplyAll(workspace.Path, services, current, isChecked: true);
			AssertStableCloneBoundary(current, expectedDotFileCount);
			Assert.Equal(baselineRootNames, SnapshotRootNames(current));
			Assert.Equal(baselineIgnoreOptions, current.IgnoreOptions);

			current = ToggleFileOption(
				workspace.Path,
				services,
				current,
				IgnoreOptionId.DotFiles,
				isChecked: false);
			AssertStableCloneBoundary(current, expectedDotFileCount, expectedDotFilesChecked: false);
			Assert.Contains(current.EffectiveExtensionOptions, option => option.Name == ".gitignore");

			current = ToggleFileOption(
				workspace.Path,
				services,
				current,
				IgnoreOptionId.DotFiles,
				isChecked: true);
			AssertStableCloneBoundary(current, expectedDotFileCount);
			Assert.Equal(baselineIgnoreOptions, current.IgnoreOptions);
		}

		current = RefreshFull(
			workspace.Path,
			services,
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(workspace.Path, current));

		AssertStableCloneBoundary(current, expectedDotFileCount);
		Assert.Equal(baselineRootNames, SnapshotRootNames(current));
		Assert.Equal(baselineIgnoreOptions, current.IgnoreOptions);
	}

	[Fact]
	public void OrdinaryFolderScan_WithoutManagedCloneBoundary_KeepsDotGitUnderNormalIgnoreSemantics()
	{
		using var workspace = CreateWorkspace(CloneFixtureKind.DotNet);
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();

		var snapshot = RefreshFull(
			workspace.Path,
			services,
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(workspace.Path));

		Assert.Contains(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.DotFolders);
		Assert.Equal(1, snapshot.IgnoreOptionCounts.DotFolders);
	}

	private static SelectionRefreshSnapshot RefreshFull(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshContext context)
	{
		return services.Engine.ComputeFullRefreshSnapshot(
			context with { CaptureTreeInventory = true },
			TestContext.Current.CancellationToken);
	}

	private static SelectionRefreshSnapshot ApplyAll(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshSnapshot snapshot,
		bool isChecked)
	{
		var context = ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, snapshot);
		var states = snapshot.IgnoreOptionStateCache.Keys.ToDictionary(static id => id, _ => isChecked);
		return RefreshFull(
			rootPath,
			services,
			context with
			{
				IgnoreSelectionCache = isChecked ? states.Keys.ToHashSet() : [],
				IgnoreOptionStateCache = states,
				IgnoreAllPreference = isChecked
			});
	}

	private static SelectionRefreshSnapshot ToggleFileOption(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshSnapshot snapshot,
		IgnoreOptionId optionId,
		bool isChecked)
	{
		var context = ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, snapshot);
		var states = new Dictionary<IgnoreOptionId, bool>(snapshot.IgnoreOptionStateCache)
		{
			[optionId] = isChecked
		};
		var selectedRoots = snapshot.RootOptions!
			.Where(static option => option.IsChecked)
			.Select(static option => option.Name)
			.ToHashSet(PathComparer.Default);
		var liveSnapshot = services.Engine.ComputeLiveRefreshSnapshot(
			context with
			{
				IgnoreSelectionCache = states
					.Where(static pair => pair.Value)
					.Select(static pair => pair.Key)
					.ToHashSet(),
				IgnoreOptionStateCache = states,
				IgnoreAllPreference = null,
				CaptureTreeInventory = true
			},
			selectedRoots,
			TestContext.Current.CancellationToken);

		return liveSnapshot with { RootOptions = snapshot.RootOptions };
	}

	private static SelectionRefreshSnapshot ToggleIgnoreOption(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshSnapshot snapshot,
		IgnoreOptionId optionId,
		bool isChecked)
	{
		var states = new Dictionary<IgnoreOptionId, bool>(snapshot.IgnoreOptionStateCache)
		{
			[optionId] = isChecked
		};
		return RefreshFull(
			rootPath,
			services,
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, snapshot) with
			{
				IgnoreSelectionCache = states
					.Where(static pair => pair.Value)
					.Select(static pair => pair.Key)
					.ToHashSet(),
				IgnoreOptionStateCache = states,
				IgnoreAllPreference = null
			});
	}

	private static void AssertDirectoryToggleState(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshSnapshot snapshot,
		bool dotFoldersChecked,
		bool emptyFoldersChecked,
		string[] expectedRoots)
	{
		AssertIgnoreOption(snapshot, IgnoreOptionId.DotFolders, dotFoldersChecked, expectedCount: 1);
		AssertIgnoreOption(snapshot, IgnoreOptionId.EmptyFolders, emptyFoldersChecked, expectedCount: 1);
		AssertCloneMetadataExcluded(snapshot);
		Assert.Equal(expectedRoots, SnapshotRootNames(snapshot));
		SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
			rootPath,
			services.IgnoreRulesService,
			snapshot);
	}

	private static void AssertIgnoreOption(
		SelectionRefreshSnapshot snapshot,
		IgnoreOptionId optionId,
		bool expectedChecked,
		int expectedCount)
	{
		var option = Assert.Single(snapshot.IgnoreOptions, option => option.Id == optionId);
		Assert.Equal(expectedChecked, option.IsChecked);
		Assert.Equal(expectedCount, GetIgnoreOptionCount(snapshot.IgnoreOptionCounts, optionId));
		Assert.EndsWith($"({expectedCount})", option.Label);
	}

	private static int GetIgnoreOptionCount(IgnoreOptionCounts counts, IgnoreOptionId optionId) => optionId switch
	{
		IgnoreOptionId.HiddenFolders => counts.HiddenFolders,
		IgnoreOptionId.HiddenFiles => counts.HiddenFiles,
		IgnoreOptionId.DotFolders => counts.DotFolders,
		IgnoreOptionId.DotFiles => counts.DotFiles,
		IgnoreOptionId.EmptyFolders => counts.EmptyFolders,
		IgnoreOptionId.EmptyFiles => counts.EmptyFiles,
		IgnoreOptionId.ExtensionlessFiles => counts.ExtensionlessFiles,
		_ => 0
	};

	private static void AssertCloneMetadataExcluded(SelectionRefreshSnapshot snapshot)
	{
		Assert.NotNull(snapshot.RootOptions);
		Assert.NotNull(snapshot.TreeInventory);
		Assert.DoesNotContain(snapshot.RootOptions!, option => option.Name == ".git");
		Assert.DoesNotContain(snapshot.TreeInventory!.Entries, entry =>
			entry.RelativePath == ".git" ||
			entry.RelativePath.StartsWith($".git{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
	}

	private static void AssertTreePathVisible(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshSnapshot snapshot,
		string relativePath)
	{
		Assert.True(
			ContainsTreePath(BuildProjectedTree(rootPath, services, snapshot), relativePath),
			$"Expected projected tree path '{relativePath}' to be visible.");
	}

	private static void AssertTreePathHidden(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshSnapshot snapshot,
		string relativePath)
	{
		Assert.False(
			ContainsTreePath(BuildProjectedTree(rootPath, services, snapshot), relativePath),
			$"Expected projected tree path '{relativePath}' to be hidden.");
	}

	private static TreeBuildResult BuildProjectedTree(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshSnapshot snapshot)
	{
		var selectedRoots = ProjectLoadWorkflowRefreshHarness.CollectCheckedRootNames(snapshot);
		var selectedExtensions = snapshot.EffectiveExtensionOptions
			.Where(static option => option.IsChecked)
			.Select(static option => option.Name)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var selectedIgnoreOptions = ProjectLoadWorkflowRefreshHarness.CollectCheckedIgnoreOptionIds(snapshot);
		var rules = services.IgnoreRulesService.Build(rootPath, selectedIgnoreOptions, selectedRoots);
		return new TreeBuilder().Build(
			Assert.IsType<ProjectTreeInventorySnapshot>(snapshot.TreeInventory),
			new TreeFilterOptions(selectedExtensions, selectedRoots, rules),
			TestContext.Current.CancellationToken);
	}

	private static bool ContainsTreePath(TreeBuildResult tree, string relativePath)
	{
		var children = tree.Root.Children;
		foreach (var segment in relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
		{
			var match = children.FirstOrDefault(node => PathComparer.Default.Equals(node.Name, segment));
			if (match is null)
				return false;

			children = match.Children;
		}

		return true;
	}

	private static void AssertStableCloneBoundary(
		SelectionRefreshSnapshot snapshot,
		int expectedDotFileCount,
		bool expectedDotFilesChecked = true,
		bool expectedExtensionlessChecked = true)
	{
		AssertCloneMetadataExcluded(snapshot);
		Assert.DoesNotContain(snapshot.IgnoreOptions, option => option.Id is
			IgnoreOptionId.HiddenFolders or
			IgnoreOptionId.DotFolders or
			IgnoreOptionId.EmptyFolders or
			IgnoreOptionId.UseGitIgnore or
			IgnoreOptionId.SmartIgnore);

		var dotFiles = Assert.Single(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.DotFiles);
		var extensionless = Assert.Single(
			snapshot.IgnoreOptions,
			option => option.Id == IgnoreOptionId.ExtensionlessFiles);
		Assert.Equal(expectedDotFilesChecked, dotFiles.IsChecked);
		Assert.Equal(expectedExtensionlessChecked, extensionless.IsChecked);
		Assert.Equal(expectedDotFileCount, snapshot.IgnoreOptionCounts.DotFiles);
		Assert.Equal(1, snapshot.IgnoreOptionCounts.ExtensionlessFiles);
		Assert.Equal(0, snapshot.IgnoreOptionCounts.HiddenFolders);
		Assert.Equal(0, snapshot.IgnoreOptionCounts.DotFolders);
		Assert.Equal(0, snapshot.IgnoreOptionCounts.EmptyFolders);
	}

	private static string[] SnapshotRootNames(SelectionRefreshSnapshot snapshot) =>
		snapshot.RootOptions!.Select(static option => option.Name).ToArray();

	private static TemporaryDirectory CreateWorkspace(CloneFixtureKind fixtureKind)
	{
		var workspace = new TemporaryDirectory();
		workspace.CreateFile(Path.Combine(".git", "HEAD"), "ref: refs/heads/main\n");
		workspace.CreateFile(Path.Combine(".git", "objects", "pack", "pack-a.pack"), "metadata\n");
		workspace.CreateDirectory(Path.Combine(".git", "refs", "tags"));
		MarkHidden(Path.Combine(workspace.Path, ".git"));

		if (fixtureKind == CloneFixtureKind.Python)
		{
			workspace.CreateFile(Path.Combine("app", "bot.py"), "print('ok')\n");
			workspace.CreateFile(".env-example", "TOKEN=\n");
			workspace.CreateFile(".gitignore", "__pycache__/\n*.pyc\n");
			workspace.CreateFile("LICENSE", "MIT\n");
			workspace.CreateFile("README.md", "# Bot\n");
			workspace.CreateFile("requirements.txt", "requests\n");
			workspace.CreateFile("install.sh", "#!/bin/sh\n");
			return workspace;
		}

		workspace.CreateFile(Path.Combine("OmenGamingHubUnlocker", "App.csproj"), "<Project />\n");
		workspace.CreateFile(Path.Combine("OmenGamingHubUnlocker", "Program.cs"), "class Program {}\n");
		workspace.CreateFile(Path.Combine("Tests", "AppTests.cs"), "class AppTests {}\n");
		workspace.CreateFile(".gitignore", "[Bb]in/\n[Oo]bj/\nartifacts/\n");
		workspace.CreateFile("OmenGamingHubUnlocker.sln", "Microsoft Visual Studio Solution File\n");
		workspace.CreateFile("README", "Omen Gaming Hub Unlocker\n");
		return workspace;
	}

	private static TemporaryDirectory CreateDirectoryToggleWorkspace()
	{
		var workspace = new TemporaryDirectory();
		workspace.CreateFile(Path.Combine(".git", "HEAD"), "ref: refs/heads/main\n");
		workspace.CreateFile(Path.Combine(".git", "objects", "pack", "pack-a.pack"), "metadata\n");
		workspace.CreateDirectory(Path.Combine(".git", "refs", "tags"));
		MarkHidden(Path.Combine(workspace.Path, ".git"));
		workspace.CreateFile(Path.Combine(".workspace", "settings.json"), "{}\n");
		workspace.CreateDirectory("empty-root");
		workspace.CreateFile(Path.Combine("src", "app.cs"), "class App {}\n");
		return workspace;
	}

	private static TemporaryDirectory CreateBareManagedCloneWorkspace()
	{
		var workspace = new TemporaryDirectory();
		workspace.CreateFile(Path.Combine(".git", "HEAD"), "ref: refs/heads/main\n");
		workspace.CreateFile(Path.Combine(".git", "objects", "pack", "pack-a.pack"), "metadata\n");
		workspace.CreateDirectory(Path.Combine(".git", "refs", "tags"));
		MarkHidden(Path.Combine(workspace.Path, ".git"));
		workspace.CreateFile(Path.Combine("src", "app.cs"), "class App {}\n");
		return workspace;
	}

	private static ProjectLoadWorkflowRefreshHarness.WorkflowServices CreateManagedCloneServices() =>
		ProjectLoadWorkflowRefreshHarness.CreateServices(
			transformRules: static rules => rules with { ExcludedRootFolderName = ".git" });

	private static void MarkHidden(string path)
	{
		try
		{
			File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
		}
		catch when (!OperatingSystem.IsWindows())
		{
			// Dot-name semantics still make this a valid clone-metadata fixture on Unix.
		}
	}

	public enum CloneFixtureKind
	{
		Python,
		DotNet
	}
}
