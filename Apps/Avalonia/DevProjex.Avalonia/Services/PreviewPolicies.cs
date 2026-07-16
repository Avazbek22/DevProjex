using System.Runtime.CompilerServices;
using DevProjex.Kernel;

namespace DevProjex.Avalonia.Services;

internal readonly record struct PreviewCacheKeyData(
    string? ProjectPath,
    int TreeIdentity,
    PreviewContentMode Mode,
    TreeTextFormat TreeFormat,
    int SelectedCount,
    int SelectedHash);

internal readonly record struct StatusMetricLabels(
    string LinesPrefix,
    string CharsPrefix,
    string TokensPrefix);

internal static class PreviewWarmupPolicy
{
    internal const int PreviewWarmupFileThreshold = 140;

    public static bool ShouldBuildPreviewWarmup(
        PreviewContentMode mode,
        bool hasSelection,
        IReadOnlySet<string> selectedPaths,
        TreeNodeDescriptor? treeRoot)
    {
        if (mode == PreviewContentMode.Tree)
            return false;

        if (hasSelection)
            return CountSelectedFilesUpToLimit(selectedPaths, treeRoot, PreviewWarmupFileThreshold) >= PreviewWarmupFileThreshold;

        return CountTreeFilesUpToLimit(treeRoot, PreviewWarmupFileThreshold) >= PreviewWarmupFileThreshold;
    }

    public static int CountSelectedFilesUpToLimit(
        IReadOnlySet<string> selectedPaths,
        TreeNodeDescriptor? treeRoot,
        int maxCount)
    {
        if (treeRoot is null || maxCount <= 0)
            return 0;

        return PreviewFileCollectionPolicy.CountSelectedFilesUpToLimit(selectedPaths, treeRoot, maxCount);
    }

    public static int CountTreeFilesUpToLimit(TreeNodeDescriptor? treeRoot, int maxCount)
    {
        if (treeRoot is null || maxCount <= 0)
            return 0;

        var count = 0;
        var stack = new Stack<TreeNodeDescriptor>();
        stack.Push(treeRoot);

        while (stack.Count > 0 && count < maxCount)
        {
            var node = stack.Pop();
            if (!node.IsDirectory)
            {
                count++;
                continue;
            }

            for (var index = node.Children.Count - 1; index >= 0; index--)
                stack.Push(node.Children[index]);
        }

        return count;
    }

    public static List<string> CollectInitialPreviewFiles(
        IReadOnlySet<string> selectedPaths,
        bool hasSelection,
        TreeNodeDescriptor? treeRoot,
        int maxFileCount)
    {
        if (maxFileCount <= 0)
            return [];

        var uniqueFiles = new HashSet<string>(PathComparer.Default);
        if (hasSelection)
        {
            if (treeRoot is not null)
                PreviewFileCollectionPolicy.CollectSelectedFilePaths(treeRoot, selectedPaths, uniqueFiles, maxFileCount, ensureExists: true);
        }
        else if (treeRoot is not null)
        {
            CollectInitialPreviewFilesFromTree(treeRoot, uniqueFiles, maxFileCount);
        }

        if (uniqueFiles.Count == 0)
            return [];

        var files = new List<string>(uniqueFiles);
        files.Sort(PathComparer.Default);
        if (files.Count > maxFileCount)
            files.RemoveRange(maxFileCount, files.Count - maxFileCount);

        return files;
    }

    private static void CollectInitialPreviewFilesFromTree(
        TreeNodeDescriptor node,
        HashSet<string> uniqueFiles,
        int maxFileCount)
    {
        if (uniqueFiles.Count >= maxFileCount)
            return;

        if (!node.IsDirectory)
        {
            if (File.Exists(node.FullPath))
                uniqueFiles.Add(node.FullPath);
            return;
        }

        foreach (var child in node.Children)
        {
            CollectInitialPreviewFilesFromTree(child, uniqueFiles, maxFileCount);
            if (uniqueFiles.Count >= maxFileCount)
                break;
        }
    }
}

internal static class PreviewFileCollectionPolicy
{
    private const long HeavyTextThreshold = 1_500_000;
    private const int HeavyLineThreshold = 35_000;

    public static int CountPreviewLines(string text)
    {
        var lineCount = 1;
        foreach (var ch in text)
        {
            if (ch == '\n')
                lineCount++;
        }

        return lineCount;
    }

    public static bool ShouldForcePreviewMemoryCleanup(long textLength, int lineCount) =>
        textLength >= HeavyTextThreshold || lineCount >= HeavyLineThreshold;

    public static List<string> CollectOrderedPreviewFiles(
        IReadOnlySet<string> selectedPaths,
        bool hasSelection,
        TreeNodeDescriptor? treeRoot)
    {
        if (hasSelection)
        {
            return treeRoot is null
                ? []
                : BuildOrderedSelectedFilePaths(selectedPaths, treeRoot);
        }

        return treeRoot is null
            ? []
            : BuildOrderedAllFilePaths(treeRoot);
    }

    public static PreviewCacheKeyData BuildPreviewCacheKey(
        string? projectPath,
        TreeNodeDescriptor? treeRoot,
        PreviewContentMode mode,
        TreeTextFormat treeFormat,
        IReadOnlySet<string> selectedPaths)
    {
        return new PreviewCacheKeyData(
            ProjectPath: projectPath,
            TreeIdentity: treeRoot is null ? 0 : RuntimeHelpers.GetHashCode(treeRoot),
            Mode: mode,
            TreeFormat: treeFormat,
            SelectedCount: selectedPaths.Count,
            SelectedHash: BuildPathSetHash(selectedPaths));
    }

    public static int BuildPathSetHash(IReadOnlySet<string> selectedPaths)
    {
        if (selectedPaths.Count == 0)
            return 0;

        var ordered = new List<string>(selectedPaths.Count);
        ordered.AddRange(selectedPaths);
        ordered.Sort(PathComparer.Default);

        var hash = new HashCode();
        foreach (var path in ordered)
            hash.Add(path, PathComparer.Default);

        return hash.ToHashCode();
    }

    public static int CountSelectedFilesUpToLimit(
        IReadOnlySet<string> selectedPaths,
        TreeNodeDescriptor treeRoot,
        int maxCount,
        bool ensureExists = true)
    {
        if (maxCount <= 0)
            return 0;

        var uniquePaths = new HashSet<string>(PathComparer.Default);
        CollectSelectedFilePaths(treeRoot, selectedPaths, uniquePaths, maxCount, ensureExists);
        return uniquePaths.Count;
    }

    public static List<string> BuildOrderedSelectedFilePaths(
        IReadOnlySet<string> selectedPaths,
        TreeNodeDescriptor treeRoot,
        bool ensureExists = true)
    {
        var uniquePaths = new HashSet<string>(PathComparer.Default);
        CollectSelectedFilePaths(treeRoot, selectedPaths, uniquePaths, maxCount: int.MaxValue, ensureExists);

        var orderedPaths = new List<string>(uniquePaths.Count);
        orderedPaths.AddRange(uniquePaths);
        orderedPaths.Sort(PathComparer.Default);
        return orderedPaths;
    }

    /// <summary>
    /// Expands directory selections into their file descendants without requiring the UI tree
    /// to materialize every node. This keeps preview/export/metrics aligned with subtree
    /// checkbox semantics while preserving lazy TreeView branches.
    /// </summary>
    public static void CollectSelectedFilePaths(
        TreeNodeDescriptor node,
        IReadOnlySet<string> selectedPaths,
        HashSet<string> uniquePaths,
        int maxCount,
        bool ensureExists)
    {
        if (uniquePaths.Count >= maxCount)
            return;

        if (selectedPaths.Contains(node.FullPath))
        {
            CollectAllFilePaths(node, uniquePaths, maxCount, ensureExists);
            return;
        }

        if (!node.IsDirectory)
            return;

        for (var index = 0; index < node.Children.Count; index++)
        {
            CollectSelectedFilePaths(node.Children[index], selectedPaths, uniquePaths, maxCount, ensureExists);
            if (uniquePaths.Count >= maxCount)
                break;
        }
    }

    public static List<string> BuildOrderedAllFilePaths(TreeNodeDescriptor treeRoot)
    {
        // Keep a path-based uniqueness pass even though runtime trees should already be unique.
        // Tests intentionally synthesize case-variant nodes to verify cross-platform comparer semantics.
        var uniquePaths = new HashSet<string>(PathComparer.Default);
        var stack = new Stack<TreeNodeDescriptor>();
        stack.Push(treeRoot);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!node.IsDirectory)
            {
                uniquePaths.Add(node.FullPath);
                continue;
            }

            for (var index = node.Children.Count - 1; index >= 0; index--)
                stack.Push(node.Children[index]);
        }

        var orderedPaths = new List<string>(uniquePaths.Count);
        orderedPaths.AddRange(uniquePaths);
        orderedPaths.Sort(PathComparer.Default);
        return orderedPaths;
    }

    public static IEnumerable<string> EnumerateFilePaths(TreeNodeDescriptor node)
    {
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

    private static void CollectAllFilePaths(
        TreeNodeDescriptor node,
        HashSet<string> uniquePaths,
        int maxCount,
        bool ensureExists)
    {
        var stack = new Stack<TreeNodeDescriptor>();
        stack.Push(node);

        while (stack.Count > 0 && uniquePaths.Count < maxCount)
        {
            var current = stack.Pop();
            if (!current.IsDirectory)
            {
                if (!ensureExists || File.Exists(current.FullPath))
                    uniquePaths.Add(current.FullPath);
                continue;
            }

            for (var index = current.Children.Count - 1; index >= 0; index--)
                stack.Push(current.Children[index]);
        }
    }
}

internal static class PreviewSelectionMetricsPolicy
{
    public static bool TryGetCachedMetrics(
        bool hasStatusMetricsSnapshot,
        PreviewContentMode selectedMode,
        IPreviewTextDocument document,
        PreviewSelectionRange selectionRange,
        ExportOutputMetrics treeMetrics,
        ExportOutputMetrics contentMetrics,
        out ExportOutputMetrics metrics)
    {
        metrics = ExportOutputMetrics.Empty;

        if (!hasStatusMetricsSnapshot || !IsFullDocumentSelection(document, selectionRange))
            return false;

        metrics = selectedMode switch
        {
            PreviewContentMode.Tree => treeMetrics,
            PreviewContentMode.Content => contentMetrics,
            PreviewContentMode.TreeAndContent => AddMetrics(treeMetrics, contentMetrics),
            _ => ExportOutputMetrics.Empty
        };

        return metrics != ExportOutputMetrics.Empty;
    }

    public static bool IsFullDocumentSelection(IPreviewTextDocument document, PreviewSelectionRange selectionRange)
    {
        var normalizedSelection = selectionRange.Normalize();
        if (normalizedSelection.StartLine != 1 || normalizedSelection.StartColumn != 0)
            return false;

        var lastLine = Math.Max(1, document.LineCount);
        var lastLineLength = document.GetLineText(lastLine).Length;
        return normalizedSelection.EndLine == lastLine &&
               normalizedSelection.EndColumn == lastLineLength;
    }

    public static ExportOutputMetrics AddMetrics(ExportOutputMetrics left, ExportOutputMetrics right) =>
        new(left.Lines + right.Lines, left.Chars + right.Chars, left.Tokens + right.Tokens);

    public static string FormatStatusMetricsText(
        ExportOutputMetrics metrics,
        StatusMetricLabels labels,
        bool useCompactMode)
    {
        if (useCompactMode)
            return $"[{labels.LinesPrefix} {FormatNumber(metrics.Lines)}]";

        return $"[{labels.LinesPrefix} {FormatNumber(metrics.Lines)} | {labels.CharsPrefix} {FormatNumber(metrics.Chars)} | {labels.TokensPrefix} {FormatNumber(metrics.Tokens)}]";
    }

    private static string FormatNumber(long value)
    {
        return value switch
        {
            >= 1_000_000 => $"{value / 1_000_000.0:F1}M",
            >= 10_000 => $"{value / 1_000.0:F1}K",
            _ => value.ToString("N0")
        };
    }
}

internal static class MetricsCalculationPolicy
{
    // Covers the 700 ms layout-readiness fallback, reveal delay, animation and scheduler margin.
    public static readonly TimeSpan InitialVisualReadyTimeout = TimeSpan.FromMilliseconds(1400);

    public static async Task WaitForInitialVisualReadyAsync(
        Task initialVisualReadyTask,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (initialVisualReadyTask.IsCompletedSuccessfully)
            return;

        try
        {
            await initialVisualReadyTask.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            // Visual choreography must never prevent metrics from eventually starting.
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A superseded visual transition should not cancel otherwise valid metrics work.
        }
        catch when (initialVisualReadyTask.IsFaulted)
        {
            // The animation task is observed separately; metrics can safely continue.
        }
    }

    public static bool ShouldProceedWithMetricsCalculation(bool hasAnyCheckedNodes, bool hasCompleteMetricsBaseline) =>
        hasAnyCheckedNodes || hasCompleteMetricsBaseline;

    public static int GetBaselineWarmupParallelism(int processorCount)
    {
        if (processorCount <= 1)
            return 1;

        // The initial whole-project warmup is a throughput-oriented phase. Matching the older
        // aggressive fan-out keeps large baseline scans fast, while later selection recovery
        // still uses a more conservative policy to preserve UI responsiveness.
        return Math.Max(4, processorCount);
    }

    public static int GetSelectionRecoveryParallelism(int processorCount)
    {
        if (processorCount <= 1)
            return 1;

        // Recovery scans are user-driven and often happen while the user is actively interacting
        // with the window. Keep one core free for UI work and clamp the fan-out to avoid turning
        // a follow-up selection into a CPU saturation event.
        return Math.Clamp(processorCount - 1, 1, 8);
    }
}

internal static class SettingsPanelRevealPolicy
{
    // Width is the durable distinction between the first collapsed reveal and a refresh of an
    // already displayed island. Do not reduce this to SettingsVisible: it is true in both cases.
    public static bool ShouldRunInitialReveal(
        bool settingsVisible,
        bool settingsAnimating,
        bool hasVisiblePanelWidth) =>
        settingsVisible && !settingsAnimating && !hasVisiblePanelWidth;
}
