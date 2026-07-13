using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class TreeNodeViewModelTests
{
    [Fact]
    public void Constructor_SetsDescriptorAndDisplayName()
    {
        var descriptor = CreateDescriptor("Root");

        var node = new TreeNodeViewModel(descriptor, null, null);

        Assert.Equal(descriptor, node.Descriptor);
        Assert.Equal("Root", node.DisplayName);
        Assert.Equal("Root", node.Descriptor.DisplayName);
    }

    [Fact]
    public void FullPath_ReturnsDescriptorPath()
    {
        var descriptor = CreateDescriptor("Root");
        var node = new TreeNodeViewModel(descriptor, null, null);

        Assert.Equal(descriptor.FullPath, node.FullPath);
    }

    [Fact]
    public void Parent_IsNullForRoot()
    {
        var node = new TreeNodeViewModel(CreateDescriptor("Root"), null, null);

        Assert.Null(node.Parent);
    }

    [Fact]
    public void Parent_IsSetForChild()
    {
        var root = new TreeNodeViewModel(CreateDescriptor("Root"), null, null);
        var child = new TreeNodeViewModel(CreateDescriptor("Child"), root, null);

        Assert.Equal(root, child.Parent);
    }

    [Fact]
    public void IsExpanded_DefaultsToFalse()
    {
        var node = CreateNode("Node");

        Assert.False(node.IsExpanded);
    }

    [Fact]
    public void IsSelected_DefaultsToFalse()
    {
        var node = CreateNode("Node");

        Assert.False(node.IsSelected);
    }

    [Fact]
    public void DisplayName_Changes()
    {
        var node = CreateNode("Node");

        node.DisplayName = "Updated";

        Assert.Equal("Updated", node.DisplayName);
    }

    [Fact]
    public void IsExpanded_Changes()
    {
        var node = CreateNode("Node");

        node.IsExpanded = true;

        Assert.True(node.IsExpanded);
    }

    [Fact]
    public void IsExpanded_CollapsingNode_ResetsExpandedDescendants()
    {
        var root = CreateTree();
        root.SetExpandedRecursive(true);

        root.IsExpanded = false;

        Assert.False(root.IsExpanded);
        Assert.False(root.Children[0].IsExpanded);
        Assert.False(root.Children[0].Children[0].IsExpanded);
        Assert.False(root.Children[1].IsExpanded);
    }

    [Fact]
    public void IsExpanded_CollapseWithinPreservationScope_KeepsDescendantExpansionState()
    {
        var root = CreateTree();
        root.SetExpandedRecursive(true);

        using (TreeNodeViewModel.BeginPreserveDescendantExpansionStateScope())
            root.IsExpanded = false;

        Assert.False(root.IsExpanded);
        Assert.True(root.Children[0].IsExpanded);
        Assert.True(root.Children[0].Children[0].IsExpanded);
        Assert.True(root.Children[1].IsExpanded);
    }

    [Fact]
    public void IsSelected_Changes()
    {
        var node = CreateNode("Node");

        node.IsSelected = true;

        Assert.True(node.IsSelected);
    }

    [Fact]
    public void SetExpandedRecursive_ExpandsAllChildren()
    {
        var root = CreateTree();

        root.SetExpandedRecursive(true);

        Assert.All(root.Flatten(), node => Assert.True(node.IsExpanded));
    }

    [Fact]
    public void SetExpandedRecursive_DoesNotChangeChildCount()
    {
        var root = CreateTree();
        var countBefore = root.Children.Count;

        root.SetExpandedRecursive(true);

        Assert.Equal(countBefore, root.Children.Count);
    }

    [Fact]
    public void Flatten_ReturnsSelfAndDescendants()
    {
        var root = CreateTree();

        var nodes = root.Flatten().ToList();

        Assert.Equal(4, nodes.Count);
        Assert.Contains(root, nodes);
    }

    [Fact]
    public void Flatten_ReturnsPreOrderTraversal()
    {
        var root = CreateTree();

        var nodes = root.Flatten().ToList();

        Assert.Equal("Root", nodes[0].DisplayName);
        Assert.Equal("Child", nodes[1].DisplayName);
        Assert.Equal("Leaf", nodes[2].DisplayName);
        Assert.Equal("Child2", nodes[3].DisplayName);
    }

    [Fact]
    public void EnsureParentsExpanded_SetsAncestors()
    {
        var root = CreateTree();
        var leaf = root.Children[0].Children[0];

        leaf.EnsureParentsExpanded();

        Assert.True(root.IsExpanded);
        Assert.True(root.Children[0].IsExpanded);
    }

    [Fact]
    public void IndentWidth_TracksNodeDepth()
    {
        var root = CreateTree();
        var child = root.Children[0];
        var leaf = child.Children[0];

        Assert.Equal(0, root.Depth);
        Assert.Equal(0, root.IndentWidth.Value);
        Assert.Equal(1, child.Depth);
        Assert.Equal(16, child.IndentWidth.Value);
        Assert.Equal(2, leaf.Depth);
        Assert.Equal(32, leaf.IndentWidth.Value);
    }

    [Fact]
    public void EnsureParentsExpanded_OnRoot_DoesNotExpandRoot()
    {
        var root = CreateTree();

        root.EnsureParentsExpanded();

        Assert.False(root.IsExpanded);
    }

    [Fact]
    public void IsChecked_SetsChildrenChecked()
    {
        var root = CreateTree();

        root.IsChecked = true;

        Assert.All(root.Children, child => Assert.True(child.IsChecked is true));
    }

    [Fact]
    public void IsChecked_OneChildChecked_ParentStaysUnchecked()
    {
        var root = CreateTree();

        root.Children[0].IsChecked = true;

        Assert.Null(root.IsChecked);
    }

    [Fact]
    public void IsChecked_LastChildChecked_SetsParentChecked()
    {
        var root = CreateTree();

        root.Children[0].IsChecked = true;
        root.Children[1].IsChecked = true;

        Assert.True(root.IsChecked is true);
    }

    [Fact]
    public void IsChecked_UncheckedChildKeepsParentUnchecked()
    {
        var root = CreateTree();
        root.IsChecked = true;

        root.Children[0].IsChecked = false;

        Assert.Null(root.IsChecked);
    }

    [Fact]
    public void IsChecked_AllChildrenChecked_SetsParentChecked()
    {
        var root = CreateTree();
        root.IsChecked = false;

        foreach (var child in root.Children)
            child.IsChecked = true;

        Assert.True(root.IsChecked is true);
    }

    [Fact]
    public void IsChecked_RecursiveParentUpdate()
    {
        var root = CreateTree();
        var leaf = root.Children[0].Children[0];

        leaf.IsChecked = true;
        root.Children[1].IsChecked = true;

        Assert.True(root.Children[0].IsChecked is true);
        Assert.True(root.IsChecked is true);
    }

    [Fact]
    public void SetExpandedRecursive_CollapsesAllChildren()
    {
        var root = CreateTree();
        root.SetExpandedRecursive(true);

        root.SetExpandedRecursive(false);

        Assert.All(root.Flatten(), node => Assert.False(node.IsExpanded));
    }

    [Fact]
    public void EnsureParentsExpanded_DoesNotChangeLeafSelection()
    {
        var root = CreateTree();
        var leaf = root.Children[0].Children[0];

        leaf.EnsureParentsExpanded();

        Assert.False(leaf.IsSelected);
    }

    [Fact]
    public void IsChecked_ParentStaysUncheckedWhenNoChildren()
    {
        var node = CreateNode("Leaf");

        node.IsChecked = true;

        Assert.True(node.IsChecked is true);
    }

    [Fact]
    public void IsChecked_ParentIndeterminate_ClickSetsChildrenUnchecked()
    {
        var root = CreateTree();

        root.Children[0].IsChecked = true;
        root.IsChecked = false;

        Assert.All(root.Children, child => Assert.True(child.IsChecked is false));
        Assert.True(root.IsChecked is false);
    }

    [Fact]
    public void IsChecked_CascadeSkipsBranchesAlreadyInTargetState()
    {
        var root = CreateTree();
        root.Children[0].IsChecked = true;
        var unchangedBranch = root.Children[1];
        var propertyChangedCount = 0;
        unchangedBranch.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TreeNodeViewModel.IsChecked))
                propertyChangedCount++;
        };

        root.IsChecked = false;

        Assert.Equal(0, propertyChangedCount);
        Assert.False(unchangedBranch.IsChecked);
    }

    [Fact]
    public void CheckedChangedCallback_IsScopedToOwningTree()
    {
        var firstTreeChanges = 0;
        var secondTreeChanges = 0;
        var firstRoot = new TreeNodeViewModel(
            CreateDescriptor("First"),
            parent: null,
            icon: null,
            checkedChanged: _ => firstTreeChanges++);
        var secondRoot = new TreeNodeViewModel(
            CreateDescriptor("Second"),
            parent: null,
            icon: null,
            checkedChanged: _ => secondTreeChanges++);

        firstRoot.IsChecked = true;

        Assert.Equal(1, firstTreeChanges);
        Assert.Equal(0, secondTreeChanges);
    }

    [Fact]
    public void TreeSelectionSnapshotCache_ReusesSnapshotUntilSelectionChanges()
    {
        var root = CreateTree();
        root.IsChecked = true;
        var roots = new List<TreeNodeViewModel> { root };
        var cache = new TreeSelectionSnapshotCache();

        var first = cache.GetOrCreate(roots);
        var second = cache.GetOrCreate(roots);
        root.IsChecked = false;
        cache.Invalidate();
        var changed = cache.GetOrCreate(roots);

        Assert.Same(first, second);
        Assert.NotSame(first, changed);
        Assert.Contains(root.FullPath, first);
        Assert.Empty(changed);
    }

    [Fact]
    public void TreeSelectionSnapshotCache_RebuildsWhenTreeRootChanges()
    {
        var firstRoot = CreateNode("First");
        firstRoot.IsChecked = true;
        var roots = new List<TreeNodeViewModel> { firstRoot };
        var cache = new TreeSelectionSnapshotCache();
        var first = cache.GetOrCreate(roots);
        var secondRoot = CreateNode("Second");
        secondRoot.IsChecked = true;
        roots[0] = secondRoot;

        var second = cache.GetOrCreate(roots);

        Assert.NotSame(first, second);
        Assert.DoesNotContain(firstRoot.FullPath, second);
        Assert.Contains(secondRoot.FullPath, second);
    }

    [Fact]
    public void Flatten_ReturnsLeafOnlyWhenNoChildren()
    {
        var node = CreateNode("Leaf");

        var nodes = node.Flatten().ToList();

        Assert.Single(nodes);
        Assert.Equal(node, nodes[0]);
    }

    #region HasChildren and ChildItemsSource Tests (Virtualization Fix)

    [Fact]
    public void HasChildren_ReturnsFalse_WhenNoChildren()
    {
        var node = CreateNode("Leaf");

        Assert.False(node.HasChildren);
    }

    [Fact]
    public void HasChildren_ReturnsTrue_WhenHasChildren()
    {
        var root = CreateTree();

        Assert.True(root.HasChildren);
    }

    [Fact]
    public void HasChildren_ReturnsTrue_WhenHasSingleChild()
    {
        var parent = CreateNode("Parent");
        var child = new TreeNodeViewModel(CreateDescriptor("Child"), parent, null);
        parent.Children.Add(child);

        Assert.True(parent.HasChildren);
    }

    [Fact]
    public void HasChildren_ReturnsFalse_AfterClearingChildren()
    {
        var root = CreateTree();
        Assert.True(root.HasChildren);

        root.Children.Clear();

        Assert.False(root.HasChildren);
    }

    [Fact]
    public void HasChildren_ReturnsTrue_AfterAddingChild()
    {
        var node = CreateNode("Node");
        Assert.False(node.HasChildren);

        node.Children.Add(new TreeNodeViewModel(CreateDescriptor("Child"), node, null));

        Assert.True(node.HasChildren);
    }

    [Fact]
    public void HasChildren_ConsistentWithChildrenCount()
    {
        var root = CreateTree();

        Assert.Equal(root.Children.Count > 0, root.HasChildren);
    }

    [Fact]
    public void ChildItemsSource_ReturnsChildren_WhenHasChildren()
    {
        var root = CreateTree();

        var itemsSource = root.ChildItemsSource;

        Assert.Equal(root.Children, itemsSource);
    }

    [Fact]
    public void ChildItemsSource_ReturnsEmptyCollection_WhenNoChildren()
    {
        var leaf = CreateNode("Leaf");

        var itemsSource = leaf.ChildItemsSource;

        Assert.Empty(itemsSource);
        Assert.NotSame(leaf.Children, itemsSource);
    }

    [Fact]
    public void ChildItemsSource_ReturnsSameEmptyInstance_ForDifferentLeafNodes()
    {
        var leaf1 = CreateNode("Leaf1");
        var leaf2 = CreateNode("Leaf2");

        var source1 = leaf1.ChildItemsSource;
        var source2 = leaf2.ChildItemsSource;

        // Both should return the same static empty array instance
        Assert.Same(source1, source2);
    }

    [Fact]
    public void HasChildren_And_ChildItemsSource_AreConsistent()
    {
        var root = CreateTree();
        var leaf = root.Children[0].Children[0];

        // For node with children
        Assert.True(root.HasChildren);
        Assert.Same(root.Children, root.ChildItemsSource);

        // For leaf node
        Assert.False(leaf.HasChildren);
        Assert.Empty(leaf.ChildItemsSource);
    }

    [Fact]
    public void HasChildren_CorrectForDeepNesting()
    {
        var root = CreateTree();
        var child = root.Children[0];
        var leaf = child.Children[0];

        Assert.True(root.HasChildren);      // Has 2 children
        Assert.True(child.HasChildren);     // Has 1 child (Leaf)
        Assert.False(leaf.HasChildren);     // No children
    }

    [Fact]
    public void ClearRecursive_SetsHasChildrenToFalse()
    {
        var root = CreateTree();
        Assert.True(root.HasChildren);

        root.ClearRecursive();

        Assert.False(root.HasChildren);
        Assert.Empty(root.Children);
    }

    [Fact]
    public void HasChildren_ForFile_IsFalse()
    {
        // Simulate a file node (no children, IsDirectory would be false in real scenario)
        var fileDescriptor = new TreeNodeDescriptor("file.txt", @"C:\file.txt", false, false, "txt-icon", []);
        var fileNode = new TreeNodeViewModel(fileDescriptor, null, null);

        Assert.False(fileNode.HasChildren);
    }

    [Fact]
    public void HasChildren_ForEmptyDirectory_IsFalse()
    {
        // Empty directory has no children
        var dirDescriptor = new TreeNodeDescriptor("EmptyDir", @"C:\EmptyDir", true, false, "dir-icon", []);
        var dirNode = new TreeNodeViewModel(dirDescriptor, null, null);

        Assert.False(dirNode.HasChildren);
    }

    [Fact]
    public void HasChildren_ForDirectoryWithFiles_IsTrue()
    {
        var dirDescriptor = new TreeNodeDescriptor("Dir", @"C:\Dir", true, false, "dir-icon", []);
        var dirNode = new TreeNodeViewModel(dirDescriptor, null, null);

        var fileDescriptor = new TreeNodeDescriptor("file.txt", @"C:\Dir\file.txt", false, false, "txt-icon", []);
        dirNode.Children.Add(new TreeNodeViewModel(fileDescriptor, dirNode, null));

        Assert.True(dirNode.HasChildren);
    }

    [Fact]
    public void HasChildren_ReturnsTrue_ForLazyDescriptorChildrenBeforeMaterialization()
    {
        var descriptor = CreateDescriptor(
            "Root",
            new TreeNodeDescriptor("Child", @"C:\Root\Child", true, false, "icon", []));

        var node = new TreeNodeViewModel(descriptor, null, null, BuildChildrenFromDescriptor);

        Assert.True(node.HasChildren);
    }

    [Fact]
    public void ChildItemsSource_RealizesLazyChildrenOnDemand()
    {
        var descriptor = CreateDescriptor(
            "Root",
            new TreeNodeDescriptor("Child", @"C:\Root\Child", true, false, "icon", []));

        var node = new TreeNodeViewModel(descriptor, null, null, BuildChildrenFromDescriptor);

        var itemsSource = node.ChildItemsSource.ToList();

        Assert.Single(itemsSource);
        Assert.Equal("Child", itemsSource[0].DisplayName);
        Assert.Single(node.Children);
    }

    [Fact]
    public void ClearRecursive_DoesNotMaterializeLazyChildren()
    {
        var descriptor = CreateDescriptor(
            "Root",
            new TreeNodeDescriptor("Child", @"C:\Root\Child", true, false, "icon", []));
        var factoryCallCount = 0;
        var node = new TreeNodeViewModel(descriptor, null, null, parent =>
        {
            factoryCallCount++;
            return BuildChildrenFromDescriptor(parent);
        });

        node.ClearRecursive();

        Assert.Equal(0, factoryCallCount);
        Assert.False(node.HasChildren);
        Assert.Empty(node.ChildItemsSource);
    }

    [Fact]
    public void IsChecked_DoesNotMaterializeLazyChildrenForCascadeUpdates()
    {
        var leafDescriptor = new TreeNodeDescriptor("Leaf", @"C:\Root\Child\Leaf", false, false, "icon", []);
        var childDescriptor = new TreeNodeDescriptor("Child", @"C:\Root\Child", true, false, "icon", [leafDescriptor]);
        var rootDescriptor = CreateDescriptor("Root", childDescriptor);
        var factoryCallCount = 0;
        var root = new TreeNodeViewModel(rootDescriptor, null, null, parent =>
        {
            factoryCallCount++;
            return BuildChildrenFromDescriptor(parent);
        });

        root.IsChecked = true;

        Assert.True(root.IsChecked is true);
        Assert.Equal(0, factoryCallCount);
    }

    [Fact]
    public void LazyChildren_InheritDeferredCheckedState_WhenMaterializedLater()
    {
        var leafDescriptor = new TreeNodeDescriptor("Leaf", @"C:\Root\Child\Leaf", false, false, "icon", []);
        var childDescriptor = new TreeNodeDescriptor("Child", @"C:\Root\Child", true, false, "icon", [leafDescriptor]);
        var rootDescriptor = CreateDescriptor("Root", childDescriptor);
        var root = new TreeNodeViewModel(rootDescriptor, null, null, BuildChildrenFromDescriptor);

        root.IsChecked = true;

        var child = Assert.Single(root.Children);
        Assert.True(child.IsChecked is true);
        Assert.True(child.Children[0].IsChecked is true);
    }

    [Fact]
    public void CollectCheckedPaths_CheckedParent_ShortCircuitsWithoutMaterializingLazyChildren()
    {
        var childDescriptor = new TreeNodeDescriptor("Child", @"C:\Root\Child", true, false, "icon", []);
        var rootDescriptor = CreateDescriptor("Root", childDescriptor);
        var factoryCallCount = 0;
        var root = new TreeNodeViewModel(rootDescriptor, null, null, parent =>
        {
            factoryCallCount++;
            return BuildChildrenFromDescriptor(parent);
        });
        var selected = new HashSet<string>(PathComparer.Default);

        root.IsChecked = true;
        root.CollectCheckedPaths(selected);

        Assert.Equal(0, factoryCallCount);
        Assert.Equal(new[] { root.FullPath }, selected);
    }

    #endregion

    private static TreeNodeViewModel CreateNode(string name)
    {
        return new TreeNodeViewModel(CreateDescriptor(name), null, null);
    }

    private static TreeNodeViewModel CreateTree()
    {
        var root = new TreeNodeViewModel(CreateDescriptor("Root"), null, null);
        var child = new TreeNodeViewModel(CreateDescriptor("Child"), root, null);
        child.Children.Add(new TreeNodeViewModel(CreateDescriptor("Leaf"), child, null));
        var secondChild = new TreeNodeViewModel(CreateDescriptor("Child2"), root, null);
        root.Children.Add(child);
        root.Children.Add(secondChild);

        return root;
    }

    private static TreeNodeDescriptor CreateDescriptor(string name, params TreeNodeDescriptor[] children)
        => new(name, $"C:\\{name}", true, false, "icon", children);

    private static IReadOnlyList<TreeNodeViewModel> BuildChildrenFromDescriptor(TreeNodeViewModel parent)
    {
        return parent.Descriptor.Children
            .Select(child => new TreeNodeViewModel(child, parent, null, BuildChildrenFromDescriptor))
            .ToList();
    }

}
