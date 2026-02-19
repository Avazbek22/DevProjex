namespace DevProjex.Tests.Unit.Avalonia;

public sealed class MainWindowPreviewInternalsTests
{
    [Theory]
    [InlineData("", 1)]
    [InlineData("one", 1)]
    [InlineData("one\ntwo", 2)]
    [InlineData("a\r\nb\r\nc", 3)]
    [InlineData("\n\n\n", 4)]
    public void CountPreviewLines_ReturnsExpectedValue(string text, int expected)
    {
        var method = GetPrivateStaticMethod("CountPreviewLines");

        var result = (int)method.Invoke(null, [text])!;

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CountPreviewLines_LargeInput_RemainsStable()
    {
        var method = GetPrivateStaticMethod("CountPreviewLines");
        var text = string.Join('\n', Enumerable.Range(1, 200_000));

        var result = (int)method.Invoke(null, [text])!;

        Assert.Equal(200_000, result);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1_499_999, 34_999, false)]
    [InlineData(1_500_000, 10, true)]
    [InlineData(10, 35_000, true)]
    [InlineData(2_000_000, 100_000, true)]
    public void ShouldForcePreviewMemoryCleanup_UsesThresholdPolicy(int textLength, int lineCount, bool expected)
    {
        var method = GetPrivateStaticMethod("ShouldForcePreviewMemoryCleanup");

        var result = (bool)method.Invoke(null, [textLength, lineCount])!;

        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildPathSetHash_EmptySet_IsZero()
    {
        var method = GetPrivateStaticMethod("BuildPathSetHash");

        var result = (int)method.Invoke(null, [new HashSet<string>(PathComparer.Default)])!;

        Assert.Equal(0, result);
    }

    [Fact]
    public void BuildPathSetHash_IsOrderIndependent()
    {
        var method = GetPrivateStaticMethod("BuildPathSetHash");

        var setA = new HashSet<string>(PathComparer.Default) { "/a/b.cs", "/c/d.cs", "/e/f.cs" };
        var setB = new HashSet<string>(PathComparer.Default) { "/e/f.cs", "/a/b.cs", "/c/d.cs" };

        var hashA = (int)method.Invoke(null, [setA])!;
        var hashB = (int)method.Invoke(null, [setB])!;

        Assert.Equal(hashA, hashB);
    }

    [Fact]
    public void BuildPreviewCacheKey_SameArguments_ProduceEqualKey()
    {
        var method = GetPrivateStaticMethod("BuildPreviewCacheKey");
        var root = CreateTree("root");
        var selected = new HashSet<string>(PathComparer.Default) { "/root/a.cs" };

        var keyA = method.Invoke(null, ["/root", root, PreviewContentMode.Content, TreeTextFormat.Json, selected]);
        var keyB = method.Invoke(null, ["/root", root, PreviewContentMode.Content, TreeTextFormat.Json, selected]);

        Assert.NotNull(keyA);
        Assert.NotNull(keyB);
        Assert.True(keyA!.Equals(keyB));
    }

    [Fact]
    public void BuildPreviewCacheKey_DifferentMode_ProduceDifferentKey()
    {
        var method = GetPrivateStaticMethod("BuildPreviewCacheKey");
        var root = CreateTree("root");
        var selected = new HashSet<string>(PathComparer.Default) { "/root/a.cs" };

        var keyA = method.Invoke(null, ["/root", root, PreviewContentMode.Tree, TreeTextFormat.Ascii, selected]);
        var keyB = method.Invoke(null, ["/root", root, PreviewContentMode.TreeAndContent, TreeTextFormat.Ascii, selected]);

        Assert.NotNull(keyA);
        Assert.NotNull(keyB);
        Assert.False(keyA!.Equals(keyB));
    }

    [Fact]
    public void BuildPreviewCacheKey_DifferentTreeInstance_ProduceDifferentKey()
    {
        var method = GetPrivateStaticMethod("BuildPreviewCacheKey");
        var selected = new HashSet<string>(PathComparer.Default) { "/root/a.cs" };
        var rootA = CreateTree("root");
        var rootB = CreateTree("root");

        var keyA = method.Invoke(null, ["/root", rootA, PreviewContentMode.Tree, TreeTextFormat.Json, selected]);
        var keyB = method.Invoke(null, ["/root", rootB, PreviewContentMode.Tree, TreeTextFormat.Json, selected]);

        Assert.NotNull(keyA);
        Assert.NotNull(keyB);
        Assert.False(keyA!.Equals(keyB));
    }

    private static MethodInfo GetPrivateStaticMethod(string name)
    {
        var method = typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!;
    }

    private static TreeNodeDescriptor CreateTree(string rootName)
    {
        return new TreeNodeDescriptor(
            DisplayName: rootName,
            FullPath: $"/{rootName}",
            IsDirectory: true,
            IsAccessDenied: false,
            IconKey: "folder",
            Children:
            [
                new TreeNodeDescriptor(
                    DisplayName: "a.cs",
                    FullPath: $"/{rootName}/a.cs",
                    IsDirectory: false,
                    IsAccessDenied: false,
                    IconKey: "csharp",
                    Children: [])
            ]);
    }
}
