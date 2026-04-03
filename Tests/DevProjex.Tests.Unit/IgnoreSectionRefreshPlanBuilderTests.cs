namespace DevProjex.Tests.Unit;

public sealed class IgnoreSectionRefreshPlanBuilderTests
{
	[Fact]
	public void Build_WhenSnapshotStateDidNotChange_ReturnsNone()
	{
		var snapshot = new IgnoreSectionSnapshotState(
			HasIgnoreOptionCounts: true,
			new IgnoreOptionCounts(EmptyFiles: 2),
			HasExtensionlessEntries: true,
			ExtensionlessEntriesCount: 1);

		var plan = IgnoreSectionRefreshPlanBuilder.Build(
			snapshot,
			snapshot,
			new HashSet<IgnoreOptionId>(),
			new HashSet<IgnoreOptionId>());

		Assert.Equal(IgnoreSectionRefreshPlan.None, plan);
	}

	[Fact]
	public void Build_WhenAvailabilityChangedButSelectionDidNotChange_RefreshesOptionsOnly()
	{
		var beforeSnapshot = new IgnoreSectionSnapshotState(
			HasIgnoreOptionCounts: false,
			IgnoreOptionCounts.Empty,
			HasExtensionlessEntries: false,
			ExtensionlessEntriesCount: 0);
		var afterSnapshot = new IgnoreSectionSnapshotState(
			HasIgnoreOptionCounts: true,
			new IgnoreOptionCounts(EmptyFiles: 1),
			HasExtensionlessEntries: false,
			ExtensionlessEntriesCount: 0);

		var plan = IgnoreSectionRefreshPlanBuilder.Build(
			beforeSnapshot,
			afterSnapshot,
			new HashSet<IgnoreOptionId>(),
			new HashSet<IgnoreOptionId>());

		Assert.True(plan.RequiresIgnoreOptionsRefresh);
		Assert.False(plan.RequiresSecondSnapshotPass);
		Assert.False(plan.RequiresRootFolderRefresh);
		Assert.Equal(IgnoreOptionRefreshImpact.None, plan.Impact);
	}

	[Fact]
	public void Build_WhenFileLevelSelectionChanged_SchedulesSecondSnapshotWithoutRootRefresh()
	{
		var beforeSnapshot = new IgnoreSectionSnapshotState(
			HasIgnoreOptionCounts: false,
			IgnoreOptionCounts.Empty,
			HasExtensionlessEntries: false,
			ExtensionlessEntriesCount: 0);
		var afterSnapshot = new IgnoreSectionSnapshotState(
			HasIgnoreOptionCounts: true,
			new IgnoreOptionCounts(EmptyFiles: 1),
			HasExtensionlessEntries: false,
			ExtensionlessEntriesCount: 0);

		var plan = IgnoreSectionRefreshPlanBuilder.Build(
			beforeSnapshot,
			afterSnapshot,
			new HashSet<IgnoreOptionId>(),
			new HashSet<IgnoreOptionId> { IgnoreOptionId.EmptyFiles });

		Assert.True(plan.RequiresIgnoreOptionsRefresh);
		Assert.True(plan.RequiresSecondSnapshotPass);
		Assert.False(plan.RequiresRootFolderRefresh);
		Assert.Equal(IgnoreOptionRefreshImpact.FileVisibility, plan.Impact);
	}

	[Fact]
	public void Build_WhenDirectoryLevelSelectionChanged_SchedulesRootRefreshBeforeSecondSnapshot()
	{
		var beforeSnapshot = new IgnoreSectionSnapshotState(
			HasIgnoreOptionCounts: false,
			IgnoreOptionCounts.Empty,
			HasExtensionlessEntries: false,
			ExtensionlessEntriesCount: 0);
		var afterSnapshot = new IgnoreSectionSnapshotState(
			HasIgnoreOptionCounts: true,
			new IgnoreOptionCounts(DotFolders: 1),
			HasExtensionlessEntries: false,
			ExtensionlessEntriesCount: 0);

		var plan = IgnoreSectionRefreshPlanBuilder.Build(
			beforeSnapshot,
			afterSnapshot,
			new HashSet<IgnoreOptionId>(),
			new HashSet<IgnoreOptionId> { IgnoreOptionId.DotFolders });

		Assert.True(plan.RequiresIgnoreOptionsRefresh);
		Assert.True(plan.RequiresSecondSnapshotPass);
		Assert.True(plan.RequiresRootFolderRefresh);
		Assert.Equal(IgnoreOptionRefreshImpact.RootStructure, plan.Impact);
	}

	[Fact]
	public void Build_WhenFileAndDirectorySelectionChanged_PreservesCombinedImpact()
	{
		var beforeSnapshot = new IgnoreSectionSnapshotState(
			HasIgnoreOptionCounts: true,
			new IgnoreOptionCounts(EmptyFiles: 1),
			HasExtensionlessEntries: false,
			ExtensionlessEntriesCount: 0);
		var afterSnapshot = new IgnoreSectionSnapshotState(
			HasIgnoreOptionCounts: true,
			new IgnoreOptionCounts(EmptyFiles: 1, DotFolders: 1),
			HasExtensionlessEntries: false,
			ExtensionlessEntriesCount: 0);

		var plan = IgnoreSectionRefreshPlanBuilder.Build(
			beforeSnapshot,
			afterSnapshot,
			new HashSet<IgnoreOptionId> { IgnoreOptionId.EmptyFiles },
			new HashSet<IgnoreOptionId> { IgnoreOptionId.DotFolders });

		Assert.True(plan.RequiresIgnoreOptionsRefresh);
		Assert.True(plan.RequiresSecondSnapshotPass);
		Assert.True(plan.RequiresRootFolderRefresh);
		Assert.Equal(
			IgnoreOptionRefreshImpact.FileVisibility | IgnoreOptionRefreshImpact.RootStructure,
			plan.Impact);
	}
}
