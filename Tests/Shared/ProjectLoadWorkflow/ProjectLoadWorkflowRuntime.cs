using DevProjex.Application.Services;
using DevProjex.Application.UseCases;
using DevProjex.Infrastructure.FileSystem;
using DevProjex.Infrastructure.SmartIgnore;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel.Contracts;

namespace DevProjex.Tests.Shared.ProjectLoadWorkflow;

public static class ProjectLoadWorkflowRuntime
{
    // Workflow tests intentionally reuse one immutable seeded workspace. Caching the
    // expected metrics by normalized selection state keeps the assertions production-accurate
    // while avoiding hundreds of repeated tree/content rebuilds in CI.
    private static readonly Lock MetricsCacheLock = new();
    private static readonly Dictionary<MetricsCacheKey, ProjectLoadWorkflowMetrics> MetricsCache = [];

    public static LocalizationService CreateLocalizationService() =>
        new(new WorkflowLocalizationCatalog(), AppLanguage.En);

    public static IgnoreOptionsService CreateIgnoreOptionsService() =>
        new(CreateLocalizationService());

    public static IgnoreRulesService CreateIgnoreRulesService()
    {
        var smartIgnoreRules = new ISmartIgnoreRule[]
        {
            new CommonSmartIgnoreRule(),
            new FrontendArtifactsIgnoreRule(),
            new DotNetArtifactsIgnoreRule(),
            new PythonArtifactsIgnoreRule(),
            new JvmArtifactsIgnoreRule(),
            new RustArtifactsIgnoreRule(),
            new GoArtifactsIgnoreRule(),
            new PhpArtifactsIgnoreRule(),
            new RubyArtifactsIgnoreRule()
        };

        return new IgnoreRulesService(new SmartIgnoreService(smartIgnoreRules));
    }

    public static BuildTreeUseCase CreateBuildTreeUseCase() =>
        new(
            new TreeBuilder(),
            new TreeNodePresentationService(CreateLocalizationService(), new WorkflowIconMapper()));

    public static async Task<ProjectLoadWorkflowMetrics> ComputeMetricsAsync(
        string rootPath,
        IReadOnlyCollection<string> selectedRoots,
        IReadOnlyCollection<string> allowedExtensions,
        IReadOnlyCollection<IgnoreOptionId> selectedIgnoreOptions,
        CancellationToken cancellationToken)
    {
        // The runtime UI applies selections using set semantics, not list ordering.
        // The test harness must mirror that behavior exactly or the "expected" metrics
        // drift away from the real BuildTree/Export pipeline on large workspaces.
        var selectedRootSet = new HashSet<string>(selectedRoots, PathComparer.Default);
        var allowedExtensionSet = new HashSet<string>(allowedExtensions, StringComparer.OrdinalIgnoreCase);
        var cacheKey = MetricsCacheKey.Create(rootPath, selectedRootSet, allowedExtensionSet, selectedIgnoreOptions);

        lock (MetricsCacheLock)
        {
            if (MetricsCache.TryGetValue(cacheKey, out var cachedMetrics))
                return cachedMetrics;
        }

        var ignoreRulesService = CreateIgnoreRulesService();
        var ignoreRules = ignoreRulesService.Build(rootPath, selectedIgnoreOptions, selectedRootSet);

        var buildTreeUseCase = CreateBuildTreeUseCase();
        var buildResult = buildTreeUseCase.Execute(new BuildTreeRequest(
            rootPath,
            new TreeFilterOptions(
                AllowedExtensions: allowedExtensionSet,
                AllowedRootFolders: selectedRootSet,
                IgnoreRules: ignoreRules)));

        var treeExport = new TreeExportService();
        var treeText = treeExport.BuildFullTree(rootPath, buildResult.Root, TreeTextFormat.Ascii);
        var treeMetrics = ExportOutputMetricsCalculator.FromText(treeText);

        var orderedPaths = EnumerateAllFiles(buildResult.Root)
            .Distinct(PathComparer.Default)
            .OrderBy(static path => path, PathComparer.Default)
            .ToArray();
        var contentMetrics = await ComputeContentMetricsAsync(orderedPaths, cancellationToken);

        var computedMetrics = new ProjectLoadWorkflowMetrics(treeMetrics, contentMetrics);
        lock (MetricsCacheLock)
            MetricsCache[cacheKey] = computedMetrics;

        return computedMetrics;
    }

    public static IEnumerable<string> EnumerateAllFiles(TreeNodeDescriptor node)
    {
        // An iterative walk avoids hiding recursion-related issues behind a helper that
        // silently fails only on deeper trees than the tiny unit fixtures usually cover.
        var stack = new Stack<TreeNodeDescriptor>();
        stack.Push(node);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!current.IsDirectory)
            {
                yield return current.FullPath;
                continue;
            }

            for (var index = current.Children.Count - 1; index >= 0; index--)
                stack.Push(current.Children[index]);
        }
    }

    public sealed record ProjectLoadWorkflowMetrics(
        ExportOutputMetrics TreeMetrics,
        ExportOutputMetrics ContentMetrics);

    private static async Task<ExportOutputMetrics> ComputeContentMetricsAsync(
        IReadOnlyList<string> orderedPaths,
        CancellationToken cancellationToken)
    {
        if (orderedPaths.Count == 0)
            return ExportOutputMetrics.Empty;

        var analyzer = new FileContentAnalyzer();
        var metricsInputs = new List<ContentFileMetrics>(orderedPaths.Count);
        foreach (var path in orderedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metrics = await analyzer.GetTextFileMetricsAsync(path, cancellationToken);
            if (metrics is null)
                continue;

            metricsInputs.Add(new ContentFileMetrics(
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

        return ExportOutputMetricsCalculator.FromOrderedContentFiles(metricsInputs);
    }

    private readonly record struct MetricsCacheKey(
        string RootPath,
        string RootsKey,
        string ExtensionsKey,
        string IgnoreKey)
    {
        public static MetricsCacheKey Create(
            string rootPath,
            IEnumerable<string> selectedRoots,
            IEnumerable<string> allowedExtensions,
            IEnumerable<IgnoreOptionId> selectedIgnoreOptions)
        {
            return new MetricsCacheKey(
                rootPath,
                Normalize(selectedRoots, StringComparer.OrdinalIgnoreCase),
                Normalize(allowedExtensions, StringComparer.OrdinalIgnoreCase),
                string.Join("|", selectedIgnoreOptions.OrderBy(static option => (int)option).Select(static option => option.ToString())));
        }

        private static string Normalize(IEnumerable<string> values, StringComparer comparer) =>
            string.Join("|", values.OrderBy(static value => value, comparer));
    }

    private sealed class WorkflowLocalizationCatalog : ILocalizationCatalog
    {
        public IReadOnlyDictionary<string, string> Get(AppLanguage language)
        {
            return new Dictionary<string, string>
            {
                ["Tree.AccessDeniedRoot"] = "Access denied",
                ["Tree.AccessDenied"] = "Access denied",
                ["Settings.Ignore.SmartIgnore"] = "Smart ignore",
                ["Settings.Ignore.UseGitIgnore"] = "Use .gitignore",
                ["Settings.Ignore.HiddenFolders"] = "Hidden folders",
                ["Settings.Ignore.HiddenFiles"] = "Hidden files",
                ["Settings.Ignore.DotFolders"] = "Dot folders",
                ["Settings.Ignore.DotFiles"] = "Dot files",
                ["Settings.Ignore.EmptyFolders"] = "Empty folders",
                ["Settings.Ignore.EmptyFiles"] = "Empty files",
                ["Settings.Ignore.ExtensionlessFiles"] = "Files without extension"
            };
        }
    }

    private sealed class WorkflowIconMapper : IIconMapper
    {
        public string GetIconKey(FileSystemNode node) => node.IsDirectory ? "folder" : "file";
    }
}
