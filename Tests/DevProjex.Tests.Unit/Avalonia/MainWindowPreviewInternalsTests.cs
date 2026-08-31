using Avalonia.Platform.Storage;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class MainWindowPreviewInternalsTests
{
    [Theory]
    [InlineData(TreeTextFormat.Ascii, "txt")]
    [InlineData(TreeTextFormat.Json, "json")]
    [InlineData(TreeTextFormat.Xml, "xml")]
    [InlineData(TreeTextFormat.Markdown, "md")]
    public void GetTreeExportFileExtension_ReturnsExpectedDesktopExtension(TreeTextFormat format, string expected)
    {
        var method = typeof(MainWindow).GetMethod(
            "GetTreeExportFileExtension",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.Equal(expected, method!.Invoke(null, [format]));
    }

    [Theory]
    [InlineData(TreeTextFormat.Ascii, "TXT", "*.txt")]
    [InlineData(TreeTextFormat.Json, "JSON", "*.json")]
    [InlineData(TreeTextFormat.Xml, "XML", "*.xml")]
    [InlineData(TreeTextFormat.Markdown, "Markdown", "*.md")]
    public void CreateTreeExportFileTypeChoices_OffersNativeFormatAndTextFallback(
        TreeTextFormat format,
        string expectedName,
        string expectedPattern)
    {
        var method = typeof(MainWindow).GetMethod(
            "CreateTreeExportFileTypeChoices",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var choices = Assert.IsAssignableFrom<IReadOnlyList<FilePickerFileType>>(
            method!.Invoke(null, [format]));
        var nativeChoice = choices[0];

        Assert.Equal(expectedName, nativeChoice.Name);
        Assert.Equal([expectedPattern], nativeChoice.Patterns);

        if (format == TreeTextFormat.Ascii)
        {
            Assert.Single(choices);
            return;
        }

        Assert.Equal(2, choices.Count);
        Assert.Equal("TXT", choices[1].Name);
        Assert.Equal(["*.txt"], choices[1].Patterns);
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("one", 1)]
    [InlineData("one\ntwo", 2)]
    [InlineData("a\r\nb\r\nc", 3)]
    [InlineData("\n\n\n", 4)]
    public void CountPreviewLines_ReturnsExpectedValue(string text, int expected)
    {
        var result = PreviewFileCollectionPolicy.CountPreviewLines(text);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CountPreviewLines_LargeInput_RemainsStable()
    {
        var text = string.Join('\n', Enumerable.Range(1, 200_000));

        var result = PreviewFileCollectionPolicy.CountPreviewLines(text);

        Assert.Equal(200_000, result);
    }

    [Fact]
    public void BuildPathSetHash_EmptySet_IsZero()
    {
        var result = PreviewFileCollectionPolicy.BuildPathSetHash(new HashSet<string>(PathComparer.Default));

        Assert.Equal(0, result);
    }

    [Fact]
    public void BuildPathSetHash_IsOrderIndependent()
    {
        var setA = new HashSet<string>(PathComparer.Default) { "/a/b.cs", "/c/d.cs", "/e/f.cs" };
        var setB = new HashSet<string>(PathComparer.Default) { "/e/f.cs", "/a/b.cs", "/c/d.cs" };

        var hashA = PreviewFileCollectionPolicy.BuildPathSetHash(setA);
        var hashB = PreviewFileCollectionPolicy.BuildPathSetHash(setB);

        Assert.Equal(hashA, hashB);
    }

    [Fact]
    public void BuildOrderedSelectedFilePaths_CaseVariantPathsRemainDistinctOnEveryPlatform()
    {
        var upper = CreatePath("root", "A.cs");
        var lower = CreatePath("root", "a.cs");
        var other = CreatePath("root", "B.cs");
        var selected = new HashSet<string>(StringComparer.Ordinal) { other, lower, upper };
        var root = new TreeNodeDescriptor(
            DisplayName: "root",
            FullPath: CreatePath("root"),
            IsDirectory: true,
            IsAccessDenied: false,
            IconKey: "folder",
            Children:
            [
                new TreeNodeDescriptor("B.cs", other, false, false, "csharp", []),
                new TreeNodeDescriptor("a.cs", lower, false, false, "csharp", []),
                new TreeNodeDescriptor("A.cs", upper, false, false, "csharp", [])
            ]);

        var result = PreviewFileCollectionPolicy.BuildOrderedSelectedFilePaths(selected, root, ensureExists: false);

        var expected = new[] { other, lower, upper }
            .OrderBy(path => path, ProjectTreePathIdentity.CanonicalComparer)
            .ToList();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildOrderedSelectedFilePaths_DirectorySelection_ExpandsDescendantFiles()
    {
        var innerA = CreatePath("root", "src", "a.cs");
        var innerB = CreatePath("root", "src", "nested", "b.cs");
        var ignored = CreatePath("root", "docs", "guide.md");
        var root = new TreeNodeDescriptor(
            DisplayName: "root",
            FullPath: CreatePath("root"),
            IsDirectory: true,
            IsAccessDenied: false,
            IconKey: "folder",
            Children:
            [
                new TreeNodeDescriptor(
                    "src",
                    CreatePath("root", "src"),
                    true,
                    false,
                    "folder",
                    [
                        new TreeNodeDescriptor("a.cs", innerA, false, false, "csharp", []),
                        new TreeNodeDescriptor(
                            "nested",
                            CreatePath("root", "src", "nested"),
                            true,
                            false,
                            "folder",
                            [
                                new TreeNodeDescriptor("b.cs", innerB, false, false, "csharp", [])
                            ])
                    ]),
                new TreeNodeDescriptor(
                    "docs",
                    CreatePath("root", "docs"),
                    true,
                    false,
                    "folder",
                    [
                        new TreeNodeDescriptor("guide.md", ignored, false, false, "markdown", [])
                    ])
            ]);

        var result = PreviewFileCollectionPolicy.BuildOrderedSelectedFilePaths(
            new HashSet<string>(PathComparer.Default) { CreatePath("root", "src") },
            root,
            ensureExists: false);

        Assert.Equal(
            new[] { innerA, innerB }.OrderBy(path => path, PathComparer.Default),
            result);
    }

    [Fact]
    public void BuildOrderedSelectedFilePaths_RootSelection_ReturnsAllDescendantFiles()
    {
        var first = CreatePath("root", "src", "a.cs");
        var second = CreatePath("root", "src", "nested", "b.cs");
        var third = CreatePath("root", "README.md");
        var rootPath = CreatePath("root");
        var root = new TreeNodeDescriptor(
            DisplayName: "root",
            FullPath: rootPath,
            IsDirectory: true,
            IsAccessDenied: false,
            IconKey: "folder",
            Children:
            [
                new TreeNodeDescriptor(
                    "src",
                    CreatePath("root", "src"),
                    true,
                    false,
                    "folder",
                    [
                        new TreeNodeDescriptor("a.cs", first, false, false, "csharp", []),
                        new TreeNodeDescriptor(
                            "nested",
                            CreatePath("root", "src", "nested"),
                            true,
                            false,
                            "folder",
                            [
                                new TreeNodeDescriptor("b.cs", second, false, false, "csharp", [])
                            ])
                    ]),
                new TreeNodeDescriptor("README.md", third, false, false, "markdown", [])
            ]);

        var result = PreviewFileCollectionPolicy.BuildOrderedSelectedFilePaths(
            new HashSet<string>(PathComparer.Default) { rootPath },
            root,
            ensureExists: false);

        Assert.Equal(
            new[] { first, second, third }.OrderBy(path => path, PathComparer.Default),
            result);
    }

    [Fact]
    public void BuildOrderedSelectedFilePaths_EmptyDirectorySelection_ReturnsNoFiles()
    {
        var root = new TreeNodeDescriptor(
            DisplayName: "root",
            FullPath: CreatePath("root"),
            IsDirectory: true,
            IsAccessDenied: false,
            IconKey: "folder",
            Children:
            [
                new TreeNodeDescriptor("empty", CreatePath("root", "empty"), true, false, "folder", []),
                new TreeNodeDescriptor("readme.md", CreatePath("root", "readme.md"), false, false, "markdown", [])
            ]);

        var result = PreviewFileCollectionPolicy.BuildOrderedSelectedFilePaths(
            new HashSet<string>(PathComparer.Default) { CreatePath("root", "empty") },
            root,
            ensureExists: false);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildOrderedAllFilePaths_ReturnsSortedUniqueFiles()
    {
        var root = new TreeNodeDescriptor(
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
                        new TreeNodeDescriptor("b.cs", CreatePath("root", "src", "b.cs"), false, false, "csharp", []),
                        new TreeNodeDescriptor("a.cs", CreatePath("root", "src", "a.cs"), false, false, "csharp", [])
                    ]),
                new TreeNodeDescriptor("readme.md", CreatePath("root", "readme.md"), false, false, "markdown", [])
            ]);

        var result = PreviewFileCollectionPolicy.BuildOrderedAllFilePaths(root);

        var expected = new[]
        {
            CreatePath("root", "readme.md"),
            CreatePath("root", "src", "a.cs"),
            CreatePath("root", "src", "b.cs")
        }
        .OrderBy(path => path, PathComparer.Default)
        .ToList();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildOrderedAllFilePaths_CaseVariantPathsRemainDistinctOnEveryPlatform()
    {
        var upper = CreatePath("root", "A.cs");
        var lower = CreatePath("root", "a.cs");
        var root = new TreeNodeDescriptor(
            DisplayName: "root",
            FullPath: CreatePath("root"),
            IsDirectory: true,
            IsAccessDenied: false,
            IconKey: "folder",
            Children:
            [
                new TreeNodeDescriptor("A.cs", upper, false, false, "csharp", []),
                new TreeNodeDescriptor("a.cs", lower, false, false, "csharp", [])
            ]);

        var result = PreviewFileCollectionPolicy.BuildOrderedAllFilePaths(root);
        Assert.Equal([upper, lower], result);
    }

    [Fact]
    public void BuildOrderedAllFilePaths_DeepTree_RemainsStable()
    {
        const int depth = 2048;
        var current = new TreeNodeDescriptor(
            DisplayName: "leaf.txt",
            FullPath: CreatePath("root", "leaf.txt"),
            IsDirectory: false,
            IsAccessDenied: false,
            IconKey: "text",
            Children: []);

        for (var index = depth - 1; index >= 0; index--)
        {
            current = new TreeNodeDescriptor(
                DisplayName: $"dir{index}",
                FullPath: CreatePath("root", $"dir{index}"),
                IsDirectory: true,
                IsAccessDenied: false,
                IconKey: "folder",
                Children: [current]);
        }

        var root = new TreeNodeDescriptor(
            DisplayName: "root",
            FullPath: CreatePath("root"),
            IsDirectory: true,
            IsAccessDenied: false,
            IconKey: "folder",
            Children: [current]);

        var result = PreviewFileCollectionPolicy.BuildOrderedAllFilePaths(root);

        Assert.Single(result);
        Assert.EndsWith("leaf.txt", result[0], StringComparison.OrdinalIgnoreCase);
    }

	[Fact]
	public void BuildOrderedAllFilePathsWithCancellation_StopsDuringTraversal()
	{
		using var cancellation = new CancellationTokenSource();
		var child = new TreeNodeDescriptor(
			"file.txt",
			CreatePath("root", "file.txt"),
			false,
			false,
			"file",
			[]);
		var root = new TreeNodeDescriptor(
			"root",
			CreatePath("root"),
			true,
			false,
			"folder",
			new CancelOnReadList<TreeNodeDescriptor>([child], cancellation));

		Assert.Throws<OperationCanceledException>(() =>
			PreviewFileCollectionPolicy.BuildOrderedAllFilePathsWithCancellation(
				root,
				cancellation.Token));
	}

	[Fact]
	public void BuildSelectionProjectionWithCancellation_StopsDuringTraversal()
	{
		using var cancellation = new CancellationTokenSource();
		var child = new TreeNodeDescriptor(
			"file.txt",
			CreatePath("root", "file.txt"),
			false,
			false,
			"file",
			[]);
		var root = new TreeNodeDescriptor(
			"root",
			CreatePath("root"),
			true,
			false,
			"folder",
			new CancelOnReadList<TreeNodeDescriptor>([child], cancellation));

		Assert.Throws<OperationCanceledException>(() =>
			TreeSelectionSnapshotCache.BuildProjectionWithCancellation(
				root,
				new HashSet<string>(PathComparer.Default),
				allOrderedFilePaths: null,
				cancellation.Token));
	}

    [Fact]
    public void BuildPreviewCacheKey_SameArguments_ProduceEqualKey()
    {
        var root = CreateTree("root");
        var selected = new HashSet<string>(PathComparer.Default) { "/root/a.cs" };

        var keyA = PreviewFileCollectionPolicy.BuildPreviewCacheKey("/root", root, PreviewContentMode.Content, TreeTextFormat.Json, selected);
        var keyB = PreviewFileCollectionPolicy.BuildPreviewCacheKey("/root", root, PreviewContentMode.Content, TreeTextFormat.Json, selected);

        Assert.Equal(keyA, keyB);
    }

    [Fact]
    public void BuildPreviewCacheKey_ImplicitAndCheckedRootSelectionProduceEqualKey()
    {
        var root = CreateTree("root");
        var implicitSelection = new HashSet<string>(PathComparer.Default);
        var checkedRoot = new HashSet<string>(PathComparer.Default)
        {
            root.FullPath
        };

        var implicitKey = PreviewFileCollectionPolicy.BuildPreviewCacheKey(
            "/root",
            root,
            PreviewContentMode.Content,
            TreeTextFormat.Ascii,
            implicitSelection);
        var checkedRootKey = PreviewFileCollectionPolicy.BuildPreviewCacheKey(
            "/root",
            root,
            PreviewContentMode.Content,
            TreeTextFormat.Ascii,
            checkedRoot);

        Assert.Equal(implicitKey, checkedRootKey);
        Assert.Equal(0, checkedRootKey.SelectedCount);
        Assert.Equal(0, checkedRootKey.SelectedHash);
    }

    [Fact]
    public void ZeroCheckedPathsUseTheWholeTreeForCacheWarmupAndOrderedProjection()
    {
        var root = CreateTree("root");
        var zeroCheckedPaths = new HashSet<string>(PathComparer.Default);
        var checkedRoot = new HashSet<string>(PathComparer.Default) { root.FullPath };
        var allOrderedFiles = PreviewFileCollectionPolicy.BuildOrderedAllFilePaths(root);

        Assert.Equal(
            PreviewFileCollectionPolicy.BuildPreviewCacheKey(
                "/root",
                root,
                PreviewContentMode.Content,
                TreeTextFormat.Ascii,
                checkedRoot),
            PreviewFileCollectionPolicy.BuildPreviewCacheKey(
                "/root",
                root,
                PreviewContentMode.Content,
                TreeTextFormat.Ascii,
                zeroCheckedPaths));
        Assert.Equal(
            allOrderedFiles,
            PreviewFileCollectionPolicy.CollectOrderedPreviewFiles(
                zeroCheckedPaths,
                hasSelection: false,
                root));
        Assert.Equal(
            allOrderedFiles,
            TreeSelectionSnapshotCache.BuildProjection(
                root,
                zeroCheckedPaths,
                allOrderedFiles).OrderedFiles);
        var warmupPlan = PreviewWarmupPolicy.CreateSelectionPlan(root, zeroCheckedPaths);
        Assert.NotNull(warmupPlan);
        Assert.False(warmupPlan.HasExplicitSelection);
        Assert.True(warmupPlan.SelectedRoot?.IncludesWholeSubtree);
    }

    [Fact]
    public void CollectOrderedPreviewFiles_CheckedRootMatchesImplicitFullTree()
    {
        var root = CreateTree("root");
        var implicitFiles = PreviewFileCollectionPolicy.CollectOrderedPreviewFiles(
            new HashSet<string>(PathComparer.Default),
            hasSelection: false,
            root);
        var checkedRootFiles = PreviewFileCollectionPolicy.CollectOrderedPreviewFiles(
            new HashSet<string>(PathComparer.Default) { root.FullPath },
            hasSelection: true,
            root);

        Assert.Equal(implicitFiles, checkedRootFiles);
    }

    [Fact]
    public void BuildPreviewCacheKey_DifferentMode_ProduceDifferentKey()
    {
        var root = CreateTree("root");
        var selected = new HashSet<string>(PathComparer.Default) { "/root/a.cs" };

        var keyA = PreviewFileCollectionPolicy.BuildPreviewCacheKey("/root", root, PreviewContentMode.Tree, TreeTextFormat.Ascii, selected);
        var keyB = PreviewFileCollectionPolicy.BuildPreviewCacheKey("/root", root, PreviewContentMode.TreeAndContent, TreeTextFormat.Ascii, selected);

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void BuildPreviewCacheKey_DifferentTreeInstance_ProduceDifferentKey()
    {
        var selected = new HashSet<string>(PathComparer.Default) { "/root/a.cs" };
        var rootA = CreateTree("root");
        var rootB = CreateTree("root");

        var keyA = PreviewFileCollectionPolicy.BuildPreviewCacheKey("/root", rootA, PreviewContentMode.Tree, TreeTextFormat.Json, selected);
        var keyB = PreviewFileCollectionPolicy.BuildPreviewCacheKey("/root", rootB, PreviewContentMode.Tree, TreeTextFormat.Json, selected);

        Assert.NotEqual(keyA, keyB);
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

    private static string CreatePath(params string[] segments)
    {
        return OperatingSystem.IsWindows()
            ? Path.Combine(["C:\\", ..segments])
            : Path.Combine(["/", ..segments]);
    }

	private sealed class CancelOnReadList<T>(
		IReadOnlyList<T> values,
		CancellationTokenSource cancellation) : IReadOnlyList<T>
	{
		public int Count => values.Count;

		public T this[int index]
		{
			get
			{
				var value = values[index];
				cancellation.Cancel();
				return value;
			}
		}

		public IEnumerator<T> GetEnumerator() => values.GetEnumerator();

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
	}
}
