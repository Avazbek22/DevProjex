using System.Reflection;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DevProjex.Application.Services;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.ViewModels;
using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowTreeContextMenuUiTests(UiWorkspaceFixture workspace)
{
	[AvaloniaFact]
	public async Task PointerAndKeyboardOpenTheSingleMenuForNodesButNotForEmptySpace()
	{
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);
		try
		{
			var tree = window.FindControl<TreeView>("ProjectTree")!;
			var root = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
			root.IsExpanded = true;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
			var file = root.Children.Single(node => node.DisplayName == "README.md");
			var fileItem = FindRealizedItem(window, file);
			var controller = GetController(window);
			Assert.Same(controller.Menu, tree.ContextFlyout);
			file.IsChecked = true;

			await RightClickAsync(window, fileItem);

			Assert.Same(file, controller.ActiveNode);
			Assert.True(controller.Menu.IsOpen);
			Assert.Equal(
				["OpenInFileManager", "-", "CopyFullPath", "CopyRelativePath", "CopyContent"],
				Describe(controller.Menu));

			controller.Menu.Hide();
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			var treeOrigin = tree.TranslatePoint(default, window)!.Value;
			var emptyPoint = new Point(treeOrigin.X + 12, treeOrigin.Y + tree.Bounds.Height - 5);
			window.MouseDown(emptyPoint, MouseButton.Right, RawInputModifiers.RightMouseButton);
			window.MouseUp(emptyPoint, MouseButton.Right, RawInputModifiers.None);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 3);

			Assert.False(controller.Menu.IsOpen);

			tree.SelectedItem = file;
			fileItem.Focus();
			await UiTestDriver.PressKeyAsync(window, Key.F10, RawInputModifiers.Shift);

			Assert.True(controller.Menu.IsOpen);
			Assert.Same(file, controller.ActiveNode);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task FolderMenuHasFolderCommandsAndUsesTheSameFlyoutInstance()
	{
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);
		try
		{
			var root = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
			root.IsExpanded = true;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);
			var folder = root.Children.Single(node => node.DisplayName == "src");
			root.Children.First(node => !ReferenceEquals(node, folder)).IsChecked = true;
			var controller = GetController(window);
			var sharedMenu = controller.Menu;

			Assert.True(controller.TryOpenForNode(folder, FindRealizedItem(window, folder), showAtPointer: false));

			Assert.Same(sharedMenu, controller.Menu);
			Assert.Equal(
				["OpenInFileManager", "-", "CopyFullPath", "CopyRelativePath", "-", "SelectOnly", "ExpandBranch"],
				Describe(controller.Menu));

			folder.IsExpanded = true;
			controller.Menu.Hide();
			Assert.True(controller.TryOpenForNode(folder, FindRealizedItem(window, folder), showAtPointer: false));
			Assert.Equal("CollapseBranch", Describe(controller.Menu)[^1]);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task PathCommandsUseClipboardAndInjectedLauncher()
	{
		var launcher = new RecordingProjectPathLauncher();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			configureServices: services => services with { ProjectPathLauncher = launcher });
		try
		{
			var root = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
			root.IsExpanded = true;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);
			var file = root.Children.Single(node => node.DisplayName == "README.md");
			var controller = GetController(window);
			var placement = FindRealizedItem(window, file);

			await InvokeCommandAsync(controller, file, placement, ProjectTreeContextMenuCommand.CopyFullPath);
			Assert.Equal(file.FullPath, await ClipboardExtensions.TryGetTextAsync(window.Clipboard!));

			await InvokeCommandAsync(controller, file, placement, ProjectTreeContextMenuCommand.CopyRelativePath);
			Assert.Equal(
				$"{Path.GetFileName(Path.TrimEndingDirectorySeparator(workspace.Project.RootPath))}/README.md",
				await ClipboardExtensions.TryGetTextAsync(window.Clipboard!));

			await InvokeCommandAsync(controller, file, placement, ProjectTreeContextMenuCommand.OpenInFileManager);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => launcher.Requests.Count == 1,
				"tree file-manager request to reach the injected launcher");
			Assert.Equal((file.FullPath, false), launcher.Requests[0]);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task CopyContentUsesAppliedRedactionAndSelectOnlyUpdatesTheTree()
	{
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		try
		{
			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
			await UiTestDriver.ClickApplySettingsAsync(window);
			var root = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
			root.IsExpanded = true;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);
			var src = root.Children.Single(node => node.DisplayName == "src");
			src.IsExpanded = true;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);
			var secretFile = src.Children.Single(node => node.DisplayName == "Secrets.cs");
			var controller = GetController(window);
			var placement = FindRealizedItem(window, secretFile);

			await InvokeCommandAsync(controller, secretFile, placement, ProjectTreeContextMenuCommand.CopyContent);
			await WaitForClipboardTextAsync(window, "DEVPROJEX_REDACTED");
			var clipboard = await ClipboardExtensions.TryGetTextAsync(window.Clipboard!);
			Assert.DoesNotContain("AKIAZ7M3Q5X2P6N4R7T5", clipboard, StringComparison.Ordinal);

			root.Children.First(node => !ReferenceEquals(node, src)).IsChecked = true;
			await InvokeCommandAsync(controller, secretFile, placement, ProjectTreeContextMenuCommand.SelectOnly);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
			Assert.True(secretFile.IsChecked);
			Assert.All(root.Children.Where(node => !ReferenceEquals(node, src)), node => Assert.False(node.IsChecked));
			Assert.All(src.Children.Where(node => !ReferenceEquals(node, secretFile)), node => Assert.False(node.IsChecked));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task LoadingDisablesContentAndSelectionButKeepsPathCommandsEnabled()
	{
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);
		try
		{
			var viewModel = UiTestDriver.GetViewModel(window);
			var root = Assert.Single(viewModel.TreeNodes);
			root.IsExpanded = true;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
			var file = root.Children.Single(node => node.DisplayName == "README.md");
			root.Children.First(node => !ReferenceEquals(node, file)).IsChecked = true;
			var controller = GetController(window);
			viewModel.IsProjectLoadInProgress = true;

			Assert.True(controller.TryOpenForNode(file, FindRealizedItem(window, file), showAtPointer: false));

			Assert.False(FindCommand(controller.Menu, ProjectTreeContextMenuCommand.CopyContent).IsEnabled);
			Assert.False(FindCommand(controller.Menu, ProjectTreeContextMenuCommand.SelectOnly).IsEnabled);
			Assert.True(FindCommand(controller.Menu, ProjectTreeContextMenuCommand.OpenInFileManager).IsEnabled);
			Assert.True(FindCommand(controller.Menu, ProjectTreeContextMenuCommand.CopyFullPath).IsEnabled);
			Assert.True(FindCommand(controller.Menu, ProjectTreeContextMenuCommand.CopyRelativePath).IsEnabled);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	private static ProjectTreeContextMenuController GetController(MainWindow window)
	{
		var field = typeof(MainWindow).GetField(
			"_treeContextMenu",
			BindingFlags.Instance | BindingFlags.NonPublic);
		return Assert.IsType<ProjectTreeContextMenuController>(field?.GetValue(window));
	}

	private static TreeViewItem FindRealizedItem(MainWindow window, TreeNodeViewModel node) =>
		Assert.Single(
			window.GetVisualDescendants().OfType<TreeViewItem>(),
			item => ReferenceEquals(item.DataContext, node));

	private static async Task RightClickAsync(MainWindow window, TreeViewItem item)
	{
		var origin = item.TranslatePoint(default, window)!.Value;
		var point = new Point(origin.X + Math.Min(20, item.Bounds.Width / 2), origin.Y + item.Bounds.Height / 2);
		window.MouseDown(point, MouseButton.Right, RawInputModifiers.RightMouseButton);
		window.MouseUp(point, MouseButton.Right, RawInputModifiers.None);
		await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
	}

	private static async Task InvokeCommandAsync(
		ProjectTreeContextMenuController controller,
		TreeNodeViewModel node,
		Control placement,
		ProjectTreeContextMenuCommand command)
	{
		controller.Menu.Hide();
		Assert.True(controller.TryOpenForNode(node, placement, showAtPointer: false));
		FindCommand(controller.Menu, command).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
		await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
	}

	private static MenuItem FindCommand(
		MenuFlyout menu,
		ProjectTreeContextMenuCommand command) =>
		Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Tag, command));

	private static string[] Describe(MenuFlyout menu) =>
		menu.Items.Select(item => item switch
		{
			Separator => "-",
			MenuItem { Tag: ProjectTreeContextMenuCommand command } => command.ToString(),
			_ => "?"
		}).ToArray();

	private static async Task WaitForClipboardTextAsync(MainWindow window, string expected)
	{
		var timeout = Stopwatch.StartNew();
		while (timeout.Elapsed < TimeSpan.FromSeconds(10))
		{
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			var text = await ClipboardExtensions.TryGetTextAsync(window.Clipboard!);
			if (text?.Contains(expected, StringComparison.Ordinal) == true)
				return;
		}

		throw new XunitException($"Timed out waiting for clipboard text containing '{expected}'.");
	}

	private sealed class RecordingProjectPathLauncher : IProjectPathLauncher
	{
		public List<(string Path, bool IsDirectory)> Requests { get; } = [];

		public Task<ProjectPathLaunchResult> LaunchAsync(
			string fullPath,
			bool isDirectory,
			CancellationToken cancellationToken = default)
		{
			Requests.Add((fullPath, isDirectory));
			return Task.FromResult(ProjectPathLaunchResult.Success);
		}
	}
}
