using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class MainWindowPreviewWarmupTests
{
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
    public void ShouldBuildPreviewWarmup_TreeModeAlwaysReturnsFalse()
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

        Assert.False(result);
    }

    [Fact]
    public void ShouldBuildPreviewWarmup_ContentModeRequiresSelectionThreshold()
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
    public void ShouldBuildPreviewWarmup_ContentModeStaysBelowThresholdForSmallSelection()
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

        Assert.False(result);
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

    private static string CreatePath(params string[] segments)
    {
        return OperatingSystem.IsWindows()
            ? Path.Combine(["C:\\", ..segments])
            : Path.Combine(["/", ..segments]);
    }
}
