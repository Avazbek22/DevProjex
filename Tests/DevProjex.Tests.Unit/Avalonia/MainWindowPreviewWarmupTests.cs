using DevProjex.Avalonia.Services;
using DevProjex.Application.Compression;
using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class MainWindowPreviewWarmupTests
{
    [Fact]
    public void SupportsTransformationContext_CompressionOnlyAllowsWarmup()
    {
        using var compressionSession = new CodeCompressionSession(new NoOpCodeCompressor());
        var context = ContentTransformationContext.For(
            new CodeCompressionContext("project", compressionSession),
            redaction: null);

        Assert.True(PreviewWarmupPolicy.SupportsTransformationContext(context));
    }

    [Fact]
    public void SupportsTransformationContext_SecretRedactionSuppressesWarmup()
    {
        using var redactionSession = new SecretRedactionSession(new EmptySecretDetector());
        var context = ContentTransformationContext.For(
            compression: null,
            new SecretRedactionContext("project", redactionSession));

        Assert.False(PreviewWarmupPolicy.SupportsTransformationContext(context));
    }

    [Fact]
    public void CreateSelectionPlan_CheckedRootUsesImplicitFullTreePlan()
    {
        var root = CreateFlatRoot(
            CreatePath("root"),
            CreatePath("root", "a.txt"));

        var plan = PreviewWarmupPolicy.CreateSelectionPlan(
            root,
            new HashSet<string>(PathComparer.Default) { root.FullPath });

        Assert.NotNull(plan);
        Assert.False(plan.HasExplicitSelection);
        Assert.NotNull(plan.SelectedRoot);
        Assert.True(plan.SelectedRoot.IncludesWholeSubtree);
    }

    private sealed class NoOpCodeCompressor : ICodeCompressor
    {
        public string TransformIdentity => "no-op:v1";

        public bool IsSupported(string relativePath) => false;

        public ICodeCompressionScope CreateScope(string projectRoot) => new Scope();

        private sealed class Scope : ICodeCompressionScope
        {
            public CodeCompressionAnalysis Analyze(
                string fullPath,
                string relativePath,
                string content,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("No file should be analyzed by this policy test.");

            public void Dispose()
            {
            }
        }
    }

    private sealed class EmptySecretDetector : ISecretDetector
    {
        public IReadOnlyList<DetectedSecret> Detect(
            string repositoryRelativePath,
            string content,
            CancellationToken cancellationToken = default) => [];
    }

    [Fact]
    public void CountSelectedFilesUpToLimit_IgnoresMissingFilesAndStopsAtLimit()
    {
        using var temp = new TemporaryDirectory();
        var first = temp.CreateFile("a.txt", "a");
        var second = temp.CreateFile("b.txt", "b");
        var third = temp.CreateFile("c.txt", "c");
        var missing = Path.Combine(temp.Path, "missing.txt");
        var treeRoot = CreateFlatRoot(temp.Path, first, second, third, missing);

        var result = PreviewWarmupPolicy.CountSelectedFilesUpToLimit(
            new HashSet<string>(PathComparer.Default) { missing, third, first, second },
            treeRoot,
            2);

        Assert.Equal(2, result);
    }

    [Fact]
    public void CountSelectedFilesUpToLimit_DirectorySelection_ExpandsDescendants()
    {
        using var temp = new TemporaryDirectory();
        var srcPath = temp.CreateFolder("src");
        var nestedPath = Path.Combine(srcPath, "nested");
        Directory.CreateDirectory(nestedPath);
        var first = temp.CreateFile(Path.Combine("src", "a.txt"), "a");
        var second = temp.CreateFile(Path.Combine("src", "nested", "b.txt"), "b");
        var third = temp.CreateFile("readme.md", "docs");

        var treeRoot = new TreeNodeDescriptor(
            DisplayName: "root",
            FullPath: temp.Path,
            IsDirectory: true,
            IsAccessDenied: false,
            IconKey: "folder",
            Children:
            [
                new TreeNodeDescriptor(
                    "src",
                    srcPath,
                    true,
                    false,
                    "folder",
                    [
                        new TreeNodeDescriptor("a.txt", first, false, false, "file", []),
                        new TreeNodeDescriptor(
                            "nested",
                            nestedPath,
                            true,
                            false,
                            "folder",
                            [
                                new TreeNodeDescriptor("b.txt", second, false, false, "file", [])
                            ])
                    ]),
                new TreeNodeDescriptor("readme.md", third, false, false, "file", [])
            ]);

        var result = PreviewWarmupPolicy.CountSelectedFilesUpToLimit(
            new HashSet<string>(PathComparer.Default) { srcPath },
            treeRoot,
            10);

        Assert.Equal(2, result);
    }

    [Fact]
    public void CountTreeFilesUpToLimit_CountsLeafFilesOnly()
    {
        var treeRoot = new TreeNodeDescriptor(
            DisplayName: "root",
            FullPath: CreatePath("root"),
            IsDirectory: true,
            IsAccessDenied: false,
            IconKey: "folder",
            Children:
            [
                new TreeNodeDescriptor(
                    DisplayName: "src",
                    FullPath: CreatePath("root", "src"),
                    IsDirectory: true,
                    IsAccessDenied: false,
                    IconKey: "folder",
                    Children:
                    [
                        CreateFileDescriptor("one.cs"),
                        CreateFileDescriptor("two.cs")
                    ]),
                CreateFileDescriptor("readme.md")
            ]);

        var result = PreviewWarmupPolicy.CountTreeFilesUpToLimit(treeRoot, 2);

        Assert.Equal(2, result);
    }

    [Fact]
    public void CollectInitialPreviewFiles_FromSelection_DedupesSortsAndFiltersMissing()
    {
        using var temp = new TemporaryDirectory();
        var alpha = temp.CreateFile("alpha.txt", "alpha");
        var beta = temp.CreateFile("beta.txt", "beta");
        var missing = Path.Combine(temp.Path, "missing.txt");

        var files = PreviewWarmupPolicy.CollectInitialPreviewFiles(
            new HashSet<string>(PathComparer.Default) { beta, missing, alpha, beta },
            true,
            CreateFlatRoot(temp.Path, alpha, beta, missing),
            10);

        Assert.Equal(
            new[] { alpha, beta }.OrderBy(static path => path, PathComparer.Default),
            files);
    }

    [Fact]
    public void CollectInitialPreviewFiles_FromTree_ReturnsOrderedUniqueFilesUpToLimit()
    {
        using var temp = new TemporaryDirectory();
        var zeta = temp.CreateFile("zeta.txt", "z");
        var alpha = temp.CreateFile("alpha.txt", "a");
        var beta = temp.CreateFile("beta.txt", "b");
        var missing = Path.Combine(temp.Path, "missing.txt");

        var treeRoot = new TreeNodeDescriptor(
            DisplayName: "root",
            FullPath: temp.Path,
            IsDirectory: true,
            IsAccessDenied: false,
            IconKey: "folder",
            Children:
            [
                new TreeNodeDescriptor("group", Path.Combine(temp.Path, "group"), true, false, "folder",
                [
                    new TreeNodeDescriptor("alpha.txt", alpha, false, false, "file", []),
                    new TreeNodeDescriptor("missing.txt", missing, false, false, "file", []),
                    new TreeNodeDescriptor("beta.txt", beta, false, false, "file", [])
                ]),
                new TreeNodeDescriptor("zeta.txt", zeta, false, false, "file", [])
            ]);

        var files = PreviewWarmupPolicy.CollectInitialPreviewFiles(
            new HashSet<string>(PathComparer.Default),
            false,
            treeRoot,
            2);

        Assert.Equal(
            new[] { alpha, beta }.OrderBy(static path => path, PathComparer.Default),
            files);
    }

    [Fact]
    public void ShouldBuildPreviewWarmup_TreeModeReturnsTrueWhenTreeExists()
    {
        using var temp = new TemporaryDirectory();
        var selectedPaths = Enumerable.Range(0, 150)
            .Select(index => temp.CreateFile($"file{index:000}.txt", "x"))
            .ToHashSet(PathComparer.Default);

        var result = PreviewWarmupPolicy.ShouldBuildPreviewWarmup(
            PreviewContentMode.Tree,
            true,
            selectedPaths,
            CreateFlatRoot(temp.Path, selectedPaths.ToArray()));

        Assert.True(result);
    }

    [Fact]
    public void ShouldBuildPreviewWarmup_ContentModeReturnsTrueWhenTreeExists()
    {
        using var temp = new TemporaryDirectory();
        var selectedPaths = Enumerable.Range(0, 140)
            .Select(index => temp.CreateFile($"file{index:000}.txt", "x"))
            .ToHashSet(PathComparer.Default);

        var result = PreviewWarmupPolicy.ShouldBuildPreviewWarmup(
            PreviewContentMode.Content,
            true,
            selectedPaths,
            CreateFlatRoot(temp.Path, selectedPaths.ToArray()));

        Assert.True(result);
    }

    [Fact]
    public void ShouldBuildPreviewWarmup_ContentModeDoesNotDelaySmallSelection()
    {
        using var temp = new TemporaryDirectory();
        var selectedPaths = Enumerable.Range(0, 12)
            .Select(index => temp.CreateFile($"file{index:000}.txt", "x"))
            .ToHashSet(PathComparer.Default);

        var result = PreviewWarmupPolicy.ShouldBuildPreviewWarmup(
            PreviewContentMode.Content,
            true,
            selectedPaths,
            CreateFlatRoot(temp.Path, selectedPaths.ToArray()));

        Assert.True(result);
    }

    [Fact]
    public void ShouldBuildPreviewWarmup_WithoutTreeReturnsFalse()
    {
        var result = PreviewWarmupPolicy.ShouldBuildPreviewWarmup(
            PreviewContentMode.Tree,
            false,
            new HashSet<string>(PathComparer.Default),
            treeRoot: null);

        Assert.False(result);
    }

    [Fact]
    public void CreateBoundedTreeProjection_LimitsNodesAndPreservesOrder()
    {
        var root = CreateNestedTree(depth: 12);

        var projection = PreviewWarmupPolicy.CreateBoundedTreeProjection(
            root,
            new HashSet<string>(PathComparer.Default),
            maxNodeCount: 7);

        Assert.NotNull(projection);
        Assert.Equal(7, CountNodes(projection!));
        Assert.Equal(
            Enumerable.Range(0, 7).Select(index => $"node-{index}"),
            EnumerateNodes(projection!).Select(static node => node.DisplayName));
    }

    [Fact]
    public void CreateBoundedTreeProjection_DeepTreeDoesNotDependOnTheCallStack()
    {
        const int depth = 16_000;
        var root = CreateNestedTree(depth);

        var projection = PreviewWarmupPolicy.CreateBoundedTreeProjection(
            root,
            new HashSet<string>(PathComparer.Default),
            maxNodeCount: depth);

        Assert.NotNull(projection);
        Assert.Equal(depth, CountNodes(projection!));
        Assert.Equal($"node-{depth - 1}", EnumerateNodes(projection!).Last().DisplayName);
    }

    [Fact]
    public void CreateBoundedTreeProjection_PartialSelectionIncludesOnlyRequiredBranch()
    {
        var rootPath = CreatePath("root");
        var selectedPath = CreatePath("root", "selected", "target.cs");
        var root = new TreeNodeDescriptor("root", rootPath, true, false, "folder",
        [
            new TreeNodeDescriptor("ignored", CreatePath("root", "ignored"), true, false, "folder",
            [
                CreateFileDescriptor("ignored.cs")
            ]),
            new TreeNodeDescriptor("selected", CreatePath("root", "selected"), true, false, "folder",
            [
                new TreeNodeDescriptor("target.cs", selectedPath, false, false, "file", []),
                new TreeNodeDescriptor(
                    "other.cs",
                    CreatePath("root", "selected", "other.cs"),
                    false,
                    false,
                    "file",
                    [])
            ])
        ]);

        var projection = PreviewWarmupPolicy.CreateBoundedTreeProjection(
            root,
            new HashSet<string>(PathComparer.Default) { selectedPath },
            maxNodeCount: 20);

        Assert.NotNull(projection);
        Assert.Equal(
            ["root", "selected", "target.cs"],
            EnumerateNodes(projection!).Select(static node => node.DisplayName));
    }

    [Fact]
    public void CreateBoundedTreeProjection_StaleSelectionFallsBackToWholeTree()
    {
        var root = CreateNestedTree(depth: 4);
        var stalePath = Path.Combine(root.FullPath, "missing.cs");

        var projection = PreviewWarmupPolicy.CreateBoundedTreeProjection(
            root,
            new HashSet<string>(PathComparer.Default) { stalePath },
            maxNodeCount: 3);

        Assert.NotNull(projection);
        Assert.Equal(
            ["node-0", "node-1", "node-2"],
            EnumerateNodes(projection!).Select(static node => node.DisplayName));
    }

    [Fact]
    public void CreateBoundedTreeProjection_RareSelectedLeafInVeryWideTreeUsesDirectLookup()
    {
        const int siblingCount = 32_768;
        var rootPath = CreatePath("wide-root");
        var targetPath = Path.Combine(rootPath, "zz-target.cs");
        var children = Enumerable.Range(0, siblingCount)
            .Select(index => new TreeNodeDescriptor(
                $"file-{index:00000}.cs",
                Path.Combine(rootPath, $"file-{index:00000}.cs"),
                false,
                false,
                "file",
                []))
            .Append(new TreeNodeDescriptor(
                "zz-target.cs",
                targetPath,
                false,
                false,
                "file",
                []))
            .ToArray();
        var observedChildren = new CountingReadOnlyList<TreeNodeDescriptor>(children);
        var root = new TreeNodeDescriptor(
            "wide-root",
            rootPath,
            true,
            false,
            "folder",
            observedChildren);

        var projection = PreviewWarmupPolicy.CreateBoundedTreeProjection(
            root,
            new HashSet<string>(PathComparer.Default) { targetPath },
            maxNodeCount: 4);

        Assert.NotNull(projection);
        Assert.Equal(
            ["wide-root", "zz-target.cs"],
            EnumerateNodes(projection!).Select(static node => node.DisplayName));
        Assert.InRange(observedChildren.AccessCount, 1, 96);
    }

    [Fact]
    public void CreateBoundedTreeProjection_DeepBroadSelectedPathScalesWithDepthNotSiblingCount()
    {
        const int depth = 96;
        const int siblingCount = 256;
        var observedChildLists = new List<CountingReadOnlyList<TreeNodeDescriptor>>();
        var (root, targetPath) = CreateDeepBroadTree(
            depth,
            siblingCount,
            observedChildLists);

        var projection = PreviewWarmupPolicy.CreateBoundedTreeProjection(
            root,
            new HashSet<string>(PathComparer.Default) { targetPath },
            maxNodeCount: depth + 1);

        Assert.NotNull(projection);
        Assert.Equal(depth + 1, CountNodes(projection!));
        Assert.InRange(
            observedChildLists.Sum(static children => children.AccessCount),
            depth,
            depth * 40);
    }

    [Fact]
    public void CreateBoundedTreeProjection_StalePathInsideVeryWideRootFallsBackWithinBudget()
    {
        const int siblingCount = 32_768;
        var rootPath = CreatePath("stale-wide-root");
        var children = Enumerable.Range(0, siblingCount)
            .Select(index => new TreeNodeDescriptor(
                $"directory-{index:00000}",
                Path.Combine(rootPath, $"directory-{index:00000}"),
                true,
                false,
                "folder",
                []))
            .ToArray();
        var observedChildren = new CountingReadOnlyList<TreeNodeDescriptor>(children);
        var root = new TreeNodeDescriptor(
            "stale-wide-root",
            rootPath,
            true,
            false,
            "folder",
            observedChildren);

        var projection = PreviewWarmupPolicy.CreateBoundedTreeProjection(
            root,
            new HashSet<string>(PathComparer.Default)
            {
                Path.Combine(rootPath, "zz-missing")
            },
            maxNodeCount: 4);

        Assert.NotNull(projection);
        Assert.Equal(
            ["stale-wide-root", "directory-00000", "directory-00001", "directory-00002"],
            EnumerateNodes(projection!).Select(static node => node.DisplayName));
        Assert.InRange(observedChildren.AccessCount, 3, 96);
    }

    [Fact]
    public void CollectInitialPreviewFiles_RareSelectedLeafInVeryWideTreeUsesDirectLookup()
    {
        const int siblingCount = 32_768;
        using var temp = new TemporaryDirectory();
        var targetPath = temp.CreateFile("zz-target.txt", "target");
        var children = Enumerable.Range(0, siblingCount)
            .Select(index => new TreeNodeDescriptor(
                $"file-{index:00000}.txt",
                Path.Combine(temp.Path, $"file-{index:00000}.txt"),
                false,
                false,
                "file",
                []))
            .Append(new TreeNodeDescriptor(
                "zz-target.txt",
                targetPath,
                false,
                false,
                "file",
                []))
            .ToArray();
        var observedChildren = new CountingReadOnlyList<TreeNodeDescriptor>(children);
        var root = new TreeNodeDescriptor(
            "root",
            temp.Path,
            true,
            false,
            "folder",
            observedChildren);
        var selectionPlan = PreviewWarmupPolicy.CreateSelectionPlan(
            root,
            new HashSet<string>(PathComparer.Default) { targetPath });

        var files = PreviewWarmupPolicy.CollectInitialPreviewFiles(
            selectionPlan,
            maxFileCount: 24,
            maxNodeVisitCount: 64);

        Assert.Equal([targetPath], files);
        Assert.InRange(observedChildren.AccessCount, 1, 96);
    }

    [Fact]
    public void CollectInitialPreviewFiles_WholeTreeTraversalHonorsNodeVisitBudget()
    {
        const int siblingCount = 10_000;
        var rootPath = CreatePath("bounded-file-warmup");
        var children = Enumerable.Range(0, siblingCount)
            .Select(index => new TreeNodeDescriptor(
                $"empty-{index:00000}",
                Path.Combine(rootPath, $"empty-{index:00000}"),
                true,
                false,
                "folder",
                []))
            .ToArray();
        var observedChildren = new CountingReadOnlyList<TreeNodeDescriptor>(children);
        var root = new TreeNodeDescriptor(
            "bounded-file-warmup",
            rootPath,
            true,
            false,
            "folder",
            observedChildren);
        var selectionPlan = PreviewWarmupPolicy.CreateSelectionPlan(
            root,
            new HashSet<string>(PathComparer.Default));

        var files = PreviewWarmupPolicy.CollectInitialPreviewFiles(
            selectionPlan,
            maxFileCount: 24,
            maxNodeVisitCount: 64);

        Assert.Empty(files);
        Assert.Equal(63, observedChildren.AccessCount);
    }

    [Fact]
    public void CollectInitialPreviewFiles_UsesOrderedSnapshotWithoutWalkingWholeTree()
    {
        const int siblingCount = 32_768;
        using var temp = new TemporaryDirectory();
        var firstFile = temp.CreateFile("first.txt", "first");
        var children = Enumerable.Range(0, siblingCount)
            .Select(index => new TreeNodeDescriptor(
                $"empty-{index:00000}",
                Path.Combine(temp.Path, $"empty-{index:00000}"),
                true,
                false,
                "folder",
                []))
            .ToArray();
        var observedChildren = new CountingReadOnlyList<TreeNodeDescriptor>(children);
        var root = new TreeNodeDescriptor(
            "root",
            temp.Path,
            true,
            false,
            "folder",
            observedChildren);
        var selectionPlan = PreviewWarmupPolicy.CreateSelectionPlan(
            root,
            new HashSet<string>(PathComparer.Default));

        var files = PreviewWarmupPolicy.CollectInitialPreviewFiles(
            selectionPlan,
            maxFileCount: 24,
            maxNodeVisitCount: 64,
            orderedFilePaths: [firstFile]);

        Assert.Equal([firstFile], files);
        Assert.Equal(0, observedChildren.AccessCount);
    }

    private static TreeNodeDescriptor CreateFlatRoot(string rootPath, params string[] filePaths)
    {
        var children = filePaths
            .Select(path => new TreeNodeDescriptor(
                Path.GetFileName(path),
                path,
                false,
                false,
                "file",
                []))
            .ToList();

        return new TreeNodeDescriptor(
            DisplayName: Path.GetFileName(rootPath),
            FullPath: rootPath,
            IsDirectory: true,
            IsAccessDenied: false,
            IconKey: "folder",
            Children: children);
    }

    private static TreeNodeDescriptor CreateFileDescriptor(string name)
    {
        var path = CreatePath("root", name);
        return new TreeNodeDescriptor(name, path, false, false, "file", []);
    }

    private static TreeNodeDescriptor CreateNestedTree(int depth)
    {
        TreeNodeDescriptor? child = null;
        for (var index = depth - 1; index >= 0; index--)
        {
            child = new TreeNodeDescriptor(
                $"node-{index}",
                CreatePath("root", $"node-{index}"),
                true,
                false,
                "folder",
                child is null ? [] : [child]);
        }

        return child!;
    }

    private static (TreeNodeDescriptor Root, string TargetPath) CreateDeepBroadTree(
        int depth,
        int siblingCount,
        List<CountingReadOnlyList<TreeNodeDescriptor>> observedChildLists)
    {
        var rootPath = CreatePath("deep-broad-root");
        var (selectedChild, targetPath) = CreateDeepBroadSelectedBranch(
            rootPath,
            level: 0,
            depth,
            siblingCount,
            observedChildLists);
        var rootChildren = CreateBroadLevelChildren(
            rootPath,
            selectedChild,
            siblingCount);
        var observedRootChildren =
            new CountingReadOnlyList<TreeNodeDescriptor>(rootChildren);
        observedChildLists.Add(observedRootChildren);
        return (
            new TreeNodeDescriptor(
                "deep-broad-root",
                rootPath,
                true,
                false,
                "folder",
                observedRootChildren),
            targetPath);
    }

    private static (TreeNodeDescriptor Node, string TargetPath)
        CreateDeepBroadSelectedBranch(
            string parentPath,
            int level,
            int depth,
            int siblingCount,
            List<CountingReadOnlyList<TreeNodeDescriptor>> observedChildLists)
    {
        var selectedName = $"z{level:000}";
        var selectedPath = Path.Combine(parentPath, selectedName);
        if (level == depth - 1)
        {
            return (
                new TreeNodeDescriptor(
                    selectedName,
                    selectedPath,
                    false,
                    false,
                    "file",
                    []),
                selectedPath);
        }

        var (selectedChild, targetPath) =
            CreateDeepBroadSelectedBranch(
                selectedPath,
                level + 1,
                depth,
                siblingCount,
                observedChildLists);
        var children = CreateBroadLevelChildren(
            selectedPath,
            selectedChild,
            siblingCount);
        var observedChildren =
            new CountingReadOnlyList<TreeNodeDescriptor>(children);
        observedChildLists.Add(observedChildren);
        return (
            new TreeNodeDescriptor(
                selectedName,
                selectedPath,
                true,
                false,
                "folder",
                observedChildren),
            targetPath);
    }

    private static IReadOnlyList<TreeNodeDescriptor> CreateBroadLevelChildren(
        string parentPath,
        TreeNodeDescriptor selectedChild,
        int siblingCount)
    {
        var isDirectoryLevel = selectedChild.IsDirectory;
        return Enumerable.Range(0, siblingCount - 1)
            .Select(index => new TreeNodeDescriptor(
                $"a{index:000}",
                Path.Combine(parentPath, $"a{index:000}"),
                isDirectoryLevel,
                false,
                isDirectoryLevel ? "folder" : "file",
                []))
            .Append(selectedChild)
            .ToArray();
    }

    private static int CountNodes(TreeNodeDescriptor root) =>
        EnumerateNodes(root).Count();

    private static IEnumerable<TreeNodeDescriptor> EnumerateNodes(
        TreeNodeDescriptor root)
    {
        var stack = new Stack<TreeNodeDescriptor>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            for (var index = node.Children.Count - 1; index >= 0; index--)
                stack.Push(node.Children[index]);
        }
    }

    private static string CreatePath(params string[] segments)
    {
        return OperatingSystem.IsWindows()
            ? Path.Combine(["C:\\", ..segments])
            : Path.Combine(["/", ..segments]);
    }

    private sealed class CountingReadOnlyList<T>(
        IReadOnlyList<T> items) : IReadOnlyList<T>
    {
        public int AccessCount { get; private set; }

        public int Count => items.Count;

        public T this[int index]
        {
            get
            {
                AccessCount++;
                return items[index];
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            for (var index = 0; index < items.Count; index++)
            {
                AccessCount++;
                yield return items[index];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
