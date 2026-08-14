using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class ProjectTreeUiStateTests
{
    [Fact]
    public void CheckedRootAndEmptySelection_RoundTripRemainDistinct()
    {
        var descriptor = CreateProjectDescriptor();
        var checkedTree = BuildTree(descriptor);
        var emptyTree = BuildTree(descriptor);
        var cache = new TreeSelectionSnapshotCache();
        checkedTree.IsChecked = true;

        var checkedSnapshot = ProjectTreeSelectionSnapshot.Capture(
            descriptor.FullPath,
            [checkedTree],
            cache);
        cache.ResetForTreeReplacement();
        var emptySnapshot = ProjectTreeSelectionSnapshot.Capture(
            descriptor.FullPath,
            [emptyTree],
            cache);

        var restoredCheckedTree = BuildTree(descriptor);
        var restoredEmptyTree = BuildTree(descriptor);
        Assert.True(checkedSnapshot!.Restore(restoredCheckedTree).Applied);
        var emptyRestore = emptySnapshot!.Restore(restoredEmptyTree);
        Assert.True(emptyRestore.Applied);

        Assert.True(restoredCheckedTree.IsChecked);
        Assert.All(restoredCheckedTree.Children, static node => Assert.True(node.IsChecked));
        Assert.False(restoredEmptyTree.IsChecked);
        Assert.All(restoredEmptyTree.Children, static node => Assert.False(node.IsChecked));
        Assert.Equal(0, emptyRestore.PathLookupChildInspectionCount);
        Assert.Equal(0, emptyRestore.CheckedStateRecalculationCount);
    }

    [Fact]
    public void PartialSelection_RestoresTriStateAndDeferredChildren()
    {
        var descriptor = CreateProjectDescriptor();
        var source = BuildTree(descriptor);
        var selectedFolder = source.Children[0];
        var selectedLeaf = selectedFolder.Children[0];
        selectedLeaf.IsChecked = true;

        var snapshot = ProjectTreeSelectionSnapshot.Capture(
            descriptor.FullPath,
            [source],
            new TreeSelectionSnapshotCache());
        var restored = BuildTree(descriptor);

        snapshot!.Restore(restored);

        var restoredFolder = restored.Children[0];
        Assert.True(restoredFolder.AreChildrenRealized);
        Assert.True(restoredFolder.Children[0].IsChecked);
        Assert.False(restoredFolder.Children[1].IsChecked);
        Assert.Null(restoredFolder.IsChecked);
        Assert.Null(restored.IsChecked);

        var folderSource = BuildTree(descriptor);
        folderSource.Children[0].IsChecked = true;
        var folderSnapshot = ProjectTreeSelectionSnapshot.Capture(
            descriptor.FullPath,
            [folderSource],
            new TreeSelectionSnapshotCache());
        var deferredRestore = BuildTree(descriptor);
        folderSnapshot!.Restore(deferredRestore);

        var deferredFolder = deferredRestore.Children[0];
        Assert.False(deferredFolder.AreChildrenRealized);
        Assert.True(deferredFolder.IsChecked);
        deferredFolder.IsExpanded = true;
        Assert.All(deferredFolder.Children, static child => Assert.True(child.IsChecked));
    }

    [Fact]
    public void ExpansionCaptureAndRestore_OnlyRealizeExpandedBranches()
    {
        var descriptor = CreateProjectDescriptor();
        var source = BuildTree(descriptor);
        source.IsExpanded = true;
        source.Children[0].IsExpanded = true;
        Assert.False(source.Children[1].AreChildrenRealized);

        var snapshot = ProjectTreeUiState.CaptureExpansion(
            descriptor.FullPath,
            [source]);
        var restored = BuildTree(descriptor);

        Assert.True(ProjectTreeUiState.RestoreExpansion(restored, snapshot));

        Assert.True(restored.IsExpanded);
        Assert.True(restored.Children[0].IsExpanded);
        Assert.True(restored.Children[0].AreChildrenRealized);
        Assert.False(restored.Children[1].IsExpanded);
        Assert.False(restored.Children[1].AreChildrenRealized);

        var missingPathSnapshot = new ProjectTreeExpansionSnapshot(
            descriptor.FullPath,
            [descriptor.FullPath, Path.Combine(descriptor.FullPath, "missing", "child")]);
        Assert.True(ProjectTreeUiState.RestoreExpansion(restored, missingPathSnapshot));
    }

    [Fact]
    public void RestoreSelection_MissingCountIsExact()
    {
        var descriptor = CreateProjectDescriptor();
        var source = BuildTree(descriptor);
        source.Children[0].Children[0].IsChecked = true;
        source.Children[1].Children[0].IsChecked = true;
        var snapshot = ProjectTreeSelectionSnapshot.Capture(
            descriptor.FullPath,
            [source],
            new TreeSelectionSnapshotCache());
        var reducedDescriptor = descriptor with
        {
            Children = [descriptor.Children[0]]
        };

        var result = snapshot!.Restore(BuildTree(reducedDescriptor));

        Assert.True(result.Applied);
        Assert.Equal(1, result.MissingCheckedPathCount);
    }

    [Fact]
    public void SnapshotFromAnotherProject_IsNotApplied()
    {
        var firstDescriptor = CreateProjectDescriptor("First");
        var secondDescriptor = CreateProjectDescriptor("Second");
        var source = BuildTree(firstDescriptor);
        source.IsChecked = true;
        var snapshot = ProjectTreeSelectionSnapshot.Capture(
            firstDescriptor.FullPath,
            [source],
            new TreeSelectionSnapshotCache());
        var secondTree = BuildTree(secondDescriptor);

        var result = snapshot!.Restore(secondTree);

        Assert.False(result.Applied);
        Assert.False(secondTree.IsChecked);
        Assert.False(ProjectTreeUiState.RestoreExpansion(
            secondTree,
            new ProjectTreeExpansionSnapshot(firstDescriptor.FullPath, [firstDescriptor.FullPath])));
    }

    [Fact]
    public void CheckedFolder_SelectsFilesAddedByRefresh()
    {
        var descriptor = CreateProjectDescriptor();
        var source = BuildTree(descriptor);
        source.Children[0].IsChecked = true;
        var snapshot = ProjectTreeSelectionSnapshot.Capture(
            descriptor.FullPath,
            [source],
            new TreeSelectionSnapshotCache());
        var sourceDescriptor = descriptor.Children[0];
        var addedFile = CreateFile(sourceDescriptor.FullPath, "new.cs");
        var refreshedDescriptor = descriptor with
        {
            Children =
            [
                sourceDescriptor with { Children = [.. sourceDescriptor.Children, addedFile] },
                descriptor.Children[1]
            ]
        };
        var refreshed = BuildTree(refreshedDescriptor);

        snapshot!.Restore(refreshed);
        var refreshedSource = refreshed.Children[0];

        Assert.False(refreshedSource.AreChildrenRealized);
        refreshedSource.IsExpanded = true;
        Assert.Equal(3, refreshedSource.Children.Count);
        Assert.All(refreshedSource.Children, static child => Assert.True(child.IsChecked));
    }

    [Fact]
    public void FilterOverrides_PreserveHiddenSelectionAndLatestUserChanges()
    {
        var descriptor = CreateProjectDescriptor();
        var source = BuildTree(descriptor);
        source.Children[0].Children[0].IsChecked = true;
        source.Children[1].Children[0].IsChecked = true;
        var snapshot = ProjectTreeSelectionSnapshot.Capture(
            descriptor.FullPath,
            [source],
            new TreeSelectionSnapshotCache());
        snapshot!.RecordOverride(source.Children[0].FullPath, isChecked: true);
        snapshot.RecordOverride(source.Children[0].Children[1].FullPath, isChecked: false);

        var restored = BuildTree(descriptor);
        snapshot.Restore(restored);

        Assert.True(restored.Children[0].Children[0].IsChecked);
        Assert.False(restored.Children[0].Children[1].IsChecked);
        Assert.True(restored.Children[1].Children[0].IsChecked);
    }

    [Fact]
    public void WidePartialSelection_RestoresWithOneChildScanAndOneParentRecalculation()
    {
        const int childCount = 20_000;
        var rootPath = Path.Combine(Path.GetTempPath(), "DevProjex-TreeState", "Wide");
        var children = new TreeNodeDescriptor[childCount];
        for (var index = 0; index < children.Length; index++)
            children[index] = CreateFile(rootPath, $"file-{index:D5}.cs");

        var descriptor = CreateFolder(rootPath, "Wide", children);
        var source = BuildTree(descriptor);
        source.IsChecked = true;
        source.Children[^1].IsChecked = false;
        var snapshot = ProjectTreeSelectionSnapshot.Capture(
            descriptor.FullPath,
            [source],
            new TreeSelectionSnapshotCache());
        var restored = BuildTree(descriptor);

        var result = snapshot!.Restore(restored);

        Assert.Equal(childCount, result.PathLookupChildInspectionCount);
        Assert.Equal(1, result.CheckedStateRecalculationCount);
        Assert.Null(restored.IsChecked);
        Assert.True(restored.Children[0].IsChecked);
        Assert.False(restored.Children[^1].IsChecked);
    }

    [Fact]
    public void DeepSparseSelection_OnlyRealizesBranchesOnSelectedPaths()
    {
        const int folderCount = 128;
        var rootPath = Path.Combine(Path.GetTempPath(), "DevProjex-TreeState", "Sparse");
        var folders = new TreeNodeDescriptor[folderCount];
        for (var index = 0; index < folders.Length; index++)
        {
            var folderPath = Path.Combine(rootPath, $"folder-{index:D3}");
            var nestedPath = Path.Combine(folderPath, "nested");
            folders[index] = CreateFolder(
                folderPath,
                $"folder-{index:D3}",
                CreateFolder(
                    nestedPath,
                    "nested",
                    CreateFile(nestedPath, "selected.cs"),
                    CreateFile(nestedPath, "other.cs")));
        }

        var descriptor = CreateFolder(rootPath, "Sparse", folders);
        var source = BuildTree(descriptor);
        source.Children[0].Children[0].Children[0].IsChecked = true;
        source.Children[^1].Children[0].Children[0].IsChecked = true;
        var snapshot = ProjectTreeSelectionSnapshot.Capture(
            descriptor.FullPath,
            [source],
            new TreeSelectionSnapshotCache());
        var restored = BuildTree(descriptor);

        var result = snapshot!.Restore(restored);

        Assert.Equal(folderCount + 6, result.PathLookupChildInspectionCount);
        Assert.True(restored.Children[0].AreChildrenRealized);
        Assert.True(restored.Children[^1].AreChildrenRealized);
        Assert.False(restored.Children[folderCount / 2].AreChildrenRealized);
        Assert.True(restored.Children[0].Children[0].Children[0].IsChecked);
        Assert.True(restored.Children[^1].Children[0].Children[0].IsChecked);
    }

    [Fact]
    public void RepeatedFilterOverrides_AreCompactedWithoutChangingOperationOrder()
    {
        var descriptor = CreateProjectDescriptor();
        var snapshot = ProjectTreeSelectionSnapshot.Capture(
            descriptor.FullPath,
            [BuildTree(descriptor)],
            new TreeSelectionSnapshotCache());
        var folderPath = descriptor.Children[0].FullPath;
        var leafPath = descriptor.Children[0].Children[0].FullPath;
        for (var index = 0; index < 1_000; index++)
            snapshot!.RecordOverride(folderPath, isChecked: (index & 1) == 0);
        snapshot!.RecordOverride(leafPath, isChecked: true);

        var restored = BuildTree(descriptor);
        snapshot.Restore(restored);

        Assert.Equal(2, snapshot.EffectiveOverrideCount);
        Assert.True(snapshot.StoredOverrideCount < 64);
        Assert.Null(restored.Children[0].IsChecked);
        Assert.True(restored.Children[0].Children[0].IsChecked);
        Assert.False(restored.Children[0].Children[1].IsChecked);
    }

    [Fact]
    public void MissingPathUncheckedByLatestOverride_IsNotReportedAsLost()
    {
        var descriptor = CreateProjectDescriptor();
        var source = BuildTree(descriptor);
        var selectedPath = source.Children[0].Children[0].FullPath;
        source.Children[0].Children[0].IsChecked = true;
        var snapshot = ProjectTreeSelectionSnapshot.Capture(
            descriptor.FullPath,
            [source],
            new TreeSelectionSnapshotCache());
        snapshot!.RecordOverride(selectedPath, isChecked: false);
        var sourceDescriptor = descriptor.Children[0];
        var reduced = descriptor with
        {
            Children =
            [
                sourceDescriptor with { Children = [sourceDescriptor.Children[1]] },
                descriptor.Children[1]
            ]
        };

        var result = snapshot.Restore(BuildTree(reduced));

        Assert.Equal(0, result.MissingCheckedPathCount);
    }

    [Fact]
    public void OptimizedRestore_MatchesSequentialCheckboxSemanticsAcrossInterleavings()
    {
        var descriptor = CreateProjectDescriptor();
        var paths = CollectDescriptorPaths(descriptor);

        for (var seed = 0; seed < 200; seed++)
        {
            var random = new Random(seed);
            var source = BuildTree(descriptor);
            var expected = BuildTree(descriptor);
            for (var operation = 0; operation < 20; operation++)
            {
                var path = paths[random.Next(paths.Count)];
                var value = random.Next(2) == 0;
                ProjectTreeUiState.FindNodeByPath(source, path)!.IsChecked = value;
                ProjectTreeUiState.FindNodeByPath(expected, path)!.IsChecked = value;
            }

            var snapshot = ProjectTreeSelectionSnapshot.Capture(
                descriptor.FullPath,
                [source],
                new TreeSelectionSnapshotCache());
            for (var operation = 0; operation < 100; operation++)
            {
                var path = paths[random.Next(paths.Count)];
                var value = random.Next(2) == 0;
                snapshot!.RecordOverride(path, value);
                ProjectTreeUiState.FindNodeByPath(expected, path)!.IsChecked = value;
            }

            var actual = BuildTree(descriptor);
            snapshot!.Restore(actual);
            var expectedNodes = expected.Flatten().ToArray();
            var actualNodes = actual.Flatten().ToArray();

            Assert.Equal(expectedNodes.Length, actualNodes.Length);
            for (var index = 0; index < expectedNodes.Length; index++)
            {
                Assert.Equal(expectedNodes[index].FullPath, actualNodes[index].FullPath);
                Assert.Equal(expectedNodes[index].IsChecked, actualNodes[index].IsChecked);
            }
        }
    }

    private static TreeNodeDescriptor CreateProjectDescriptor(string projectName = "Project")
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "DevProjex-TreeState", projectName);
        var sourcePath = Path.Combine(rootPath, "src");
        var docsPath = Path.Combine(rootPath, "docs");
        return CreateFolder(
            rootPath,
            projectName,
            CreateFolder(
                sourcePath,
                "src",
                CreateFile(sourcePath, "first.cs"),
                CreateFile(sourcePath, "second.cs")),
            CreateFolder(
                docsPath,
                "docs",
                CreateFile(docsPath, "readme.md")));
    }

    private static IReadOnlyList<string> CollectDescriptorPaths(TreeNodeDescriptor root)
    {
        var paths = new List<string>();
        var pending = new Stack<TreeNodeDescriptor>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            paths.Add(current.FullPath);
            for (var index = current.Children.Count - 1; index >= 0; index--)
                pending.Push(current.Children[index]);
        }

        return paths;
    }

    private static TreeNodeDescriptor CreateFolder(
        string fullPath,
        string name,
        params TreeNodeDescriptor[] children) =>
        new(
            name,
            fullPath,
            IsDirectory: true,
            IsAccessDenied: false,
            IconKey: "folder",
            Children: children);

    private static TreeNodeDescriptor CreateFile(string parentPath, string name) =>
        new(
            name,
            Path.Combine(parentPath, name),
            IsDirectory: false,
            IsAccessDenied: false,
            IconKey: "file",
            Children: []);

    private static TreeNodeViewModel BuildTree(TreeNodeDescriptor descriptor)
    {
        return BuildNode(descriptor, parent: null, materializeChildren: true);

        static TreeNodeViewModel BuildNode(
            TreeNodeDescriptor currentDescriptor,
            TreeNodeViewModel? parent,
            bool materializeChildren)
        {
            TreeNodeViewModel node;
            if (materializeChildren || currentDescriptor.Children.Count == 0)
            {
                node = new TreeNodeViewModel(currentDescriptor, parent, icon: null);
            }
            else
            {
                node = new TreeNodeViewModel(
                    currentDescriptor,
                    parent,
                    icon: null,
                    childrenFactory: current => BuildChildren(current));
            }

            if (materializeChildren)
            {
                var children = BuildChildren(node);
                for (var index = 0; index < children.Count; index++)
                    node.Children.Add(children[index]);
            }

            return node;
        }

        static IReadOnlyList<TreeNodeViewModel> BuildChildren(TreeNodeViewModel parent)
        {
            var descriptors = parent.Descriptor.Children;
            var children = new List<TreeNodeViewModel>(descriptors.Count);
            for (var index = 0; index < descriptors.Count; index++)
            {
                children.Add(BuildNode(
                    descriptors[index],
                    parent,
                    materializeChildren: false));
            }

            return children;
        }
    }
}
