using System.Reflection;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Kernel.Contracts;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class PreviewFileProjectionCancellationUiTests(UiWorkspaceFixture workspace)
{
	[AvaloniaTheory]
	[InlineData(false, PreviewContentMode.Content)]
	[InlineData(true, PreviewContentMode.Content)]
	[InlineData(false, PreviewContentMode.TreeAndContent)]
	[InlineData(true, PreviewContentMode.TreeAndContent)]
	public async Task CanceledPreviewStopsCollectingFilePaths(
		bool hasSelection,
		PreviewContentMode mode)
	{
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);
		try
		{
			var snapshotMethod = typeof(MainWindow).GetMethod(
				"CaptureProjectTextOutputSnapshot",
				BindingFlags.Instance | BindingFlags.NonPublic);
			var controllerField = typeof(MainWindow).GetField(
				"_previewSurfaceController",
				BindingFlags.Instance | BindingFlags.NonPublic);
			Assert.NotNull(snapshotMethod);
			Assert.NotNull(controllerField);
			var snapshot = Assert.IsType<ProjectTextOutputSnapshot>(snapshotMethod.Invoke(window, null));
			var controller = Assert.IsType<PreviewSurfaceController>(controllerField.GetValue(window));
			using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
				TestContext.Current.CancellationToken);
			var filePath = Path.Combine(snapshot.RootPath, "canceled-projection.txt");
			var file = new TreeNodeDescriptor("canceled-projection.txt", filePath, false, false, "file", []);
			var children = new CancelOnFirstReadList(file, cancellation);
			var root = snapshot.Root with { Children = children };
			var selectedPaths = new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer);
			if (hasSelection)
				selectedPaths.Add(filePath);

			Assert.ThrowsAny<OperationCanceledException>(() =>
			{
				var preview = controller.BuildDocument(
					mode,
					selectedPaths,
					hasSelection,
					snapshot.TreeFormat,
					"No checked files",
					"No text content",
					"No data",
					snapshot.RootPath,
					root,
					currentTreeOrderedFilePaths: null,
					snapshot.PathPresentation,
					cancellation.Token);
				preview.Document.Dispose();
			});

			Assert.Equal(1, children.ReadCount);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	private sealed class CancelOnFirstReadList(
		TreeNodeDescriptor child,
		CancellationTokenSource cancellation) : IReadOnlyList<TreeNodeDescriptor>
	{
		public int Count => 10_000;
		public int ReadCount { get; private set; }

		public TreeNodeDescriptor this[int index]
		{
			get
			{
				if (++ReadCount == 1)
					cancellation.Cancel();
				return child;
			}
		}

		public IEnumerator<TreeNodeDescriptor> GetEnumerator()
		{
			for (var index = 0; index < Count; index++)
				yield return this[index];
		}

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
	}
}
