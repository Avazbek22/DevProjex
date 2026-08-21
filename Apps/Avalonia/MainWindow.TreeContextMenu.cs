using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
	private bool CanUseTreeContextContentAndSelection() =>
		_viewModel.CanUseProjectWorkspaceActions &&
		!_viewModel.IsProjectLoadInProgress &&
		!_selectionCoordinator.HasPreparedSelection;

	private Task<TransformedFileContentResult> ReadTreeNodeContentAsync(
		TreeNodeViewModel node,
		CancellationToken cancellationToken)
	{
		var transformationContext = CreateContentTransformationContext();
		return _transformedFileContentReader.ReadAsync(
			node.FullPath,
			transformationContext,
			cancellationToken);
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
