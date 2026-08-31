using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class PreviewFileCollectionSelectionMatrixTests
{
    [Theory]
    [MemberData(nameof(BuildOrderedSelectionCases))]
    public void BuildOrderedSelectedFilePaths_MatrixMatchesIndependentExpansion(
        string caseName,
        string[] selectionKeys)
    {
        Assert.False(string.IsNullOrWhiteSpace(caseName));
        var fixture = SelectionFixture.Create();
        var selectedPaths = fixture.Resolve(selectionKeys);

        var actual = PreviewFileCollectionPolicy.BuildOrderedSelectedFilePaths(
            selectedPaths,
            fixture.Root,
            ensureExists: false);
        var expected = fixture.ExpandSelectionIndependent(selectedPaths);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(CountSelectionCases))]
    public void CountSelectedFilesUpToLimit_MatrixMatchesIndependentCount(
        string caseName,
        string[] selectionKeys,
        int maxCount)
    {
        Assert.False(string.IsNullOrWhiteSpace(caseName));
        var fixture = SelectionFixture.Create();
        var selectedPaths = fixture.Resolve(selectionKeys);

        var actual = PreviewFileCollectionPolicy.CountSelectedFilesUpToLimit(
            selectedPaths,
            fixture.Root,
            maxCount,
            ensureExists: false);
        var expected = Math.Min(fixture.ExpandSelectionIndependent(selectedPaths).Count, maxCount);

        Assert.Equal(expected, actual);
    }

    public static IEnumerable<object[]> BuildOrderedSelectionCases()
    {
        var singletons = new[]
        {
            "root", "readme", "src", "srcApp", "srcAssets", "program", "service", "logo",
            "assetReadme", "docs", "guide", "docsDeep", "nested", "docsEmpty",
            "tests", "appTests", "scripts", "build"
        };

        foreach (var key in singletons)
            yield return [$"single:{key}", new[] { key }];

        var pairKeys = new[]
        {
            "src", "docs", "tests", "scripts", "srcApp", "srcAssets", "docsDeep", "docsEmpty"
        };

        for (var left = 0; left < pairKeys.Length; left++)
        {
            for (var right = left + 1; right < pairKeys.Length; right++)
            {
                yield return [
                    $"pair:{pairKeys[left]}+{pairKeys[right]}",
                    new[] { pairKeys[left], pairKeys[right] }
                ];
            }
        }

        yield return ["triple:srcApp+docsDeep+scripts", new[] { "srcApp", "docsDeep", "scripts" }];
        yield return ["triple:readme+guide+appTests", new[] { "readme", "guide", "appTests" }];
    }

    public static IEnumerable<object[]> CountSelectionCases()
    {
        var cases = new (string Name, string[] Keys, int Limit)[]
        {
            ("empty-limit-0", [], 0),
            ("root-limit-1", ["root"], 1),
            ("root-limit-3", ["root"], 3),
            ("root-limit-99", ["root"], 99),
            ("src-limit-1", ["src"], 1),
            ("src-limit-2", ["src"], 2),
            ("src-limit-99", ["src"], 99),
            ("srcApp-limit-1", ["srcApp"], 1),
            ("srcApp-limit-2", ["srcApp"], 2),
            ("srcAssets-limit-1", ["srcAssets"], 1),
            ("srcAssets-limit-2", ["srcAssets"], 2),
            ("docs-limit-1", ["docs"], 1),
            ("docs-limit-2", ["docs"], 2),
            ("docs-limit-99", ["docs"], 99),
            ("docsDeep-limit-1", ["docsDeep"], 1),
            ("docsEmpty-limit-3", ["docsEmpty"], 3),
            ("tests-limit-1", ["tests"], 1),
            ("scripts-limit-1", ["scripts"], 1),
            ("scripts-limit-2", ["scripts"], 2),
            ("pair-src-docs-limit-2", ["src", "docs"], 2),
            ("pair-src-docs-limit-99", ["src", "docs"], 99),
            ("pair-srcAssets-docsEmpty-limit-99", ["srcAssets", "docsEmpty"], 99),
            ("pair-root-docs-limit-3", ["root", "docs"], 3),
            ("triple-srcApp-docsDeep-scripts-limit-99", ["srcApp", "docsDeep", "scripts"], 99)
        };

        foreach (var @case in cases)
            yield return [@case.Name, @case.Keys, @case.Limit];
    }

    private sealed class SelectionFixture
    {
        private SelectionFixture(TreeNodeDescriptor root, IReadOnlyDictionary<string, string> paths)
        {
            Root = root;
            _paths = paths;
        }

        private readonly IReadOnlyDictionary<string, string> _paths;

        public TreeNodeDescriptor Root { get; }

        public static SelectionFixture Create()
        {
            var paths = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["root"] = CreatePath("root"),
                ["readme"] = CreatePath("root", "README.md"),
                ["src"] = CreatePath("root", "src"),
                ["srcApp"] = CreatePath("root", "src", "app"),
                ["srcAssets"] = CreatePath("root", "src", "assets"),
                ["program"] = CreatePath("root", "src", "app", "Program.cs"),
                ["service"] = CreatePath("root", "src", "app", "Service.cs"),
                ["logo"] = CreatePath("root", "src", "assets", "logo.bin"),
                ["assetReadme"] = CreatePath("root", "src", "assets", "readme.md"),
                ["docs"] = CreatePath("root", "docs"),
                ["guide"] = CreatePath("root", "docs", "guide.md"),
                ["docsDeep"] = CreatePath("root", "docs", "deep"),
                ["nested"] = CreatePath("root", "docs", "deep", "nested.txt"),
                ["docsEmpty"] = CreatePath("root", "docs", "empty"),
                ["tests"] = CreatePath("root", "tests"),
                ["appTests"] = CreatePath("root", "tests", "AppTests.cs"),
                ["scripts"] = CreatePath("root", "scripts"),
                ["build"] = CreatePath("root", "scripts", "build")
            };

            var root = new TreeNodeDescriptor(
                DisplayName: "root",
                FullPath: paths["root"],
                IsDirectory: true,
                IsAccessDenied: false,
                IconKey: "folder",
                Children:
                [
                    new TreeNodeDescriptor("README.md", paths["readme"], false, false, "markdown", []),
                    new TreeNodeDescriptor(
                        "src",
                        paths["src"],
                        true,
                        false,
                        "folder",
                        [
                            new TreeNodeDescriptor(
                                "app",
                                paths["srcApp"],
                                true,
                                false,
                                "folder",
                                [
                                    new TreeNodeDescriptor("Program.cs", paths["program"], false, false, "csharp", []),
                                    new TreeNodeDescriptor("Service.cs", paths["service"], false, false, "csharp", [])
                                ]),
                            new TreeNodeDescriptor(
                                "assets",
                                paths["srcAssets"],
                                true,
                                false,
                                "folder",
                                [
                                    new TreeNodeDescriptor("logo.bin", paths["logo"], false, false, "binary", []),
                                    new TreeNodeDescriptor("readme.md", paths["assetReadme"], false, false, "markdown", [])
                                ])
                        ]),
                    new TreeNodeDescriptor(
                        "docs",
                        paths["docs"],
                        true,
                        false,
                        "folder",
                        [
                            new TreeNodeDescriptor("guide.md", paths["guide"], false, false, "markdown", []),
                            new TreeNodeDescriptor(
                                "deep",
                                paths["docsDeep"],
                                true,
                                false,
                                "folder",
                                [
                                    new TreeNodeDescriptor("nested.txt", paths["nested"], false, false, "text", [])
                                ]),
                            new TreeNodeDescriptor("empty", paths["docsEmpty"], true, false, "folder", [])
                        ]),
                    new TreeNodeDescriptor(
                        "tests",
                        paths["tests"],
                        true,
                        false,
                        "folder",
                        [
                            new TreeNodeDescriptor("AppTests.cs", paths["appTests"], false, false, "csharp", [])
                        ]),
                    new TreeNodeDescriptor(
                        "scripts",
                        paths["scripts"],
                        true,
                        false,
                        "folder",
                        [
                            new TreeNodeDescriptor("build", paths["build"], false, false, "text", [])
                        ])
                ]);

            return new SelectionFixture(root, paths);
        }

        public HashSet<string> Resolve(IEnumerable<string> selectionKeys)
        {
            var selected = new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer);
            foreach (var key in selectionKeys)
                selected.Add(_paths[key]);

            return selected;
        }

        public List<string> ExpandSelectionIndependent(IReadOnlySet<string> selectedPaths)
        {
            var unique = new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer);
            ExpandNode(Root, selectedPaths, ancestorSelected: false, unique);

            var ordered = new List<string>(unique);
            ordered.Sort(ProjectTreePathIdentity.CanonicalComparer);
            return ordered;
        }

        private static void ExpandNode(
            TreeNodeDescriptor node,
            IReadOnlySet<string> selectedPaths,
            bool ancestorSelected,
            HashSet<string> unique)
        {
            var isSelected = ancestorSelected || selectedPaths.Contains(node.FullPath);
            if (!node.IsDirectory)
            {
                if (isSelected)
                    unique.Add(node.FullPath);

                return;
            }

            for (var index = 0; index < node.Children.Count; index++)
                ExpandNode(node.Children[index], selectedPaths, isSelected, unique);
        }
    }

    private static string CreatePath(params string[] segments) => Path.Combine(segments);
}
