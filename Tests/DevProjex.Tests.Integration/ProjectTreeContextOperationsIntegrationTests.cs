using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.ViewModels;
using DevProjex.Kernel.Contracts;

namespace DevProjex.Tests.Integration;

public sealed class ProjectTreeContextOperationsIntegrationTests
{
	[Fact]
	public void SelectOnlyAvailability_RequiresAnotherSelectedPath()
	{
		var root = CreateLazyNode(
			Directory("root", File("first.txt"), File("second.txt")),
			parent: null);
		var first = root.Children[0];
		var second = root.Children[1];

		root.SetCheckedForTreeStateRestore(false);
		Assert.False(ProjectTreeSelectionOperations.HasSelectionOtherThan([root], first));

		first.IsChecked = true;
		Assert.False(ProjectTreeSelectionOperations.HasSelectionOtherThan([root], first));

		second.IsChecked = true;
		Assert.True(ProjectTreeSelectionOperations.HasSelectionOtherThan([root], first));
	}

	[Fact]
	public void SelectOnly_LargeLazyTreePublishesOnceAndIsIdempotent()
	{
		var rootDescriptor = Directory(
			"root",
			Enumerable.Range(0, 2_000)
				.Select(index => Directory(
					$"folder-{index:D4}",
					File($"file-{index:D4}.txt")))
				.ToArray());
		var root = CreateLazyNode(rootDescriptor, parent: null);
		root.IsChecked = true;
		var target = root.Children[1_337];
		var recalculationCount = 0;

		if (ProjectTreeSelectionOperations.SelectOnly([root], target))
			recalculationCount++;
		if (ProjectTreeSelectionOperations.SelectOnly([root], target))
			recalculationCount++;

		Assert.Equal(1, recalculationCount);
		Assert.True(target.IsChecked);
		Assert.All(root.Children.Where(child => !ReferenceEquals(child, target)), child => Assert.False(child.IsChecked));
		var selected = new HashSet<string>(PathComparer.Default);
		root.CollectCheckedPaths(selected);
		Assert.Equal(target.FullPath, Assert.Single(selected));
	}

	[Fact]
	public void ExpandAndCollapse_DeepLazyBranchKeepsNodeStateWithoutCreatingContainers()
	{
		var descriptor = File("leaf.txt");
		for (var depth = 127; depth >= 0; depth--)
			descriptor = Directory($"level-{depth:D3}", descriptor);
		var factoryCalls = 0;
		var root = CreateLazyNode(descriptor, parent: null, () => factoryCalls++);

		root.SetExpandedRecursive(true);

		var expandedNodes = root.Flatten().ToArray();
		Assert.All(expandedNodes, node => Assert.True(node.IsExpanded));
		Assert.Equal(128, factoryCalls);

		root.SetExpandedRecursive(false);

		Assert.All(expandedNodes, node => Assert.False(node.IsExpanded));
		Assert.Equal(128, factoryCalls);
	}

	private static TreeNodeViewModel CreateLazyNode(
		TreeNodeDescriptor descriptor,
		TreeNodeViewModel? parent,
		Action? factoryCalled = null) =>
		new(
			descriptor,
			parent,
			icon: null,
			childrenFactory: owner =>
			{
				factoryCalled?.Invoke();
				return descriptor.Children
					.Select(child => CreateLazyNode(child, owner, factoryCalled))
					.ToArray();
			});

	private static TreeNodeDescriptor Directory(
		string name,
		params TreeNodeDescriptor[] children) =>
		new(name, Path.Combine("C:\\project", name), true, false, "folder", children);

	private static TreeNodeDescriptor File(string name) =>
		new(name, Path.Combine("C:\\project", name), false, false, "file", []);
}
