using System.Collections.ObjectModel;
using DevProjex.Avalonia.Controls;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class ProjectTreeViewTests
{
    [AvaloniaFact]
    public void ItemsSource_PreservesHierarchyInsteadOfPublishingFlatVisibleRows()
    {
        var root = CreateNode("root");
        var folder = CreateNode("folder", root);
        var leaf = CreateNode("leaf.txt", folder, isDirectory: false);
        var sibling = CreateNode("sibling.txt", root, isDirectory: false);
        folder.Children.Add(leaf);
        root.Children.Add(folder);
        root.Children.Add(sibling);
        var roots = new ObservableCollection<TreeNodeViewModel> { root };
        var tree = new ProjectTreeView
        {
            ItemsSource = roots
        };

        Assert.Same(roots, tree.ItemsSource);
        Assert.Equal([folder, sibling], root.ChildItemsSource);
        Assert.Equal([leaf], folder.ChildItemsSource);
        Assert.DoesNotContain(folder, tree.ItemsSource.Cast<TreeNodeViewModel>());
        Assert.DoesNotContain(leaf, tree.ItemsSource.Cast<TreeNodeViewModel>());
    }

    [AvaloniaFact]
    public void ItemsSource_ProjectCollectionReplacementDoesNotRetainOldRoot()
    {
        var firstRoot = CreateNode("first");
        firstRoot.Children.Add(
            CreateNode("first-child.txt", firstRoot, isDirectory: false));
        var roots = new ObservableCollection<TreeNodeViewModel> { firstRoot };
        var tree = new ProjectTreeView
        {
            ItemsSource = roots
        };

        var secondRoot = CreateNode("second");
        roots.Clear();
        roots.Add(secondRoot);

        var publishedRoots = Assert.IsAssignableFrom<
            IEnumerable<TreeNodeViewModel>>(tree.ItemsSource);
        Assert.DoesNotContain(firstRoot, publishedRoots);
        Assert.Contains(secondRoot, publishedRoots);
    }

    private static TreeNodeViewModel CreateNode(
        string name,
        TreeNodeViewModel? parent = null,
        bool isDirectory = true)
    {
        var fullPath = parent is null
            ? Path.Combine(Path.GetTempPath(), name)
            : Path.Combine(parent.FullPath, name);
        var descriptor = new TreeNodeDescriptor(
            name,
            fullPath,
            isDirectory,
            IsAccessDenied: false,
            IconKey: "icon",
            Children: []);
        return new TreeNodeViewModel(descriptor, parent, icon: null);
    }
}
