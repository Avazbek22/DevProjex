using DevProjex.Application.Models;

namespace DevProjex.Tests.Unit;

public sealed class ProjectTreeInventoryRootFolderProjectionTests
{
    [Theory]
    [InlineData(ProjectionCase.EmptyFolderIgnored, false)]
    [InlineData(ProjectionCase.EmptyFolderVisible, true)]
    [InlineData(ProjectionCase.AllowedFile, true)]
    [InlineData(ProjectionCase.DisallowedExtension, false)]
    [InlineData(ProjectionCase.ExtensionlessFile, true)]
    [InlineData(ProjectionCase.IgnoredExtensionlessFile, false)]
    [InlineData(ProjectionCase.IgnoredEmptyFile, false)]
    [InlineData(ProjectionCase.IgnoredDotFile, false)]
    [InlineData(ProjectionCase.IgnoredHiddenFile, false)]
    [InlineData(ProjectionCase.IgnoredSmartDirectory, false)]
    [InlineData(ProjectionCase.GitIgnoredFile, false)]
    [InlineData(ProjectionCase.GitNegatedDescendant, true)]
    [InlineData(ProjectionCase.AccessDeniedDirectory, true)]
    [InlineData(ProjectionCase.DeepAllowedFile, true)]
    [InlineData(ProjectionCase.MixedIgnoredAndVisibleBranches, true)]
    public void RemoveCheckedRootsWithoutVisibleStructure_VisibilityMatrix(
        ProjectionCase projectionCase,
        bool expectedVisible)
    {
        var fixture = CreateFixture(projectionCase);
        var option = new SelectionOption("project", IsChecked: true);

        var projected = ProjectTreeInventoryRootFolderProjection.RemoveCheckedRootsWithoutVisibleStructure(
            fixture.Inventory,
            [option],
            fixture.AllowedExtensions,
            fixture.Rules,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedVisible, projected.Contains(option));
    }

    [Fact]
    public void RemoveCheckedRootsWithoutVisibleStructure_EmptyFoldersVisible_KeepsTraversableGitIgnoredRootHidden()
    {
        var rootPath = CreateSyntheticRootPath();
        var inventory = BuildInventory(rootPath, DirectoryNode("project", FileNode("drop.txt")));
        var option = new SelectionOption("project", IsChecked: true);
        var rules = CreateRules(ignoreEmptyFolders: false) with
        {
            UseGitIgnore = true,
            GitIgnoreMatcher = GitIgnoreMatcher.Build(
                rootPath,
                ["project/", "!**/packages/build/"])
        };
        var projectPath = Path.Combine(rootPath, "project");
        var gitIgnore = rules.CreateGitIgnoreScanContext(rootPath)
            .Evaluate(projectPath, "project", isDirectory: true, "project");

        Assert.True(gitIgnore.IsIgnored);
        Assert.True(gitIgnore.ShouldTraverseIgnoredDirectory);

        var projected = ProjectTreeInventoryRootFolderProjection.RemoveCheckedRootsWithoutVisibleStructure(
            inventory,
            [option],
            new HashSet<string>([".txt"], StringComparer.OrdinalIgnoreCase),
            rules,
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(option, projected);
    }

    [Fact]
    public void RemoveCheckedRootsWithoutVisibleStructure_MissingAndUncheckedRoots_PreservesOnlySelectableOptions()
    {
        var rootPath = CreateSyntheticRootPath();
        var inventory = BuildInventory(
            rootPath,
            DirectoryNode("visible", FileNode("App.cs")));
        var visible = new SelectionOption("visible", IsChecked: true);
        var missingChecked = new SelectionOption("missing-checked", IsChecked: true);
        var missingUnchecked = new SelectionOption("missing-unchecked", IsChecked: false);
        var rules = CreateRules(ignoreEmptyFolders: true);

        var projected = ProjectTreeInventoryRootFolderProjection.RemoveCheckedRootsWithoutVisibleStructure(
            inventory,
            [missingChecked, visible, missingUnchecked],
            new HashSet<string>([".cs"], StringComparer.OrdinalIgnoreCase),
            rules,
            TestContext.Current.CancellationToken);

        Assert.Equal([visible, missingUnchecked], projected);
    }

    [Fact]
    public void RemoveCheckedRootsWithoutVisibleStructure_NoProjectionChange_ReturnsOriginalCollection()
    {
        var rootPath = CreateSyntheticRootPath();
        var inventory = BuildInventory(rootPath, DirectoryNode("project", FileNode("App.cs")));
        IReadOnlyList<SelectionOption> options = [new SelectionOption("project", IsChecked: true)];

        var projected = ProjectTreeInventoryRootFolderProjection.RemoveCheckedRootsWithoutVisibleStructure(
            inventory,
            options,
            new HashSet<string>([".cs"], StringComparer.OrdinalIgnoreCase),
            CreateRules(ignoreEmptyFolders: true),
            TestContext.Current.CancellationToken);

        Assert.Same(options, projected);
    }

    [Fact]
    public void RemoveCheckedRootsWithoutVisibleStructure_RootAccessDenied_ReturnsOriginalCollection()
    {
        var rootPath = CreateSyntheticRootPath();
        var inventory = BuildInventory(rootPath, DirectoryNode("project"), rootAccessDenied: true);
        IReadOnlyList<SelectionOption> options = [new SelectionOption("project", IsChecked: true)];

        var projected = ProjectTreeInventoryRootFolderProjection.RemoveCheckedRootsWithoutVisibleStructure(
            inventory,
            options,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            CreateRules(ignoreEmptyFolders: true),
            TestContext.Current.CancellationToken);

        Assert.Same(options, projected);
    }

    [Fact]
    public void RemoveCheckedRootsWithoutVisibleStructure_Canceled_ThrowsBeforeProjection()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ProjectTreeInventoryRootFolderProjection.RemoveCheckedRootsWithoutVisibleStructure(
                BuildInventory(CreateSyntheticRootPath(), DirectoryNode("project")),
                [new SelectionOption("project", IsChecked: true)],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                CreateRules(ignoreEmptyFolders: true),
                cancellation.Token));
    }

    [Fact]
    public void ApplyScopedControllerRules_MultipleSmartIgnoredRoots_FiltersOnceWithoutDuplicates()
    {
        var rootPath = CreateSyntheticRootPath();
        IReadOnlyList<string> candidates = ["keep-a", "temp-a", "keep-b", "temp-b", "temp-c", "keep-c"];
        var rules = CreateRules(ignoreEmptyFolders: true) with
        {
            UseSmartIgnore = true,
            SmartIgnoredFolders = new HashSet<string>(["temp-a", "temp-b", "temp-c"], PathComparer.Default)
        };

        var projected = RootFolderVisibilityProjection.ApplyScopedControllerRules(
            rootPath,
            candidates,
            rules,
            TestContext.Current.CancellationToken);

        Assert.Equal(["keep-a", "keep-b", "keep-c"], projected);
        Assert.Equal(projected.Count, projected.Distinct(PathComparer.Default).Count());
    }

    [Fact]
    public void ApplyScopedControllerRules_NoControllers_ReturnsOriginalCollection()
    {
        IReadOnlyList<string> candidates = ["src", "tests"];

        var projected = RootFolderVisibilityProjection.ApplyScopedControllerRules(
            CreateSyntheticRootPath(),
            candidates,
            CreateRules(ignoreEmptyFolders: true),
            TestContext.Current.CancellationToken);

        Assert.Same(candidates, projected);
    }

    [Fact]
    public void ApplyScopedControllerRules_Canceled_ThrowsBeforeProjection()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            RootFolderVisibilityProjection.ApplyScopedControllerRules(
                CreateSyntheticRootPath(),
                ["src"],
                CreateRules(ignoreEmptyFolders: true),
                cancellation.Token));
    }

    private static ProjectionFixture CreateFixture(ProjectionCase projectionCase)
    {
        var rootPath = CreateSyntheticRootPath();
        var allowedExtensions = new HashSet<string>([".cs", ".txt"], StringComparer.OrdinalIgnoreCase);
        var rules = CreateRules(ignoreEmptyFolders: true);
        TestNode project;

        switch (projectionCase)
        {
            case ProjectionCase.EmptyFolderIgnored:
                project = DirectoryNode("project");
                break;
            case ProjectionCase.EmptyFolderVisible:
                project = DirectoryNode("project");
                rules = rules with { IgnoreEmptyFolders = false };
                break;
            case ProjectionCase.AllowedFile:
                project = DirectoryNode("project", FileNode("App.cs"));
                break;
            case ProjectionCase.DisallowedExtension:
                project = DirectoryNode("project", FileNode("readme.md"));
                break;
            case ProjectionCase.ExtensionlessFile:
                project = DirectoryNode("project", FileNode("LICENSE"));
                allowedExtensions.Clear();
                break;
            case ProjectionCase.IgnoredExtensionlessFile:
                project = DirectoryNode("project", FileNode("LICENSE"));
                rules = rules with { IgnoreExtensionlessFiles = true };
                break;
            case ProjectionCase.IgnoredEmptyFile:
                project = DirectoryNode("project", FileNode("empty.cs", length: 0));
                rules = rules with { IgnoreEmptyFiles = true };
                break;
            case ProjectionCase.IgnoredDotFile:
                project = DirectoryNode("project", FileNode(".settings.cs"));
                rules = rules with { IgnoreDotFiles = true };
                break;
            case ProjectionCase.IgnoredHiddenFile:
                project = DirectoryNode("project", FileNode("secret.cs", isHidden: true));
                rules = rules with { IgnoreHiddenFiles = true };
                break;
            case ProjectionCase.IgnoredSmartDirectory:
                project = DirectoryNode("project", DirectoryNode("generated", FileNode("Generated.cs")));
                rules = rules with
                {
                    UseSmartIgnore = true,
                    SmartIgnoredFolders = new HashSet<string>(["generated"], PathComparer.Default)
                };
                break;
            case ProjectionCase.GitIgnoredFile:
                project = DirectoryNode("project", FileNode("debug.log"));
                allowedExtensions.Add(".log");
                rules = rules with
                {
                    UseGitIgnore = true,
                    GitIgnoreMatcher = GitIgnoreMatcher.Build(rootPath, ["*.log"])
                };
                break;
            case ProjectionCase.GitNegatedDescendant:
                project = DirectoryNode(
                    "project",
                    DirectoryNode("build", FileNode("drop.txt"), FileNode("keep.txt")));
                rules = rules with
                {
                    UseGitIgnore = true,
                    GitIgnoreMatcher = GitIgnoreMatcher.Build(rootPath, ["build/**", "!build/keep.txt"])
                };
                break;
            case ProjectionCase.AccessDeniedDirectory:
                project = DirectoryNode("project", isAccessDenied: true);
                break;
            case ProjectionCase.DeepAllowedFile:
                project = DirectoryNode(
                    "project",
                    DirectoryNode("level-1", DirectoryNode("level-2", DirectoryNode("level-3", FileNode("App.cs")))));
                break;
            case ProjectionCase.MixedIgnoredAndVisibleBranches:
                project = DirectoryNode(
                    "project",
                    DirectoryNode("generated", FileNode("Generated.cs")),
                    DirectoryNode("src", FileNode("App.cs")));
                rules = rules with
                {
                    UseSmartIgnore = true,
                    SmartIgnoredFolders = new HashSet<string>(["generated"], PathComparer.Default)
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(projectionCase), projectionCase, null);
        }

        return new ProjectionFixture(
            BuildInventory(rootPath, project),
            allowedExtensions,
            rules);
    }

    private static IgnoreRules CreateRules(bool ignoreEmptyFolders) =>
        new(
            IgnoreHiddenFolders: false,
            IgnoreHiddenFiles: false,
            IgnoreDotFolders: false,
            IgnoreDotFiles: false,
            SmartIgnoredFolders: new HashSet<string>(PathComparer.Default),
            SmartIgnoredFiles: new HashSet<string>(PathComparer.Default))
        {
            IgnoreEmptyFolders = ignoreEmptyFolders
        };

    private static ProjectTreeInventorySnapshot BuildInventory(
        string rootPath,
        TestNode rootChild,
        bool rootAccessDenied = false)
    {
        var entries = new List<ProjectTreeInventoryEntry>
        {
            new(
                "workspace",
                rootPath,
                relativePath: string.Empty,
                parentIndex: -1,
                isDirectory: true,
                isHidden: false,
                length: 0)
        };
        PopulateChildren(parentIndex: 0, parentPath: rootPath, parentRelativePath: string.Empty, [rootChild]);
        return new ProjectTreeInventorySnapshot(entries, rootAccessDenied, hadAccessDenied: rootAccessDenied);

        void PopulateChildren(
            int parentIndex,
            string parentPath,
            string parentRelativePath,
            IReadOnlyList<TestNode> children)
        {
            if (children.Count == 0)
                return;

            var firstChildIndex = entries.Count;
            var directoryChildren = new List<(int Index, TestNode Node, string Path, string RelativePath)>();
            foreach (var child in children)
            {
                var fullPath = Path.Combine(parentPath, child.Name);
                var relativePath = string.IsNullOrEmpty(parentRelativePath)
                    ? child.Name
                    : Path.Combine(parentRelativePath, child.Name);
                var childIndex = entries.Count;
                entries.Add(new ProjectTreeInventoryEntry(
                    child.Name,
                    fullPath,
                    relativePath,
                    parentIndex,
                    child.IsDirectory,
                    child.IsHidden,
                    child.Length)
                {
                    IsAccessDenied = child.IsAccessDenied
                });
                if (child.IsDirectory)
                    directoryChildren.Add((childIndex, child, fullPath, relativePath));
            }

            var parent = entries[parentIndex];
            parent.FirstChildIndex = firstChildIndex;
            parent.ChildCount = children.Count;
            entries[parentIndex] = parent;

            foreach (var child in directoryChildren)
                PopulateChildren(child.Index, child.Path, child.RelativePath, child.Node.Children);
        }
    }

    private static TestNode DirectoryNode(
        string name,
        params TestNode[] children) =>
        new(name, IsDirectory: true, Length: 0, IsHidden: false, IsAccessDenied: false, children);

    private static TestNode DirectoryNode(string name, bool isAccessDenied) =>
        new(name, IsDirectory: true, Length: 0, IsHidden: false, isAccessDenied, []);

    private static TestNode FileNode(string name, long length = 1, bool isHidden = false) =>
        new(name, IsDirectory: false, length, isHidden, IsAccessDenied: false, []);

    private static string CreateSyntheticRootPath() =>
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DevProjex", "projection-unit"));

    public enum ProjectionCase
    {
        EmptyFolderIgnored,
        EmptyFolderVisible,
        AllowedFile,
        DisallowedExtension,
        ExtensionlessFile,
        IgnoredExtensionlessFile,
        IgnoredEmptyFile,
        IgnoredDotFile,
        IgnoredHiddenFile,
        IgnoredSmartDirectory,
        GitIgnoredFile,
        GitNegatedDescendant,
        AccessDeniedDirectory,
        DeepAllowedFile,
        MixedIgnoredAndVisibleBranches
    }

    private sealed record ProjectionFixture(
        ProjectTreeInventorySnapshot Inventory,
        HashSet<string> AllowedExtensions,
        IgnoreRules Rules);

    private sealed record TestNode(
        string Name,
        bool IsDirectory,
        long Length,
        bool IsHidden,
        bool IsAccessDenied,
        IReadOnlyList<TestNode> Children);
}
