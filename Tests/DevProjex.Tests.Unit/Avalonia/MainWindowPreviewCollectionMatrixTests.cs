using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class MainWindowPreviewCollectionMatrixTests
{
    [Theory]
    [InlineData("", 1)]
    [InlineData("\r", 1)]
    [InlineData("\n", 2)]
    [InlineData("\r\n", 2)]
    [InlineData("\r\r", 1)]
    [InlineData("\n\r\n", 3)]
    [InlineData("one\rtwo", 1)]
    [InlineData("one\r\ntwo", 2)]
    [InlineData("one\n\ntwo", 3)]
    [InlineData("\n\n\n\n", 5)]
    [InlineData("a\r\nb\nc\r\nd", 4)]
    [InlineData("trail\n", 2)]
    public void CountPreviewLines_Matrix(string text, int expectedLines)
    {
        var result = PreviewFileCollectionPolicy.CountPreviewLines(text);

        Assert.Equal(expectedLines, result);
    }

    [Theory]
    [InlineData(true, false, false, false, "A.txt")]
    [InlineData(false, true, false, false, "b.txt")]
    [InlineData(true, true, false, false, "A.txt|b.txt")]
    [InlineData(true, false, true, false, "A.txt")]
    [InlineData(false, false, true, false, "")]
    [InlineData(false, false, false, true, "nested.txt")]
    [InlineData(true, false, false, true, "A.txt|nested.txt")]
    [InlineData(true, true, true, true, "A.txt|b.txt|nested.txt")]
    public void CollectOrderedPreviewFiles_WithSelection_ReturnsOnlyExistingFiles(
        bool includeA,
        bool includeB,
        bool includeMissing,
        bool includeDirectory,
        string expectedFileNames)
    {
        using var temp = new TemporaryDirectory();

        var fileA = temp.CreateFile("A.txt", "A");
        var fileB = temp.CreateFile("b.txt", "B");
        var fileInTreeOnly = temp.CreateFile(Path.Combine("tree", "only.txt"), "X");
        var fileInDirectory = temp.CreateFile(Path.Combine("folder", "nested.txt"), "N");
        var missing = Path.Combine(temp.Path, "missing.txt");
        var directory = temp.CreateFolder("folder");
        var treeRoot = CreateTree(
            temp.Path,
            [
                Path.GetRelativePath(temp.Path, fileA),
                Path.GetRelativePath(temp.Path, fileB),
                Path.GetRelativePath(temp.Path, fileInDirectory),
                Path.GetRelativePath(temp.Path, fileInTreeOnly)
            ]);

        var selectedPaths = new HashSet<string>(PathComparer.Default);
        if (includeA)
            selectedPaths.Add(fileA);
        if (includeB)
            selectedPaths.Add(fileB);
        if (includeMissing)
            selectedPaths.Add(missing);
        if (includeDirectory)
            selectedPaths.Add(directory);

        var result = PreviewFileCollectionPolicy.CollectOrderedPreviewFiles(selectedPaths, true, treeRoot);
        var actual = string.Join("|", result.Select(Path.GetFileName));

        Assert.Equal(expectedFileNames, actual);
        Assert.DoesNotContain(result, path => Path.GetFileName(path) == "only.txt");
    }

    [Theory]
    [MemberData(nameof(TreeFileCases))]
    public void CollectOrderedPreviewFiles_WithoutSelection_UsesTreeFilesOnly(
        int caseId,
        string[] relativeFiles)
    {
        using var temp = new TemporaryDirectory();

        foreach (var relative in relativeFiles.Distinct(ProjectTreePathIdentity.CanonicalComparer))
        {
            var fullPath = Path.Combine(temp.Path, relative.Replace('/', Path.DirectorySeparatorChar));
            var parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);
            File.WriteAllText(fullPath, $"content-{caseId}");
        }

        var treeRoot = CreateTree(temp.Path, relativeFiles);
        var selectedPaths = new HashSet<string>(PathComparer.Default)
        {
            Path.Combine(temp.Path, "selected-but-ignored.txt")
        };

        var actual = PreviewFileCollectionPolicy.CollectOrderedPreviewFiles(selectedPaths, false, treeRoot);

        var expected = relativeFiles
            .Select(relative => Path.Combine(temp.Path, relative.Replace('/', Path.DirectorySeparatorChar)))
            .Distinct(ProjectTreePathIdentity.CanonicalComparer)
            .OrderBy(path => path, ProjectTreePathIdentity.CanonicalComparer)
            .ToList();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CollectOrderedPreviewFiles_WithoutSelection_PreservesCaseVariantTreeFiles()
    {
        using var temp = new TemporaryDirectory();
        var relativeFiles = new[] { "src/A.cs", "src/a.cs", "src/B.cs" };

        foreach (var relative in relativeFiles)
        {
            var fullPath = Path.Combine(temp.Path, relative.Replace('/', Path.DirectorySeparatorChar));
            var parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);
            File.WriteAllText(fullPath, relative, Encoding.UTF8);
        }

        var treeRoot = CreateTree(temp.Path, relativeFiles);
        var actual = PreviewFileCollectionPolicy.CollectOrderedPreviewFiles(
            new HashSet<string>(StringComparer.Ordinal),
            false,
            treeRoot);

        var expected = relativeFiles
            .Select(relative => Path.Combine(temp.Path, relative.Replace('/', Path.DirectorySeparatorChar)))
            .Distinct(ProjectTreePathIdentity.CanonicalComparer)
            .OrderBy(path => path, ProjectTreePathIdentity.CanonicalComparer)
            .ToList();

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("/root/a.cs;/root/b.cs", "/root/b.cs;/root/a.cs", true)]
    [InlineData("/root/A.cs;/root/b.cs", "/root/a.cs;/root/B.cs", false)]
    [InlineData("/root/a.cs;/root/b.cs", "/root/a.cs;/root/c.cs", false)]
    [InlineData("/root/a.cs", "/root/a.cs;/root/b.cs", false)]
    [InlineData("", "", true)]
    [InlineData("", "/root/a.cs", false)]
    public void BuildPathSetHash_CaseAndOrderMatrix(string left, string right, bool expectedEqual)
    {
        var leftSet = ParseSet(left);
        var rightSet = ParseSet(right);

        var leftHash = PreviewFileCollectionPolicy.BuildPathSetHash(leftSet);
        var rightHash = PreviewFileCollectionPolicy.BuildPathSetHash(rightSet);

        if (expectedEqual)
        {
            expectedEqual = leftSet.Count == rightSet.Count &&
                leftSet.OrderBy(path => path, ProjectTreePathIdentity.CanonicalComparer)
                    .SequenceEqual(
                        rightSet.OrderBy(path => path, ProjectTreePathIdentity.CanonicalComparer),
                        ProjectTreePathIdentity.CanonicalComparer);
        }

        Assert.Equal(expectedEqual, leftHash == rightHash);
    }

    public static IEnumerable<object[]> TreeFileCases()
    {
        yield return [0, Array.Empty<string>()];
        yield return [1, new[] { "z.txt" }];
        yield return [2, new[] { "z.txt", "a.txt" }];
        yield return [3, new[] { "src/main.cs", "README.md" }];
        yield return [4, new[] { "src/a.cs", "src/nested/b.cs", "docs/readme.md" }];
        yield return [5, new[] { ".env", "src/.editorconfig", "src/app.cs" }];
        yield return [6, new[] { "src/a.cs", "src/a.cs", "src/b.cs" }];
        yield return [7, new[] { "док/тест.txt", "src/Пример.cs", "a.txt" }];
    }

    private static IReadOnlySet<string> ParseSet(string source)
    {
        var set = new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer);
        if (string.IsNullOrWhiteSpace(source))
            return set;

        foreach (var value in source.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            set.Add(value);

        return set;
    }

    private static TreeNodeDescriptor CreateTree(string rootPath, IReadOnlyList<string> relativeFiles)
    {
        var root = new MutableTreeNode("Root", rootPath, isDirectory: true);
        foreach (var relativeFile in relativeFiles)
        {
            var normalized = relativeFile.Replace('\\', '/');
            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
                continue;

            var current = root;
            var currentPath = rootPath;
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                var isLast = i == segments.Length - 1;
                currentPath = Path.Combine(currentPath, segment);

                if (!current.Children.TryGetValue(segment, out var child))
                {
                    child = new MutableTreeNode(segment, currentPath, isDirectory: !isLast);
                    current.Children[segment] = child;
                }

                if (!isLast)
                    child.IsDirectory = true;

                current = child;
            }
        }

        return ToDescriptor(root);
    }

    private static TreeNodeDescriptor ToDescriptor(MutableTreeNode node)
    {
        var children = node.Children.Values
            .Select(ToDescriptor)
            .ToList();

        return new TreeNodeDescriptor(
            DisplayName: node.Name,
            FullPath: node.FullPath,
            IsDirectory: node.IsDirectory,
            IsAccessDenied: false,
            IconKey: node.IsDirectory ? "folder" : "file",
            Children: children);
    }

    private sealed class MutableTreeNode(string name, string fullPath, bool isDirectory)
    {
        public string Name { get; } = name;
        public string FullPath { get; } = fullPath;
        public bool IsDirectory { get; set; } = isDirectory;
        public Dictionary<string, MutableTreeNode> Children { get; } =
            new(ProjectTreePathIdentity.CanonicalComparer);
    }
}
