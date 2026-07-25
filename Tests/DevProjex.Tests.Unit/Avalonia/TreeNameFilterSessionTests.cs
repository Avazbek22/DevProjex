namespace DevProjex.Tests.Unit.Avalonia;

public sealed class TreeNameFilterSessionTests
{
    [Fact]
    public void Build_ParallelRootProjectionPreservesOrderAndMatchingAncestors()
    {
        var baseTree = CreateWideTree(rootCount: 12, filesPerRoot: 8);
        var session = new TreeNameFilterSession();

        var result = session.Build(baseTree, "TARGET-05", TestContext.Current.CancellationToken);

        var rootFolder = Assert.Single(result.Root.Children);
        Assert.Equal("group-05", rootFolder.DisplayName);
        var match = Assert.Single(rootFolder.Children);
        Assert.Equal("target-05.txt", match.DisplayName);
        Assert.Equal(12, baseTree.Root.Children.Count);
        Assert.All(baseTree.Root.Children, folder => Assert.Equal(9, folder.Children.Count));
    }

    [Fact]
    public void Build_IncrementalPrefixMatchesColdProjection()
    {
        var baseTree = CreateWideTree(rootCount: 16, filesPerRoot: 12);
        var incremental = new TreeNameFilterSession();
        var cold = new TreeNameFilterSession();

        _ = incremental.Build(baseTree, "tar", TestContext.Current.CancellationToken);
        var incrementalResult = incremental.Build(
            baseTree,
            "target-11",
            TestContext.Current.CancellationToken);
        var coldResult = cold.Build(
            baseTree,
            "target-11",
            TestContext.Current.CancellationToken);

        Assert.Equal(
            Flatten(coldResult.Root),
            Flatten(incrementalResult.Root));
    }

    [Fact]
    public void Build_ReusesExactQueryButInvalidatesCacheForNewBaseTree()
    {
        var session = new TreeNameFilterSession();
        var firstTree = CreateTree("first-root", "alpha.txt");
        var secondTree = CreateTree("second-root", "alpha.txt");

        var first = session.Build(firstTree, "alpha", TestContext.Current.CancellationToken);
        var cached = session.Build(firstTree, "ALPHA", TestContext.Current.CancellationToken);
        var second = session.Build(secondTree, "alpha", TestContext.Current.CancellationToken);

        Assert.Same(first.Root, cached.Root);
        Assert.NotSame(first.Root, second.Root);
        Assert.Equal("second-root", second.Root.DisplayName);
    }

    [Fact]
    public void Build_EmptyQueryReturnsOriginalTreeAndClearsPreviousProjection()
    {
        var baseTree = CreateTree("root", "alpha.txt", "beta.txt");
        var session = new TreeNameFilterSession();
        _ = session.Build(baseTree, "alpha", TestContext.Current.CancellationToken);

        var result = session.Build(baseTree, "  ", TestContext.Current.CancellationToken);
        var projectedAgain = session.Build(baseTree, "alpha", TestContext.Current.CancellationToken);

        Assert.Same(baseTree, result);
        Assert.NotSame(baseTree.Root, projectedAgain.Root);
        Assert.Single(projectedAgain.Root.Children);
    }

    [Fact]
    public void Build_PreCanceledOperationDoesNotPublishOrMutateProjection()
    {
        var baseTree = CreateWideTree(rootCount: 12, filesPerRoot: 16);
        var session = new TreeNameFilterSession();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            session.Build(baseTree, "target", cancellation.Token));

        var result = session.Build(baseTree, null, TestContext.Current.CancellationToken);
        Assert.Same(baseTree, result);
    }

    [Fact]
    public async Task Build_ConcurrentSupersedingQueriesRemainDeterministic()
    {
        var baseTree = CreateWideTree(rootCount: 24, filesPerRoot: 24);
        var session = new TreeNameFilterSession();
        var queries = Enumerable.Range(0, 24)
            .Select(index => $"target-{index:D2}")
            .ToArray();

        var results = await Task.WhenAll(queries.Select(query =>
            Task.Run(
                () => session.Build(baseTree, query, TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken)));

        for (var index = 0; index < results.Length; index++)
        {
            var folder = Assert.Single(results[index].Root.Children);
            Assert.Equal($"group-{index:D2}", folder.DisplayName);
            Assert.Equal($"target-{index:D2}.txt", Assert.Single(folder.Children).DisplayName);
        }
    }

    private static BuildTreeResult CreateWideTree(int rootCount, int filesPerRoot)
    {
        var folders = new TreeNodeDescriptor[rootCount];
        for (var rootIndex = 0; rootIndex < rootCount; rootIndex++)
        {
            var children = new List<TreeNodeDescriptor>(filesPerRoot + 1);
            for (var fileIndex = 0; fileIndex < filesPerRoot; fileIndex++)
                children.Add(File($"ordinary-{rootIndex:D2}-{fileIndex:D2}.txt"));

            children.Add(File($"target-{rootIndex:D2}.txt"));
            folders[rootIndex] = Directory($"group-{rootIndex:D2}", children);
        }

        return new BuildTreeResult(
            Directory("root", folders),
            RootAccessDenied: false,
            HadAccessDenied: false);
    }

    private static BuildTreeResult CreateTree(string rootName, params string[] files) =>
        new(
            Directory(rootName, files.Select(File).ToArray()),
            RootAccessDenied: false,
            HadAccessDenied: false);

    private static TreeNodeDescriptor Directory(
        string name,
        IReadOnlyList<TreeNodeDescriptor> children) =>
        new(name, CreatePath(name), IsDirectory: true, IsAccessDenied: false, "folder", children);

    private static TreeNodeDescriptor File(string name) =>
        new(name, CreatePath(name), IsDirectory: false, IsAccessDenied: false, "file", []);

    private static string CreatePath(string name) =>
        Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory) ?? "/", "filter-tests", name);

    private static IReadOnlyList<string> Flatten(TreeNodeDescriptor root)
    {
        var result = new List<string>();
        var stack = new Stack<(TreeNodeDescriptor Node, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            result.Add($"{depth}:{node.IsDirectory}:{node.DisplayName}");
            for (var index = node.Children.Count - 1; index >= 0; index--)
                stack.Push((node.Children[index], depth + 1));
        }

        return result;
    }
}
