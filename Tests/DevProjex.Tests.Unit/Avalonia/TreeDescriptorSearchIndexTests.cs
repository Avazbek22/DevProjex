namespace DevProjex.Tests.Unit.Avalonia;

public sealed class TreeDescriptorSearchIndexTests
{
    [Fact]
    public void Search_ExactAndIncrementalQueriesReturnStablePreOrderMatches()
    {
        var target = File("target-service.cs");
        var root = Directory(
            "root",
            Directory("src", target, File("ordinary.cs")),
            Directory("tests", File("target-service-tests.cs")));
        var session = new TreeDescriptorSearchSession();

        var broad = session.Search(root, "Project", "target", TestContext.Current.CancellationToken);
        var narrow = session.Search(root, "Project", "target-service", TestContext.Current.CancellationToken);
        var cached = session.Search(root, "Project", "TARGET-SERVICE", TestContext.Current.CancellationToken);

        Assert.Equal(2, broad.MatchIndices.Length);
        Assert.Equal(2, narrow.MatchIndices.Length);
        Assert.True(cached.UsedCache);
        Assert.Equal(narrow.MatchIndices, cached.MatchIndices);
        Assert.Equal(
            ["target-service.cs", "target-service-tests.cs"],
            narrow.MatchIndices.Select(index => narrow.Index[index].Descriptor.DisplayName));
    }

    [Fact]
    public void Search_NewRootInvalidatesCachedIndices()
    {
        var session = new TreeDescriptorSearchSession();
        var firstRoot = Directory("first", File("match-one.txt"));
        var secondRoot = Directory("second", File("match-two.txt"));

        var first = session.Search(firstRoot, "First", "match", TestContext.Current.CancellationToken);
        var second = session.Search(secondRoot, "Second", "match", TestContext.Current.CancellationToken);

        Assert.False(second.UsedCache);
        Assert.NotSame(first.Index, second.Index);
        var matchIndex = Assert.Single(second.MatchIndices);
        Assert.Equal("match-two.txt", second.Index[matchIndex].Descriptor.DisplayName);
    }

    [Fact]
    public void Search_PreCanceledRequestDoesNotPublishAnIndex()
    {
        var session = new TreeDescriptorSearchSession();
        var root = Directory("root", File("match.txt"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            session.Search(root, "Project", "match", cancellation.Token));

        var result = session.Search(root, "Project", "match", TestContext.Current.CancellationToken);
        Assert.False(result.UsedCache);
        Assert.Single(result.MatchIndices);
    }

    [Fact]
    public void Clear_DeterministicallyReleasesRootIndexMatchesAndQueryCache()
    {
        var session = new TreeDescriptorSearchSession();
        var root = Directory(
            "root",
            Directory("src", File("service-one.cs"), File("service-two.cs")));

        var result = session.Search(
            root,
            "Project",
            "service",
            TestContext.Current.CancellationToken);
        Assert.Equal(2, result.MatchIndices.Length);

        session.Clear();

        Assert.Null(GetPrivateFieldValue(session, "_root"));
        Assert.Null(GetPrivateFieldValue(session, "_index"));
        Assert.Null(GetPrivateFieldValue(session, "_lastQuery"));
        Assert.Empty(GetPrivateField<int[]>(session, "_lastMatches"));
        Assert.Empty(GetPrivateField<Dictionary<string, int[]>>(session, "_queryCache"));
        Assert.Empty(GetPrivateField<LinkedList<string>>(session, "_queryCacheLru"));
        Assert.Empty(GetPrivateField<Dictionary<string, LinkedListNode<string>>>(session, "_queryCacheNodes"));
    }

    [Fact]
    public void AncestorExpansionBudget_CountsWideSiblingCollectionsOnlyOnce()
    {
        var children = Enumerable.Range(0, 3_000)
            .Select(index => File(
                index < 360
                    ? $"match-{index:D4}.txt"
                    : $"other-{index:D4}.txt"))
            .ToArray();
        var root = Directory("root", children);
        var result = new TreeDescriptorSearchSession().Search(
            root,
            "Project",
            "match-",
            TestContext.Current.CancellationToken);

        Assert.Equal(360, result.MatchIndices.Length);
        Assert.False(result.Index.IsAncestorExpansionWithinBudget(
            result.MatchIndices,
            TreeSearchCoordinator.MaximumAutoExpandedItemCount));
        Assert.True(result.Index.IsAncestorExpansionWithinBudget(
            result.MatchIndices,
            children.Length));
    }

    [Fact]
    public void RepeatedSearchCycles_ReuseOneIndexAndKeepEveryQueryCacheStructureBounded()
    {
        const int cacheLimit = 8;
        var files = Enumerable.Range(0, 48)
            .Select(index => File($"service-{index:D2}.cs"))
            .ToArray();
        var root = Directory("root", Directory("src", files));
        var session = new TreeDescriptorSearchSession();

        for (var cycle = 0; cycle < 6; cycle++)
        {
            TreeDescriptorSearchIndex? cycleIndex = null;
            for (var queryIndex = 0; queryIndex < files.Length; queryIndex++)
            {
                var result = session.Search(
                    root,
                    "Project",
                    queryIndex.ToString("D2", CultureInfo.InvariantCulture),
                    TestContext.Current.CancellationToken);

                cycleIndex ??= result.Index;
                Assert.Same(cycleIndex, result.Index);
                Assert.InRange(
                    GetPrivateField<Dictionary<string, int[]>>(session, "_queryCache").Count,
                    0,
                    cacheLimit);
                Assert.InRange(
                    GetPrivateField<LinkedList<string>>(session, "_queryCacheLru").Count,
                    0,
                    cacheLimit);
                Assert.InRange(
                    GetPrivateField<Dictionary<string, LinkedListNode<string>>>(session, "_queryCacheNodes").Count,
                    0,
                    cacheLimit);
            }

            Assert.Same(cycleIndex, GetPrivateField<TreeDescriptorSearchIndex>(session, "_index"));
            Assert.Equal(cacheLimit, GetPrivateField<Dictionary<string, int[]>>(session, "_queryCache").Count);
            Assert.Equal(cacheLimit, GetPrivateField<LinkedList<string>>(session, "_queryCacheLru").Count);
            Assert.Equal(
                cacheLimit,
                GetPrivateField<Dictionary<string, LinkedListNode<string>>>(session, "_queryCacheNodes").Count);

            var newest = session.Search(
                root,
                "Project",
                "47",
                TestContext.Current.CancellationToken);
            var evicted = session.Search(
                root,
                "Project",
                "00",
                TestContext.Current.CancellationToken);

            Assert.True(newest.UsedCache);
            Assert.False(evicted.UsedCache);

            session.Clear();
            Assert.Empty(GetPrivateField<Dictionary<string, int[]>>(session, "_queryCache"));
        }
    }

    private static T GetPrivateField<T>(
        TreeDescriptorSearchSession session,
        string fieldName)
        => Assert.IsType<T>(GetPrivateFieldValue(session, fieldName));

    private static object? GetPrivateFieldValue(
        TreeDescriptorSearchSession session,
        string fieldName)
    {
        var field = typeof(TreeDescriptorSearchSession).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field.GetValue(session);
    }

    private static TreeNodeDescriptor Directory(
        string name,
        params TreeNodeDescriptor[] children) =>
        new(name, Path.Combine("C:", "search-index", name), true, false, "folder", children);

    private static TreeNodeDescriptor File(string name) =>
        new(name, Path.Combine("C:", "search-index", name), false, false, "file", []);
}
