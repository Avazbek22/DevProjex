namespace DevProjex.Tests.Integration;

public sealed class SelectionRefreshExtensionAvailabilityIntegrationTests
{
    private const string EmptyRootExtension = ".1770912967589";
    private const string EmptyNestedExtension = ".1770912967590";
    private const string DotFileExtension = ".1770912967591";
    private const string VisibleNumericExtension = ".1770912967592";
    private const string HiddenFileExtension = ".1770912967597";

    [Fact]
    public void FullRefresh_AllFileIgnoreRulesOn_PublishesOnlyExtensionsThatCanProduceTreeFiles()
    {
        using var temp = CreateNumericExtensionWorkspace();
        var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
        var context = ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path) with
        {
            CaptureTreeInventory = true
        };

        var snapshot = services.Engine.ComputeFullRefreshSnapshot(
            context,
            TestContext.Current.CancellationToken);

        var extensions = CollectExtensionNames(snapshot);
        Assert.Contains(".cs", extensions);
        Assert.Contains(VisibleNumericExtension, extensions);
        Assert.DoesNotContain(EmptyRootExtension, extensions);
        Assert.DoesNotContain(EmptyNestedExtension, extensions);
        Assert.DoesNotContain(DotFileExtension, extensions);
        Assert.True(snapshot.IgnoreOptionCounts.EmptyFiles >= 2);
        Assert.True(snapshot.IgnoreOptionCounts.DotFiles >= 1);
        Assert.NotNull(snapshot.TreeInventory);

        AssertPublishedExtensionsMatchProjectedTree(temp.Path, snapshot, services.IgnoreRulesService);
    }

    [Fact]
    public void LiveRefresh_DisablingOwningIgnoreRule_RevealsNumericExtensionsAndReenablingRemovesThem()
    {
        using var temp = CreateNumericExtensionWorkspace();
        var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
        var baseline = services.Engine.ComputeFullRefreshSnapshot(
            ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path),
            TestContext.Current.CancellationToken);
        var selectedRoots = CollectCheckedRootNames(baseline);

        var emptyFilesOff = ComputeLiveSnapshot(
            temp.Path,
            baseline,
            services,
            selectedRoots,
            IgnoreOptionId.EmptyFiles,
            isChecked: false);
        Assert.Contains(EmptyRootExtension, CollectExtensionNames(emptyFilesOff));
        Assert.Contains(EmptyNestedExtension, CollectExtensionNames(emptyFilesOff));
        Assert.DoesNotContain(DotFileExtension, CollectExtensionNames(emptyFilesOff));

        var dotFilesOff = ComputeLiveSnapshot(
            temp.Path,
            baseline,
            services,
            selectedRoots,
            IgnoreOptionId.DotFiles,
            isChecked: false);
        Assert.Contains(DotFileExtension, CollectExtensionNames(dotFilesOff));
        Assert.DoesNotContain(EmptyRootExtension, CollectExtensionNames(dotFilesOff));

        var emptyFilesRestored = ComputeLiveSnapshot(
            temp.Path,
            emptyFilesOff,
            services,
            selectedRoots,
            IgnoreOptionId.EmptyFiles,
            isChecked: true);
        Assert.DoesNotContain(EmptyRootExtension, CollectExtensionNames(emptyFilesRestored));
        Assert.DoesNotContain(EmptyNestedExtension, CollectExtensionNames(emptyFilesRestored));
        Assert.Contains(VisibleNumericExtension, CollectExtensionNames(emptyFilesRestored));
    }

    [Fact]
    public void FullRefresh_VisibleAndIgnoredFilesShareNumericExtension_KeepsExtensionAvailable()
    {
        using var temp = new TemporaryDirectory();
        temp.CreateFile("App.csproj", "<Project />");
        temp.CreateFile("src/visible.1770912967589", "stable payload");
        temp.CreateFile("src/empty.1770912967589", string.Empty);

        var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
        var snapshot = services.Engine.ComputeFullRefreshSnapshot(
            ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path),
            TestContext.Current.CancellationToken);

        Assert.Contains(EmptyRootExtension, CollectExtensionNames(snapshot));
        Assert.True(snapshot.IgnoreOptionCounts.EmptyFiles >= 1);
    }

    [Fact]
    public void FullRefresh_GitAndSmartIgnoredNumericExtensions_DoNotLeakIntoAvailability()
    {
        using var temp = new TemporaryDirectory();
        temp.CreateFile(".gitignore", "git-noise/\n");
        temp.CreateFile("App.csproj", "<Project />");
        temp.CreateFile("src/App.cs", "class App {}");
        temp.CreateFile("src/archive.1770912967594", "visible numeric extension");
        temp.CreateFile("git-noise/cache.1770912967595", "git ignored payload");
        temp.CreateFile("obj/project.assets.json", "{}");
        temp.CreateFile("obj/cache.1770912967596", "smart ignored payload");

        var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
        var context = ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path);
        var snapshot = services.Engine.ComputeFullRefreshSnapshot(
            context,
            TestContext.Current.CancellationToken);
        var repeated = services.Engine.ComputeFullRefreshSnapshot(
            context,
            TestContext.Current.CancellationToken);

        var extensions = CollectExtensionNames(snapshot);
        Assert.Contains(".1770912967594", extensions);
        Assert.DoesNotContain(".1770912967595", extensions);
        Assert.DoesNotContain(".1770912967596", extensions);
        Assert.Equal(
            snapshot.EffectiveExtensionOptions.Select(static option => (option.Name, option.IsChecked)),
            repeated.EffectiveExtensionOptions.Select(static option => (option.Name, option.IsChecked)));
        Assert.Equal(snapshot.IgnoreOptionCounts, repeated.IgnoreOptionCounts);
        Assert.Equal(snapshot.ControllerImpactCounts, repeated.ControllerImpactCounts);
    }

    [Fact]
    public void FullRefresh_WindowsHiddenNumericExtension_AppearsOnlyWhenHiddenFilesIsDisabled()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TemporaryDirectory();
        temp.CreateFile("App.csproj", "<Project />");
        temp.CreateFile("src/App.cs", "class App {}");
        var hiddenPath = temp.CreateFile($"src/native-hidden{HiddenFileExtension}", "hidden payload");
        File.SetAttributes(hiddenPath, File.GetAttributes(hiddenPath) | FileAttributes.Hidden);

        var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
        var baseline = services.Engine.ComputeFullRefreshSnapshot(
            ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path),
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(HiddenFileExtension, CollectExtensionNames(baseline));
        Assert.True(baseline.IgnoreOptionCounts.HiddenFiles >= 1);

        var hiddenFilesOff = ComputeLiveSnapshot(
            temp.Path,
            baseline,
            services,
            CollectCheckedRootNames(baseline),
            IgnoreOptionId.HiddenFiles,
            isChecked: false);
        Assert.Contains(HiddenFileExtension, CollectExtensionNames(hiddenFilesOff));
    }

    private static SelectionRefreshSnapshot ComputeLiveSnapshot(
        string rootPath,
        SelectionRefreshSnapshot previous,
        ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
        IReadOnlyCollection<string> selectedRoots,
        IgnoreOptionId changedOption,
        bool isChecked)
    {
        var context = ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, previous);
        var selectedIgnoreOptions = previous.IgnoreOptions
            .Where(static option => option.IsChecked)
            .Select(static option => option.Id)
            .ToHashSet();
        if (isChecked)
            selectedIgnoreOptions.Add(changedOption);
        else
            selectedIgnoreOptions.Remove(changedOption);

        var stateCache = new Dictionary<IgnoreOptionId, bool>(previous.IgnoreOptionStateCache)
        {
            [changedOption] = isChecked
        };
        context = context with
        {
            IgnoreSelectionInitialized = true,
            IgnoreSelectionCache = selectedIgnoreOptions,
            IgnoreOptionStateCache = stateCache,
            IgnoreAllPreference = null,
            IgnoreOptionStateCacheIsComplete = true
        };

        return services.Engine.ComputeLiveRefreshSnapshot(
            context,
            selectedRoots,
            TestContext.Current.CancellationToken);
    }

    private static void AssertPublishedExtensionsMatchProjectedTree(
        string rootPath,
        SelectionRefreshSnapshot snapshot,
        IgnoreRulesService ignoreRulesService)
    {
        var selectedRoots = CollectCheckedRootNames(snapshot);
        var selectedExtensions = CollectExtensionNames(snapshot);
        var selectedIgnoreOptions = snapshot.IgnoreOptions
            .Where(static option => option.IsChecked)
            .Select(static option => option.Id)
            .ToHashSet();
        var rules = ignoreRulesService.Build(rootPath, selectedIgnoreOptions, selectedRoots);
        var result = new TreeBuilder().Build(
            snapshot.TreeInventory!,
            new TreeFilterOptions(selectedExtensions, selectedRoots, rules),
            TestContext.Current.CancellationToken);
        var treeExtensions = EnumerateNodes(result.Root)
            .Where(static node => !node.IsDirectory)
            .Select(static node => Path.GetExtension(node.Name))
            .Where(static extension => !string.IsNullOrWhiteSpace(extension))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.True(
            selectedExtensions.SetEquals(treeExtensions),
            $"Published=[{string.Join(", ", selectedExtensions.Order())}], " +
            $"Tree=[{string.Join(", ", treeExtensions.Order())}]");
    }

    private static IEnumerable<FileSystemNode> EnumerateNodes(FileSystemNode root)
    {
        var pending = new Stack<FileSystemNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;

            for (var index = current.Children.Count - 1; index >= 0; index--)
                pending.Push(current.Children[index]);
        }
    }

    private static HashSet<string> CollectExtensionNames(SelectionRefreshSnapshot snapshot) =>
        snapshot.EffectiveExtensionOptions
            .Select(static option => option.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> CollectCheckedRootNames(SelectionRefreshSnapshot snapshot) =>
        snapshot.RootOptions is null
            ? new HashSet<string>(PathComparer.Default)
            : snapshot.RootOptions
                .Where(static option => option.IsChecked)
                .Select(static option => option.Name)
                .ToHashSet(PathComparer.Default);

    private static TemporaryDirectory CreateNumericExtensionWorkspace()
    {
        var temp = new TemporaryDirectory();
        temp.CreateFile("App.csproj", "<Project />");
        temp.CreateFile("src/App.cs", "class App {}");
        temp.CreateFile($"empty-root{EmptyRootExtension}", string.Empty);
        temp.CreateFile($"src/generated/empty-nested{EmptyNestedExtension}", string.Empty);
        temp.CreateFile($"src/.transient{DotFileExtension}", "dot-file payload");
        temp.CreateFile($"src/archive{VisibleNumericExtension}", "legitimate numeric extension");
        return temp;
    }
}
