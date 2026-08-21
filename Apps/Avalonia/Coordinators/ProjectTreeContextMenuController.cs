using DevProjex.Avalonia.Services;
using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Avalonia.Coordinators;

internal sealed class ProjectTreeContextMenuController : IDisposable
{
	private readonly TreeView _treeView;
	private readonly LocalizationService _localization;
	private readonly IToastService _toastService;
	private readonly IProjectPathLauncher _pathLauncher;
	private readonly Func<string?> _getProjectRoot;
	private readonly Func<bool> _allowContentAndSelection;
	private readonly Func<TreeNodeViewModel, CancellationToken, Task<TransformedFileContentResult>> _readContent;
	private readonly Func<string, Task> _setClipboardText;
	private readonly Action<TreeNodeViewModel> _selectOnly;
	private readonly Action<TreeNodeViewModel, bool> _setBranchExpanded;
	private readonly Func<string, Task> _showError;
	private readonly MenuFlyout _menu = new();
	private readonly CancellationTokenSource _lifetime = new();
	private TreeNodeViewModel? _activeNode;
	private bool _disposed;

	public ProjectTreeContextMenuController(
		TreeView treeView,
		LocalizationService localization,
		IToastService toastService,
		IProjectPathLauncher pathLauncher,
		Func<string?> getProjectRoot,
		Func<bool> allowContentAndSelection,
		Func<TreeNodeViewModel, CancellationToken, Task<TransformedFileContentResult>> readContent,
		Func<string, Task> setClipboardText,
		Action<TreeNodeViewModel> selectOnly,
		Action<TreeNodeViewModel, bool> setBranchExpanded,
		Func<string, Task> showError)
	{
		_treeView = treeView ?? throw new ArgumentNullException(nameof(treeView));
		_localization = localization ?? throw new ArgumentNullException(nameof(localization));
		_toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
		_pathLauncher = pathLauncher ?? throw new ArgumentNullException(nameof(pathLauncher));
		_getProjectRoot = getProjectRoot ?? throw new ArgumentNullException(nameof(getProjectRoot));
		_allowContentAndSelection = allowContentAndSelection ?? throw new ArgumentNullException(nameof(allowContentAndSelection));
		_readContent = readContent ?? throw new ArgumentNullException(nameof(readContent));
		_setClipboardText = setClipboardText ?? throw new ArgumentNullException(nameof(setClipboardText));
		_selectOnly = selectOnly ?? throw new ArgumentNullException(nameof(selectOnly));
		_setBranchExpanded = setBranchExpanded ?? throw new ArgumentNullException(nameof(setBranchExpanded));
		_showError = showError ?? throw new ArgumentNullException(nameof(showError));

		_treeView.AddHandler(
			InputElement.PointerPressedEvent,
			OnTreePointerPressed,
			RoutingStrategies.Tunnel,
			handledEventsToo: true);
		_treeView.AddHandler(
			InputElement.KeyDownEvent,
			OnTreeKeyDown,
			RoutingStrategies.Tunnel,
			handledEventsToo: true);
	}

	internal MenuFlyout Menu => _menu;

	internal TreeNodeViewModel? ActiveNode => _activeNode;

	internal bool TryOpenForNode(
		TreeNodeViewModel node,
		Control placementTarget,
		bool showAtPointer)
	{
		if (_disposed || string.IsNullOrWhiteSpace(_getProjectRoot()))
			return false;

		_activeNode = node;
		PopulateMenu(node);
		_menu.ShowAt(placementTarget, showAtPointer);
		return true;
	}

	private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (!e.GetCurrentPoint(_treeView).Properties.IsRightButtonPressed ||
		    TryResolveTreeItem(e.Source) is not { DataContext: TreeNodeViewModel node } item)
		{
			return;
		}

		_treeView.SelectedItem = node;
		item.Focus();
		if (TryOpenForNode(node, item, showAtPointer: true))
			e.Handled = true;
	}

	private void OnTreeKeyDown(object? sender, KeyEventArgs e)
	{
		var isContextMenuKey =
			e.Key == Key.Apps ||
			(e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift);
		if (!isContextMenuKey || ResolveKeyboardNode() is not { } node)
			return;

		var placementTarget = _treeView.TreeContainerFromItem(node) as Control ?? _treeView;
		if (TryOpenForNode(node, placementTarget, showAtPointer: false))
			e.Handled = true;
	}

	private TreeNodeViewModel? ResolveKeyboardNode()
	{
		var focused = TopLevel.GetTopLevel(_treeView)?.FocusManager?.GetFocusedElement() as Visual;
		if (TryResolveTreeItem(focused) is { DataContext: TreeNodeViewModel focusedNode })
			return focusedNode;

		return _treeView.SelectedItem as TreeNodeViewModel;
	}

	private static TreeViewItem? TryResolveTreeItem(object? source)
	{
		if (source is TreeViewItem item)
			return item;
		return source is Visual visual
			? visual.FindAncestorOfType<TreeViewItem>()
			: null;
	}

	private void PopulateMenu(TreeNodeViewModel node)
	{
		_menu.Items.Clear();
		foreach (var entry in ProjectTreeContextMenuPolicy.Build(
			         node.Descriptor.IsDirectory,
			         node.IsExpanded,
			         _allowContentAndSelection()))
		{
			if (entry.Kind == ProjectTreeContextMenuEntryKind.Separator)
			{
				_menu.Items.Add(new Separator());
				continue;
			}

			var command = entry.Command ??
			              throw new InvalidOperationException("A tree context command entry has no command.");
			var item = new MenuItem
			{
				Header = ResolveHeader(command),
				IsEnabled = entry.IsEnabled,
				Tag = command,
				Cursor = new Cursor(StandardCursorType.Hand)
			};
			item.Click += OnMenuItemClick;
			_menu.Items.Add(item);
		}
	}

	private string ResolveHeader(ProjectTreeContextMenuCommand command) =>
		_localization[command switch
		{
			ProjectTreeContextMenuCommand.OpenInFileManager => "Tree.Context.OpenInFileManager",
			ProjectTreeContextMenuCommand.CopyFullPath => "Tree.Context.CopyFullPath",
			ProjectTreeContextMenuCommand.CopyRelativePath => "Tree.Context.CopyRelativePath",
			ProjectTreeContextMenuCommand.CopyContent => "Tree.Context.CopyContent",
			ProjectTreeContextMenuCommand.SelectOnly => "Tree.Context.SelectOnly",
			ProjectTreeContextMenuCommand.ExpandBranch => "Tree.Context.ExpandBranch",
			ProjectTreeContextMenuCommand.CollapseBranch => "Tree.Context.CollapseBranch",
			_ => throw new ArgumentOutOfRangeException(nameof(command), command, null)
		}];

	private async void OnMenuItemClick(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (_disposed ||
		    _activeNode is not { } node ||
		    sender is not MenuItem { Tag: ProjectTreeContextMenuCommand command })
		{
			return;
		}

		try
		{
			await ExecuteAsync(node, command, _lifetime.Token);
		}
		catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
		{
		}
		catch (Exception exception)
		{
			await _showError(exception.Message);
		}
	}

	private async Task ExecuteAsync(
		TreeNodeViewModel node,
		ProjectTreeContextMenuCommand command,
		CancellationToken cancellationToken)
	{
		switch (command)
		{
			case ProjectTreeContextMenuCommand.OpenInFileManager:
				await OpenInFileManagerAsync(node, cancellationToken);
				break;
			case ProjectTreeContextMenuCommand.CopyFullPath:
				await CopyAsync(node.FullPath, "Toast.Tree.FullPathCopied");
				break;
			case ProjectTreeContextMenuCommand.CopyRelativePath:
				await CopyRelativePathAsync(node);
				break;
			case ProjectTreeContextMenuCommand.CopyContent:
				await CopyContentAsync(node, cancellationToken);
				break;
			case ProjectTreeContextMenuCommand.SelectOnly:
				if (_allowContentAndSelection())
					_selectOnly(node);
				break;
			case ProjectTreeContextMenuCommand.ExpandBranch:
				_setBranchExpanded(node, true);
				break;
			case ProjectTreeContextMenuCommand.CollapseBranch:
				_setBranchExpanded(node, false);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(command), command, null);
		}
	}

	private async Task OpenInFileManagerAsync(
		TreeNodeViewModel node,
		CancellationToken cancellationToken)
	{
		var result = await _pathLauncher
			.LaunchAsync(node.FullPath, node.Descriptor.IsDirectory, cancellationToken);
		if (result.Succeeded)
			return;

		if (result.Failure == ProjectPathLaunchFailure.PathNotFound)
		{
			_toastService.Show(_localization.Format("Tree.Context.PathNotFound", node.FullPath));
			return;
		}

		_toastService.Show(_localization["Tree.Context.OpenFailed"]);
	}

	private async Task CopyRelativePathAsync(TreeNodeViewModel node)
	{
		var root = _getProjectRoot();
		if (string.IsNullOrWhiteSpace(root))
			return;

		await CopyAsync(
			ProjectTreePathUtility.GetRelativeDisplayPath(root, node.FullPath),
			"Toast.Tree.RelativePathCopied");
	}

	private async Task CopyContentAsync(
		TreeNodeViewModel node,
		CancellationToken cancellationToken)
	{
		if (!_allowContentAndSelection())
			return;
		if (!File.Exists(node.FullPath))
		{
			_toastService.Show(_localization.Format("Tree.Context.PathNotFound", node.FullPath));
			return;
		}

		var result = await _readContent(node, cancellationToken);
		if (!result.HasText)
		{
			_toastService.Show(_localization["Msg.NoTextContent"]);
			return;
		}

		await CopyAsync(result.Content!, "Toast.Tree.ContentCopied");
	}

	private async Task CopyAsync(string value, string toastKey)
	{
		await _setClipboardText(value);
		_toastService.Show(_localization[toastKey]);
	}

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;
		_lifetime.Cancel();
		_lifetime.Dispose();
		_treeView.RemoveHandler(InputElement.PointerPressedEvent, OnTreePointerPressed);
		_treeView.RemoveHandler(InputElement.KeyDownEvent, OnTreeKeyDown);
		foreach (var item in _menu.Items.OfType<MenuItem>())
			item.Click -= OnMenuItemClick;
		_menu.Items.Clear();
		_menu.Hide();
		_activeNode = null;
	}
}
