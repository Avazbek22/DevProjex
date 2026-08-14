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
        Assert.True(emptySnapshot!.Restore(restoredEmptyTree).Applied);

        Assert.True(restoredCheckedTree.IsChecked);
        Assert.All(restoredCheckedTree.Children, static node => Assert.True(node.IsChecked));
        Assert.False(restoredEmptyTree.IsChecked);
        Assert.All(restoredEmptyTree.Children, static node => Assert.False(node.IsChecked));
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
