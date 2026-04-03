namespace DevProjex.Tests.Unit;

public sealed class IgnoreSectionRefreshPlanBuilderMatrixTests
{
    [Theory]
    [MemberData(nameof(Cases))]
    public void Build_Matrix_ProducesExpectedRefreshPlan(
        bool availabilityChanged,
        IgnoreOptionId[] beforeSelectionIds,
        IgnoreOptionId[] afterSelectionIds,
        int expectedImpactValue,
        bool expectSecondSnapshotPass,
        bool expectRootRefresh)
    {
        var beforeSnapshot = availabilityChanged
            ? new IgnoreSectionSnapshotState(true, IgnoreOptionCounts.Empty, false, 0)
            : new IgnoreSectionSnapshotState(true, new IgnoreOptionCounts(EmptyFiles: 1), false, 0);
        var afterSnapshot = availabilityChanged
            ? new IgnoreSectionSnapshotState(true, new IgnoreOptionCounts(EmptyFiles: 1), false, 0)
            : beforeSnapshot;

        var beforeSelection = beforeSelectionIds.ToHashSet();
        var afterSelection = afterSelectionIds.ToHashSet();

        var plan = IgnoreSectionRefreshPlanBuilder.Build(
            beforeSnapshot,
            afterSnapshot,
            beforeSelection,
            afterSelection);

        if (!availabilityChanged)
        {
            Assert.Equal(IgnoreSectionRefreshPlan.None, plan);
            return;
        }

        Assert.True(plan.RequiresIgnoreOptionsRefresh);
        Assert.Equal(expectSecondSnapshotPass, plan.RequiresSecondSnapshotPass);
        Assert.Equal(expectRootRefresh, plan.RequiresRootFolderRefresh);
        Assert.Equal(expectedImpactValue, (int)plan.Impact);
    }

    public static IEnumerable<object[]> Cases()
    {
        var transitions = new[]
        {
            Case(Array.Empty<IgnoreOptionId>(), Array.Empty<IgnoreOptionId>(), IgnoreOptionRefreshImpact.None, false, false),
            Case(Array.Empty<IgnoreOptionId>(), [IgnoreOptionId.EmptyFiles], IgnoreOptionRefreshImpact.FileVisibility, true, false),
            Case([IgnoreOptionId.EmptyFiles], Array.Empty<IgnoreOptionId>(), IgnoreOptionRefreshImpact.FileVisibility, true, false),
            Case(Array.Empty<IgnoreOptionId>(), [IgnoreOptionId.DotFolders], IgnoreOptionRefreshImpact.RootStructure, true, true),
            Case([IgnoreOptionId.DotFolders], Array.Empty<IgnoreOptionId>(), IgnoreOptionRefreshImpact.RootStructure, true, true),
            Case([IgnoreOptionId.EmptyFiles], [IgnoreOptionId.ExtensionlessFiles], IgnoreOptionRefreshImpact.FileVisibility, true, false),
            Case([IgnoreOptionId.DotFolders], [IgnoreOptionId.EmptyFolders], IgnoreOptionRefreshImpact.RootStructure, true, true),
            Case([IgnoreOptionId.EmptyFiles], [IgnoreOptionId.DotFolders], IgnoreOptionRefreshImpact.FileVisibility | IgnoreOptionRefreshImpact.RootStructure, true, true),
            Case([IgnoreOptionId.DotFolders], [IgnoreOptionId.EmptyFiles], IgnoreOptionRefreshImpact.FileVisibility | IgnoreOptionRefreshImpact.RootStructure, true, true),
            Case([IgnoreOptionId.EmptyFiles], [IgnoreOptionId.EmptyFiles, IgnoreOptionId.DotFolders], IgnoreOptionRefreshImpact.RootStructure, true, true),
            Case([IgnoreOptionId.DotFolders], [IgnoreOptionId.DotFolders, IgnoreOptionId.EmptyFiles], IgnoreOptionRefreshImpact.FileVisibility, true, false),
            Case(
                [IgnoreOptionId.DotFolders, IgnoreOptionId.EmptyFiles],
                [IgnoreOptionId.EmptyFolders, IgnoreOptionId.ExtensionlessFiles],
                IgnoreOptionRefreshImpact.FileVisibility | IgnoreOptionRefreshImpact.RootStructure,
                true,
                true)
        };

        foreach (var availabilityChanged in new[] { false, true })
        {
            foreach (var transition in transitions)
            {
                yield return
                [
                    availabilityChanged,
                    transition.BeforeSelectionIds,
                    transition.AfterSelectionIds,
                    (int)transition.ExpectedImpact,
                    transition.ExpectSecondSnapshotPass,
                    transition.ExpectRootRefresh
                ];
            }
        }
    }

    private static TransitionCase Case(
        IgnoreOptionId[] beforeSelectionIds,
        IgnoreOptionId[] afterSelectionIds,
        IgnoreOptionRefreshImpact expectedImpact,
        bool expectSecondSnapshotPass,
        bool expectRootRefresh) =>
        new(beforeSelectionIds, afterSelectionIds, expectedImpact, expectSecondSnapshotPass, expectRootRefresh);

    private sealed record TransitionCase(
        IgnoreOptionId[] BeforeSelectionIds,
        IgnoreOptionId[] AfterSelectionIds,
        IgnoreOptionRefreshImpact ExpectedImpact,
        bool ExpectSecondSnapshotPass,
        bool ExpectRootRefresh);
}
