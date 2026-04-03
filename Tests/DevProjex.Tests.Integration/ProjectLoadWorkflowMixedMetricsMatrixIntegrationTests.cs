using DevProjex.Avalonia.Services;
using DevProjex.Tests.Shared.ProjectLoadWorkflow;

namespace DevProjex.Tests.Integration;

public sealed class ProjectLoadWorkflowMixedMetricsMatrixIntegrationTests
{
    [Theory]
    [MemberData(nameof(MetricsCases))]
    public async Task ComputeMetricsAsync_MixedWorkspaceMatrix_MatchesDirectExportPipeline(
        string caseName,
        string[] selectedRoots,
        string[] allowedExtensions,
        IgnoreOptionId[] selectedIgnoreOptions)
    {
        Assert.False(string.IsNullOrWhiteSpace(caseName));
        using var workspace = new MixedWorkspaceFixture();

        var runtimeMetrics = await ProjectLoadWorkflowRuntime.ComputeMetricsAsync(
            workspace.RootPath,
            selectedRoots,
            allowedExtensions,
            selectedIgnoreOptions,
            CancellationToken.None);
        var directMetrics = await workspace.ComputeDirectMetricsAsync(
            selectedRoots,
            allowedExtensions,
            selectedIgnoreOptions,
            CancellationToken.None);

        Assert.Equal(directMetrics.TreeMetrics, runtimeMetrics.TreeMetrics);
        Assert.Equal(directMetrics.ContentMetrics, runtimeMetrics.ContentMetrics);
    }

    public static IEnumerable<object[]> MetricsCases()
    {
        yield return ["full-no-ignore", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<IgnoreOptionId>()];
        yield return ["full-gitignore", Array.Empty<string>(), Array.Empty<string>(), new[] { IgnoreOptionId.UseGitIgnore }];
        yield return ["full-dot-and-empty-rules", Array.Empty<string>(), Array.Empty<string>(), new[] { IgnoreOptionId.DotFolders, IgnoreOptionId.EmptyFiles, IgnoreOptionId.EmptyFolders }];
        yield return ["full-text-rules", Array.Empty<string>(), Array.Empty<string>(), new[] { IgnoreOptionId.DotFolders, IgnoreOptionId.EmptyFiles, IgnoreOptionId.EmptyFolders, IgnoreOptionId.ExtensionlessFiles }];
        yield return ["full-markdown-only", Array.Empty<string>(), new[] { ".md" }, Array.Empty<IgnoreOptionId>()];
        yield return ["full-csharp-only", Array.Empty<string>(), new[] { ".cs" }, Array.Empty<IgnoreOptionId>()];
        yield return ["full-powershell-only", Array.Empty<string>(), new[] { ".ps1" }, Array.Empty<IgnoreOptionId>()];
        yield return ["src-only-no-ignore", new[] { "src" }, Array.Empty<string>(), Array.Empty<IgnoreOptionId>()];
        yield return ["src-only-csharp", new[] { "src" }, new[] { ".cs" }, Array.Empty<IgnoreOptionId>()];
        yield return ["src-only-markdown", new[] { "src" }, new[] { ".md" }, Array.Empty<IgnoreOptionId>()];
        yield return ["src-only-text-rules", new[] { "src" }, Array.Empty<string>(), new[] { IgnoreOptionId.EmptyFiles, IgnoreOptionId.ExtensionlessFiles }];
        yield return ["docs-only-no-ignore", new[] { "docs" }, Array.Empty<string>(), Array.Empty<IgnoreOptionId>()];
        yield return ["docs-only-text", new[] { "docs" }, new[] { ".md", ".txt" }, Array.Empty<IgnoreOptionId>()];
        yield return ["docs-only-empty-rules", new[] { "docs" }, Array.Empty<string>(), new[] { IgnoreOptionId.EmptyFolders, IgnoreOptionId.EmptyFiles }];
        yield return ["scripts-only-no-ignore", new[] { "scripts" }, Array.Empty<string>(), Array.Empty<IgnoreOptionId>()];
        yield return ["scripts-only-ps1", new[] { "scripts" }, new[] { ".ps1" }, Array.Empty<IgnoreOptionId>()];
        yield return ["scripts-only-extensionless-hidden", new[] { "scripts" }, Array.Empty<string>(), new[] { IgnoreOptionId.ExtensionlessFiles }];
        yield return ["generated-only-no-ignore", new[] { "generated" }, Array.Empty<string>(), Array.Empty<IgnoreOptionId>()];
        yield return ["generated-only-gitignore", new[] { "generated" }, Array.Empty<string>(), new[] { IgnoreOptionId.UseGitIgnore }];
        yield return ["cache-only-no-ignore", new[] { ".cache" }, Array.Empty<string>(), Array.Empty<IgnoreOptionId>()];
        yield return ["cache-only-dotfolders", new[] { ".cache" }, Array.Empty<string>(), new[] { IgnoreOptionId.DotFolders }];
        yield return ["src-and-docs", new[] { "src", "docs" }, Array.Empty<string>(), Array.Empty<IgnoreOptionId>()];
        yield return ["src-and-scripts-extensionless-hidden", new[] { "src", "scripts" }, Array.Empty<string>(), new[] { IgnoreOptionId.ExtensionlessFiles }];
        yield return ["docs-and-generated-gitignore", new[] { "docs", "generated" }, Array.Empty<string>(), new[] { IgnoreOptionId.UseGitIgnore }];
    }

    private sealed class MixedWorkspaceFixture : IDisposable
    {
        private readonly TemporaryDirectory _temporaryDirectory = new();

        public MixedWorkspaceFixture()
        {
            RootPath = Path.Combine(_temporaryDirectory.Path, "workspace");
            Directory.CreateDirectory(RootPath);
            SeedWorkspace(RootPath);
        }

        public string RootPath { get; }

        public void Dispose() => _temporaryDirectory.Dispose();

        public async Task<ProjectLoadWorkflowRuntime.ProjectLoadWorkflowMetrics> ComputeDirectMetricsAsync(
            IReadOnlyCollection<string> selectedRoots,
            IReadOnlyCollection<string> allowedExtensions,
            IReadOnlyCollection<IgnoreOptionId> selectedIgnoreOptions,
            CancellationToken cancellationToken)
        {
            var selectedRootSet = new HashSet<string>(selectedRoots, PathComparer.Default);
            var allowedExtensionSet = new HashSet<string>(allowedExtensions, StringComparer.OrdinalIgnoreCase);
            var ignoreRulesService = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();
            var ignoreRules = ignoreRulesService.Build(RootPath, selectedIgnoreOptions, selectedRootSet);
            var buildTreeUseCase = ProjectLoadWorkflowRuntime.CreateBuildTreeUseCase();
            var buildResult = buildTreeUseCase.Execute(new BuildTreeRequest(
                RootPath,
                new TreeFilterOptions(
                    AllowedExtensions: allowedExtensionSet,
                    AllowedRootFolders: selectedRootSet,
                    IgnoreRules: ignoreRules)));

            var treeExport = new TreeExportService();
            var treeText = treeExport.BuildFullTree(RootPath, buildResult.Root, TreeTextFormat.Ascii);
            var treeMetrics = ExportOutputMetricsCalculator.FromText(treeText);
            var contentMetrics = await ComputeContentMetricsAsync(buildResult.Root, cancellationToken);

            return new ProjectLoadWorkflowRuntime.ProjectLoadWorkflowMetrics(treeMetrics, contentMetrics);
        }

        private static async Task<ExportOutputMetrics> ComputeContentMetricsAsync(
            TreeNodeDescriptor root,
            CancellationToken cancellationToken)
        {
            var analyzer = new FileContentAnalyzer();
            var orderedPaths = PreviewFileCollectionPolicy.BuildOrderedAllFilePaths(root);
            if (orderedPaths.Count == 0)
                return ExportOutputMetrics.Empty;

            var contentFiles = new List<ContentFileMetrics>(orderedPaths.Count);
            foreach (var path in orderedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var metrics = await analyzer.GetTextFileMetricsAsync(path, cancellationToken);
                if (metrics is null)
                    continue;

                contentFiles.Add(new ContentFileMetrics(
                    Path: path,
                    SizeBytes: metrics.SizeBytes,
                    LineCount: metrics.LineCount,
                    CharCount: metrics.CharCount,
                    IsEmpty: metrics.IsEmpty,
                    IsWhitespaceOnly: metrics.IsWhitespaceOnly,
                    IsEstimated: metrics.IsEstimated,
                    CrLfPairCount: metrics.CrLfPairCount,
                    TrailingNewlineChars: metrics.TrailingNewlineChars,
                    TrailingNewlineLineBreaks: metrics.TrailingNewlineLineBreaks));
            }

            return ExportOutputMetricsCalculator.FromOrderedContentFiles(contentFiles);
        }

        private static void SeedWorkspace(string rootPath)
        {
            WriteFile(rootPath, ".gitignore", "generated/\n.cache/\n");
            WriteFile(rootPath, "README.md", "# Mixed workspace\n");

            WriteFile(rootPath, Path.Combine("src", "Program.cs"), "namespace Mixed; public sealed class Program { }\n");
            WriteFile(rootPath, Path.Combine("src", "notes.md"), "# Notes\n\nSome markdown.\n");
            WriteFile(rootPath, Path.Combine("src", "assets", "info.txt"), "asset info\n");
            WriteBinaryFile(rootPath, Path.Combine("src", "assets", "logo.bin"), [0, 1, 2, 3, 255]);
            WriteBinaryFile(rootPath, Path.Combine("src", "assets", "map.dat"), [9, 8, 7, 0, 6]);

            WriteFile(rootPath, Path.Combine("docs", "guide.md"), "# Guide\n");
            WriteFile(rootPath, Path.Combine("docs", "deep", "manual.txt"), "manual\n");
            Directory.CreateDirectory(Path.Combine(rootPath, "docs", "empty"));

            WriteFile(rootPath, Path.Combine("scripts", "release.ps1"), "Write-Host 'release'\n");
            WriteFile(rootPath, Path.Combine("scripts", "build"), "dotnet build\n");

            WriteFile(rootPath, Path.Combine("generated", "Auto.g.cs"), "namespace Mixed.Generated; public static class Auto { }\n");
            WriteFile(rootPath, Path.Combine("generated", "empty.txt"), string.Empty);

            WriteFile(rootPath, Path.Combine(".cache", "notes.txt"), "cache notes\n");
            WriteBinaryFile(rootPath, Path.Combine(".cache", "cache.bin"), [1, 0, 2, 0, 3]);
        }

        private static void WriteFile(string rootPath, string relativePath, string content)
        {
            var fullPath = Path.Combine(rootPath, relativePath);
            var directoryPath = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
                Directory.CreateDirectory(directoryPath);

            File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        private static void WriteBinaryFile(string rootPath, string relativePath, byte[] content)
        {
            var fullPath = Path.Combine(rootPath, relativePath);
            var directoryPath = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
                Directory.CreateDirectory(directoryPath);

            File.WriteAllBytes(fullPath, content);
        }
    }
}
