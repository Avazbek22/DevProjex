namespace DevProjex.Tests.Unit;

public sealed class ProjectWorkspaceScanProjectionTests
{
	[Fact]
	public void TryProjectSelectedRoots_AggregatesRetainedRootsAndRemovedRootProbes()
	{
		var source = CreateSource();

		var reused = ProjectWorkspaceScanProjection.TryProjectSelectedRoots(
			source,
			["keep"],
			includeDirectoryToggleProbeRoots: true,
			includeControllerImpactProbeRoots: true,
			retainedRemovedRootEmptyFolderImpactRoots: null,
			out var projected);

		Assert.True(reused);
		Assert.Equal([".cs", ".root"], projected.Value.IgnoreSection.Extensions.Order());
		Assert.Equal(
			new IgnoreOptionCounts(HiddenFolders: 1, EmptyFolders: 2, DotFolders: 1),
			projected.Value.IgnoreSection.EffectiveIgnoreOptionCounts);
		Assert.Equal(
			new IgnoreControllerImpactCounts(GitIgnore: 1, SmartIgnore: 1),
			projected.Value.IgnoreSection.ControllerImpactCounts);
		Assert.Same(source.TreeInventory, projected.Value.TreeInventory);
	}

	[Fact]
	public void TryProjectSelectedRoots_RetainsOnlyExplicitlyOwnedEmptyFolderImpact()
	{
		var source = CreateSource();

		var reused = ProjectWorkspaceScanProjection.TryProjectSelectedRoots(
			source,
			["keep"],
			includeDirectoryToggleProbeRoots: true,
			includeControllerImpactProbeRoots: true,
			retainedRemovedRootEmptyFolderImpactRoots:
				new HashSet<string>(["remove"], PathComparer.Default),
			out var projected);

		Assert.True(reused);
		Assert.Equal(
			new IgnoreOptionCounts(HiddenFolders: 1, EmptyFolders: 5, DotFolders: 1),
			projected.Value.IgnoreSection.EffectiveIgnoreOptionCounts);
		Assert.DoesNotContain(".md", projected.Value.IgnoreSection.Extensions);
		Assert.DoesNotContain(".tmp", projected.Value.IgnoreSection.Extensions);
		Assert.Equal(0, projected.Value.IgnoreSection.EffectiveIgnoreOptionCounts.EmptyFiles);
	}

	[Fact]
	public void TryProjectSelectedRoots_UnknownRootFallsBack()
	{
		var reused = ProjectWorkspaceScanProjection.TryProjectSelectedRoots(
			CreateSource(),
			["new-root"],
			includeDirectoryToggleProbeRoots: true,
			includeControllerImpactProbeRoots: true,
			retainedRemovedRootEmptyFolderImpactRoots: null,
			out _);

		Assert.False(reused);
	}

	[Fact]
	public void TryProjectSelectedRoots_ProbeModeChangeFallsBack()
	{
		var reused = ProjectWorkspaceScanProjection.TryProjectSelectedRoots(
			CreateSource(),
			["keep"],
			includeDirectoryToggleProbeRoots: false,
			includeControllerImpactProbeRoots: true,
			retainedRemovedRootEmptyFolderImpactRoots: null,
			out _);

		Assert.False(reused);
	}

	[Fact]
	public void TryProjectSelectedRoots_RemovedAccessDeniedRootFallsBack()
	{
		var source = CreateSource(removedRootHadAccessDenied: true);

		var reused = ProjectWorkspaceScanProjection.TryProjectSelectedRoots(
			source,
			["keep"],
			includeDirectoryToggleProbeRoots: true,
			includeControllerImpactProbeRoots: true,
			retainedRemovedRootEmptyFolderImpactRoots: null,
			out _);

		Assert.False(reused);
	}

	private static ProjectWorkspaceScanSnapshot CreateSource(bool removedRootHadAccessDenied = false)
	{
		var rootFiles = CreateSection(
			[".root"],
			effectiveCounts: IgnoreOptionCounts.Empty,
			rawCounts: new IgnoreOptionCounts(HiddenFiles: 1));
		var roots = new Dictionary<string, ProjectWorkspaceRootScanSnapshot>(PathComparer.Default)
		{
			["keep"] = new(
				CreateSection([".cs"], new IgnoreOptionCounts(EmptyFolders: 2)),
				IgnoreOptionCounts.Empty,
				IgnoreControllerImpactCounts.Empty,
				RootAccessDenied: false,
				HadAccessDenied: false),
			["remove"] = new(
				CreateSection([".md"], new IgnoreOptionCounts(EmptyFolders: 3, EmptyFiles: 4)),
				new IgnoreOptionCounts(DotFolders: 1),
				new IgnoreControllerImpactCounts(SmartIgnore: 1),
				RootAccessDenied: false,
				HadAccessDenied: removedRootHadAccessDenied),
			["controller-remove"] = new(
				CreateSection([".tmp"], new IgnoreOptionCounts(EmptyFolders: 7, EmptyFiles: 8)),
				IgnoreOptionCounts.Empty,
				IgnoreControllerImpactCounts.Empty,
				RootAccessDenied: false,
				HadAccessDenied: false)
		};
		var breakdown = new ProjectWorkspaceScanBreakdown(
			rootFiles,
			roots,
			new IgnoreOptionCounts(HiddenFolders: 1),
			new IgnoreControllerImpactCounts(GitIgnore: 1),
			IncludesDirectoryToggleProbeRoots: true,
			IncludesControllerImpactProbeRoots: true,
			RootEnumerationAccessDenied: false,
			RootEnumerationHadAccessDenied: false,
			RootFilesAccessDenied: false,
			RootFilesHadAccessDenied: false);
		var inventory = new ProjectTreeInventorySnapshot(
			[
				new ProjectTreeInventoryEntry(
					"project",
					"/project",
					string.Empty,
					parentIndex: -1,
					isDirectory: true,
					isHidden: false,
					length: 0)
			],
			rootAccessDenied: false,
			hadAccessDenied: false);

		return new ProjectWorkspaceScanSnapshot(
			CreateSection(
				[".root", ".cs", ".md", ".tmp"],
				new IgnoreOptionCounts(EmptyFolders: 12, EmptyFiles: 12)),
			inventory,
			breakdown);
	}

	private static IgnoreSectionScanData CreateSection(
		IEnumerable<string> extensions,
		IgnoreOptionCounts effectiveCounts,
		IgnoreOptionCounts rawCounts = default) =>
		new(
			new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase),
			rawCounts,
			effectiveCounts,
			IgnoreControllerImpactCounts.Empty,
			new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase));
}
