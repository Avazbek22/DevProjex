using DevProjex.Application.Models;

namespace DevProjex.Tests.Integration;

internal static class SelectionSnapshotContractAssertions
{
    public static void AssertAllSectionsConsistent(
        string rootPath,
        IgnoreRulesService ignoreRulesService,
        SelectionRefreshSnapshot snapshot)
    {
        var rootOptions = Assert.IsAssignableFrom<IReadOnlyList<SelectionOption>>(snapshot.RootOptions);
        AssertUnique(rootOptions.Select(static option => option.Name), PathComparer.Default, "root options");
        Assert.All(rootOptions, option =>
            Assert.True(
                Directory.Exists(Path.Combine(rootPath, option.Name)),
                $"Root option '{option.Name}' must reference a direct directory."));

        AssertUnique(
            snapshot.ExtensionOptions.Select(static option => option.Name),
            StringComparer.OrdinalIgnoreCase,
            "extension state options");
        AssertUnique(
            snapshot.EffectiveExtensionOptions.Select(static option => option.Name),
            StringComparer.OrdinalIgnoreCase,
            "effective extension options");
        var extensionStateNames = snapshot.ExtensionOptions
            .Select(static option => option.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(snapshot.EffectiveExtensionOptions, option =>
            Assert.Contains(option.Name, extensionStateNames));

        Assert.Equal(
            snapshot.IgnoreOptions.Count,
            snapshot.IgnoreOptions.Select(static option => option.Id).Distinct().Count());
        foreach (var option in snapshot.IgnoreOptions)
        {
            Assert.True(
                snapshot.IgnoreOptionStateCache.TryGetValue(option.Id, out var cachedState),
                $"Visible ignore option '{option.Id}' must exist in the state cache.");
            Assert.Equal(option.IsChecked, cachedState);
        }

        AssertNonNegative(snapshot.IgnoreOptionCounts);
        Assert.True(snapshot.ControllerImpactCounts.GitIgnore >= 0);
        Assert.True(snapshot.ControllerImpactCounts.SmartIgnore >= 0);
        Assert.True(snapshot.ExtensionlessEntriesCount >= 0);

        AssertCheckedRootsMatchTree(rootPath, ignoreRulesService, snapshot);
    }

    private static void AssertCheckedRootsMatchTree(
        string rootPath,
        IgnoreRulesService ignoreRulesService,
        SelectionRefreshSnapshot snapshot)
    {
        var selectedRoots = ProjectLoadWorkflowRefreshHarness.CollectCheckedRootNames(snapshot);
        var selectedExtensions = snapshot.EffectiveExtensionOptions
            .Where(static option => option.IsChecked)
            .Select(static option => option.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedIgnoreOptions = ProjectLoadWorkflowRefreshHarness.CollectCheckedIgnoreOptionIds(snapshot);
        var rules = ignoreRulesService.Build(rootPath, selectedIgnoreOptions, selectedRoots);
        var tree = new TreeBuilder().Build(
            Assert.IsType<ProjectTreeInventorySnapshot>(snapshot.TreeInventory),
            new TreeFilterOptions(
                AllowedExtensions: selectedExtensions,
                AllowedRootFolders: selectedRoots,
                IgnoreRules: rules),
            TestContext.Current.CancellationToken);
        var treeRoots = tree.Root.Children
            .Where(static child => child.IsDirectory)
            .Select(static child => child.Name)
            .ToHashSet(PathComparer.Default);

        Assert.True(
            selectedRoots.SetEquals(treeRoots),
            $"Checked root options [{string.Join(", ", selectedRoots.Order(PathComparer.Default))}] " +
            $"must equal tree roots [{string.Join(", ", treeRoots.Order(PathComparer.Default))}].");
    }

    private static void AssertUnique(
        IEnumerable<string> values,
        IEqualityComparer<string> comparer,
        string sectionName)
    {
        var materialized = values.ToArray();
        Assert.True(
            materialized.Length == materialized.Distinct(comparer).Count(),
            $"{sectionName} must not contain duplicates: [{string.Join(", ", materialized)}].");
    }

    private static void AssertNonNegative(IgnoreOptionCounts counts)
    {
        Assert.True(counts.HiddenFolders >= 0);
        Assert.True(counts.HiddenFiles >= 0);
        Assert.True(counts.DotFolders >= 0);
        Assert.True(counts.DotFiles >= 0);
        Assert.True(counts.EmptyFolders >= 0);
        Assert.True(counts.ExtensionlessFiles >= 0);
        Assert.True(counts.EmptyFiles >= 0);
    }
}
