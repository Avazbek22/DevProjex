using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Reflection;
using Avalonia.VisualTree;
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
	public async Task PickerContinuation_AfterProjectSwitch_DoesNotExportAnotherProject()
	{
		using var nextProject = UiTestProject.CreateDefault();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);
		var originalTree = UiTestDriver.GetCurrentTreeIdentity(window);
		var destinationParent = Path.Combine(
			workspace.Project.AppDataPath,
			"project-copy-stale-picker",
			Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(destinationParent);

		try
		{
			await UiTestDriver.OpenFolderAsync(window, nextProject.RootPath);
			await InvokePickerContinuationAsync(
				window,
				destinationParent,
				workspace.Project.RootPath,
				originalTree);

			Assert.Empty(Directory.EnumerateFileSystemEntries(destinationParent));
			Assert.False(UiTestDriver.GetViewModel(window).IsProjectCopyExportInProgress);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task PickerContinuation_AfterWindowCloses_DoesNotStartExport()
	{
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);
		var originalTree = UiTestDriver.GetCurrentTreeIdentity(window);
		var destinationParent = Path.Combine(
			workspace.Project.AppDataPath,
			"project-copy-closed-picker",
			Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(destinationParent);

		await UiTestDriver.CloseWindowAsync(window);
		await InvokePickerContinuationAsync(
			window,
			destinationParent,
			workspace.Project.RootPath,
			originalTree);

		Assert.Empty(Directory.EnumerateFileSystemEntries(destinationParent));
	}

	[AvaloniaFact]
	public async Task ZipDestinationPolicyUsesExactNewPathAndRejectsExistingDirectory()
	{
		var toasts = new RecordingToastService();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			workspace.Project,
			configureServices: services => services with { ToastService = toasts });
		var destinationParent = Path.Combine(
			workspace.Project.AppDataPath,
			"project-copy-zip-policy",
			Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(destinationParent);

		try
		{
			var newZipPath = Path.Combine(destinationParent, "new.zip");
			Assert.Equal(
				ProjectCopyConflictPolicy.Fail,
				await InvokeZipConflictPolicyAsync(window, newZipPath));

			var existingDirectoryPath = Path.Combine(destinationParent, "directory.zip");
			Directory.CreateDirectory(existingDirectoryPath);
			Assert.Null(await InvokeZipConflictPolicyAsync(window, existingDirectoryPath));
			Assert.Single(toasts.Items);
			Assert.True(Directory.Exists(existingDirectoryPath));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task ExistingZipRequiresExplicitConfirmationBeforeReplacement()
	{
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);
		var destinationParent = Path.Combine(
			workspace.Project.AppDataPath,
			"project-copy-zip-replace",
			Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(destinationParent);
		var destinationPath = Path.Combine(destinationParent, "existing.zip");
		var originalBytes = "original archive"u8.ToArray();
		await File.WriteAllBytesAsync(destinationPath, originalBytes, TestContext.Current.CancellationToken);

		try
		{
			var canceledDecision = await BeginZipConflictPolicyAsync(window, destinationPath);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.OwnedWindows.Count == 1,
				"the ZIP replacement confirmation");
			Assert.False(canceledDecision.IsCompleted);
			var dialog = Assert.Single(window.OwnedWindows);
			var cancel = Assert.Single(
				dialog.GetVisualDescendants().OfType<Button>(),
				static button => Equals(button.Content, "Cancel"));
			await UiTestDriver.RaiseButtonClickAsync(cancel);
			Assert.Null(await canceledDecision.WaitAsync(
				TimeSpan.FromSeconds(30),
				TestContext.Current.CancellationToken));
			Assert.Equal(originalBytes, await File.ReadAllBytesAsync(
				destinationPath,
				TestContext.Current.CancellationToken));

			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.OwnedWindows.Count == 0,
				"the canceled ZIP confirmation to close");
			var approvedDecision = await BeginZipConflictPolicyAsync(window, destinationPath);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.OwnedWindows.Count == 1,
				"the ZIP replacement confirmation to reopen");
			Assert.False(approvedDecision.IsCompleted);
			dialog = Assert.Single(window.OwnedWindows);
			var overwrite = Assert.Single(
				dialog.GetVisualDescendants().OfType<Button>(),
				static button => Equals(button.Content, "Overwrite"));
			await UiTestDriver.RaiseButtonClickAsync(overwrite);
			var policy = Assert.IsType<ProjectCopyConflictPolicy>(
				await approvedDecision.WaitAsync(
					TimeSpan.FromSeconds(30),
					TestContext.Current.CancellationToken));
			Assert.Equal(ProjectCopyConflictPolicy.ReplaceAtomically, policy);

			await InvokeZipExportAsync(window, destinationPath, policy);
			using var archive = ZipFile.OpenRead(destinationPath);
			Assert.NotEmpty(archive.Entries);
		}
		finally
		{
			foreach (var dialog in window.OwnedWindows.ToArray())
				await UiTestDriver.CloseTopLevelWindowAsync(dialog);
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
		await InvokePickerContinuationAsync(
			window,
			destinationParent,
			Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes).FullPath,
			UiTestDriver.GetCurrentTreeIdentity(window));
	}

	private static async Task InvokePickerContinuationAsync(
		MainWindow window,
		string destinationParent,
		string expectedPath,
		object expectedTree)
	{
		var export = typeof(MainWindow).GetMethod(
			"ExportProjectCopyIfSourceCurrentAsync",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(export);

		var operation = await window.Dispatcher.InvokeAsync<Task>(() =>
			Assert.IsAssignableFrom<Task>(export.Invoke(
				window,
				[ProjectCopyExportFormat.Folder, destinationParent, expectedPath, expectedTree])));
		await operation.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
	}

	private static async Task<ProjectCopyConflictPolicy?> InvokeZipConflictPolicyAsync(
		MainWindow window,
		string destinationPath)
	{
		var operation = await BeginZipConflictPolicyAsync(window, destinationPath);
		return await operation.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
	}

	private static async Task<Task<ProjectCopyConflictPolicy?>> BeginZipConflictPolicyAsync(
		MainWindow window,
		string destinationPath)
	{
		var resolve = typeof(MainWindow).GetMethod(
			"ConfirmZipReplacementIfNeededAsync",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(resolve);
		return await window.Dispatcher.InvokeAsync<Task<ProjectCopyConflictPolicy?>>(() =>
			Assert.IsAssignableFrom<Task<ProjectCopyConflictPolicy?>>(
				resolve.Invoke(window, [destinationPath])));
	}

	private static async Task InvokeZipExportAsync(
		MainWindow window,
		string destinationPath,
		ProjectCopyConflictPolicy conflictPolicy)
	{
		var export = typeof(MainWindow).GetMethod(
			"ExportProjectCopyAsync",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(export);
		var operation = await window.Dispatcher.InvokeAsync<Task>(() =>
			Assert.IsAssignableFrom<Task>(export.Invoke(
				window,
				[
					ProjectCopyExportFormat.Zip,
					destinationPath,
					ProjectCopyDestinationMode.Exact,
					conflictPolicy
				])));
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
