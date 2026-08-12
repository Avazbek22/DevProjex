using System.Runtime.CompilerServices;

namespace DevProjex.Tests.UI;

[Collection("AvaloniaUI")]
public sealed class MainWindowApplySettingsSelectionUiTests
{
    [AvaloniaFact]
    public async Task StructuralApply_PreservesManualSubsetWithoutRetainingOldTree()
    {
        using var project = UiTestProject.CreateWithDynamicIgnoreEntries();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
        try
        {
            await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
            var selectedPath = Path.Combine(project.RootPath, "src");
            var oldRoot = SelectOnlyPathAndCaptureRoot(window, selectedPath);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFiles);
            await UiTestDriver.ClickApplySettingsAsync(window);

            var selectedPaths = UiTestDriver.GetCheckedTreePaths(window);
            Assert.Single(selectedPaths);
            Assert.Contains(selectedPath, selectedPaths);
            await AssertEventuallyCollectedAsync(oldRoot);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task StructuralApply_DropsMissingSelectedPathsAndKeepsSurvivors()
    {
        using var project = UiTestProject.CreateWithDynamicIgnoreEntries();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
        try
        {
            await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFiles);
            await UiTestDriver.ClickApplySettingsAsync(window);

            var survivingPath = Path.Combine(project.RootPath, "README.md");
            var disappearingPath = Path.Combine(project.RootPath, "src", "empty.txt");
            SelectOnlyPaths(window, survivingPath, disappearingPath);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFiles);
            await UiTestDriver.ClickApplySettingsAsync(window);

            var selectedPaths = UiTestDriver.GetCheckedTreePaths(window);
            Assert.Single(selectedPaths);
            Assert.Contains(survivingPath, selectedPaths);
            Assert.DoesNotContain(disappearingPath, selectedPaths);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task StructuralApply_EmptySelectionKeepsSelectAllSemantics()
    {
        using var project = UiTestProject.CreateWithDynamicIgnoreEntries();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
        try
        {
            await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
            Assert.Empty(UiTestDriver.GetCheckedTreePaths(window));

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFiles);
            await UiTestDriver.ClickApplySettingsAsync(window);

            Assert.Empty(UiTestDriver.GetCheckedTreePaths(window));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SelectOnlyPathAndCaptureRoot(MainWindow window, string selectedPath)
    {
        var root = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
        root.IsChecked = false;
        var selectedNode = Assert.Single(
            root.Flatten(),
            node => PathComparer.Default.Equals(node.FullPath, selectedPath));
        selectedNode.IsChecked = true;
        return new WeakReference(root);
    }

    private static void SelectOnlyPaths(MainWindow window, params string[] selectedPaths)
    {
        var root = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
        root.IsChecked = false;
        var nodesByPath = root
            .Flatten()
            .ToDictionary(static node => node.FullPath, PathComparer.Default);
        foreach (var path in selectedPaths)
            nodesByPath[path].IsChecked = true;
    }

    private static async Task AssertEventuallyCollectedAsync(WeakReference reference)
    {
        for (var attempt = 0; attempt < 12 && reference.IsAlive; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
        }

        Assert.False(reference.IsAlive);
    }
}
