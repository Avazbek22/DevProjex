using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
	private bool CanUseTreeContextContentAndSelection() =>
		_viewModel.CanUseProjectWorkspaceActions &&
		!_viewModel.IsProjectLoadInProgress &&
		!_selectionCoordinator.HasPreparedSelection;

	private bool IsCurrentTreeNode(TreeNodeViewModel node)
	{
		while (node.Parent is not null)
			node = node.Parent;
		return _viewModel.TreeNodes.Any(root => ReferenceEquals(root, node));
	}

	private bool ShouldShowSelectOnlyTreeNode(TreeNodeViewModel target) =>
		ProjectTreeSelectionOperations.HasSelectionOtherThan(_viewModel.TreeNodes, target);

	private Task<TransformedFileContentResult> ReadTreeNodeContentAsync(
		TreeNodeViewModel node,
		CancellationToken cancellationToken)
	{
		var transformationContext = CreateContentTransformationContext();
		return Task.Run(
			() => _transformedFileContentReader.ReadAsync(
				FindTreeRootPath(node),
				node.FullPath,
				transformationContext,
				cancellationToken),
			cancellationToken);
	}

	private static string FindTreeRootPath(TreeNodeViewModel node)
	{
		while (node.Parent is not null)
			node = node.Parent;
		return node.FullPath;
	}

	private void SelectOnlyTreeNode(TreeNodeViewModel target)
	{
		if (!CanUseTreeContextContentAndSelection())
			return;

		var changed = false;
		ApplyTreeSelectionWithoutPublishing(() =>
		{
			changed = ProjectTreeSelectionOperations.SelectOnly(
				_viewModel.TreeNodes,
				target);
		});
		if (!changed)
			return;

		if (_interactiveFilterSelectionSnapshot is { } filterSnapshot &&
		    !string.IsNullOrWhiteSpace(_currentPath) &&
		    filterSnapshot.IsForProject(_currentPath))
		{
			foreach (var root in _viewModel.TreeNodes)
				filterSnapshot.RecordOverride(root.FullPath, isChecked: false);
			filterSnapshot.RecordOverride(target.FullPath, isChecked: true);
		}

		PublishTreeSelectionChange();
	}

	private void SetTreeBranchExpanded(TreeNodeViewModel node, bool expanded)
	{
		if (!node.Descriptor.IsDirectory)
			return;

		if (expanded)
			CancelAllMemoryCleanup();

		node.SetExpandedRecursive(expanded);
		if (!expanded)
		{
			ScheduleBackgroundMemoryCleanup(
				MemoryCleanupReason.TreeCollapseCompleted);
		}
	}
}
