using System.Collections.ObjectModel;
using System.Reflection;
using DevProjex.Application.Services;
using DevProjex.Kernel.Contracts;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowProjectCopyExportUiTests(UiWorkspaceFixture workspace)
{
	[AvaloniaFact]
	public async Task LoadedWindow_ProjectCopyExportsEffectiveTreeAndRestoresWorkspaceState()
	{
		var toasts = new RecordingToastService();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			configureServices: services => services with { ToastService = toasts });
		var destinationParent = Path.Combine(
			workspace.Project.AppDataPath,
			"project-copy-ui",
			Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(destinationParent);

		try
		{
			var viewModel = UiTestDriver.GetViewModel(window);
			var root = Assert.Single(viewModel.TreeNodes);
			var expectedFiles = CollectRelativeFilePaths(root.Descriptor);
			await InvokeFolderExportAsync(window, destinationParent);

			var resultPath = Assert.Single(Directory.GetDirectories(destinationParent));
			var actualFiles = Directory
				.EnumerateFiles(resultPath, "*", SearchOption.AllDirectories)
				.Select(path => NormalizeRelativePath(Path.GetRelativePath(resultPath, path)))
				.ToHashSet(PathComparer.Default);

			Assert.Equal(expectedFiles.Count, actualFiles.Count);
			Assert.True(expectedFiles.SetEquals(actualFiles));
			Assert.False(viewModel.IsProjectCopyExportInProgress);
			Assert.True(viewModel.CanChangeProjectTree);
			Assert.True(viewModel.CanUseProjectWorkspaceActions);
			Assert.False(viewModel.StatusBusy);
			Assert.Contains(toasts.Items, toast =>
				toast.Message
					.Replace("\u200B", string.Empty, StringComparison.Ordinal)
					.Contains(resultPath, PathComparer.Comparison));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task CheckedFile_ProjectCopyExportsOnlySelectedEffectivePath()
	{
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);
		var destinationParent = Path.Combine(
			workspace.Project.AppDataPath,
			"project-copy-ui-selected",
			Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(destinationParent);

		try
		{
			var viewModel = UiTestDriver.GetViewModel(window);
			var root = Assert.Single(viewModel.TreeNodes);
			root.IsChecked = false;
			var selectedFile = root.Flatten().First(static node => !node.Descriptor.IsDirectory);
			selectedFile.IsChecked = true;
			var expectedRelativePath = NormalizeRelativePath(
				Path.GetRelativePath(root.FullPath, selectedFile.FullPath));

			await InvokeFolderExportAsync(window, destinationParent);

			var resultPath = Assert.Single(Directory.GetDirectories(destinationParent));
			var actualFiles = Directory
				.EnumerateFiles(resultPath, "*", SearchOption.AllDirectories)
				.Select(path => NormalizeRelativePath(Path.GetRelativePath(resultPath, path)))
				.ToArray();

			Assert.Equal([expectedRelativePath], actualFiles);
			Assert.True(selectedFile.IsChecked);
			Assert.False(viewModel.IsProjectCopyExportInProgress);
			Assert.True(viewModel.CanChangeProjectTree);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task ActiveProjectExport_AllowsClosingExistingPreviewButBlocksReopening()
	{
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

		try
		{
			var viewModel = UiTestDriver.GetViewModel(window);
			await UiTestDriver.OpenPreviewAsync(window);
			await window.Dispatcher.InvokeAsync(() => viewModel.IsProjectCopyExportInProgress = true);

			Assert.True(viewModel.IsPreviewMode);
			Assert.True(viewModel.CanTogglePreview);

			await UiTestDriver.TogglePreviewViaToolbarAsync(window);
			await UiTestDriver.WaitForPreviewClosedAsync(window);

			var previewButton = UiTestDriver.GetRequiredTopMenuControl<Button>(window, "PreviewToggleButton");
			Assert.True(previewButton.IsEnabled);
			Assert.False(viewModel.IsPreviewMode);
			Assert.False(viewModel.CanTogglePreview);

			await UiTestDriver.TogglePreviewViaToolbarAsync(window);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

			Assert.False(viewModel.IsPreviewMode);
		}
		finally
		{
			await window.Dispatcher.InvokeAsync(() =>
				UiTestDriver.GetViewModel(window).IsProjectCopyExportInProgress = false);
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	private static async Task InvokeFolderExportAsync(MainWindow window, string destinationParent)
	{
		var export = typeof(MainWindow).GetMethod(
			"ExportProjectCopyAsync",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(export);

		var operation = await window.Dispatcher.InvokeAsync<Task>(() =>
			Assert.IsAssignableFrom<Task>(export.Invoke(
				window,
				[ProjectCopyExportFormat.Folder, destinationParent])));
		await operation.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
	}

	private static HashSet<string> CollectRelativeFilePaths(TreeNodeDescriptor root)
	{
		var files = new HashSet<string>(PathComparer.Default);
		var pending = new Stack<TreeNodeDescriptor>();
		pending.Push(root);
		while (pending.TryPop(out var node))
		{
			if (!node.IsDirectory)
			{
				files.Add(NormalizeRelativePath(Path.GetRelativePath(root.FullPath, node.FullPath)));
				continue;
			}

			foreach (var child in node.Children)
				pending.Push(child);
		}

		return files;
	}

	private static string NormalizeRelativePath(string path) =>
		path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

	private sealed class RecordingToastService : IToastService
	{
		public ObservableCollection<ToastMessageViewModel> Items { get; } = [];

		public void Show(string message) => Items.Add(new ToastMessageViewModel(message));

		public void Show(string message, TimeSpan duration) => Show(message);
	}
}
