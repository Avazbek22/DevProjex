using System.Runtime.CompilerServices;

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

internal sealed class PreviewWarmupSelectionPlan(
    TreeNodeDescriptor root,
    PreviewWarmupSelectedNode? selectedRoot,
    bool hasExplicitSelection)
{
    public TreeNodeDescriptor Root { get; } = root;
    public PreviewWarmupSelectedNode? SelectedRoot { get; } = selectedRoot;
    public bool HasExplicitSelection { get; } = hasExplicitSelection;
}

internal sealed class PreviewWarmupSelectedNode(
    TreeNodeDescriptor source,
    int sourceIndex,
    bool includesWholeSubtree,
    IReadOnlyList<PreviewWarmupSelectedNode> children)
{
    public TreeNodeDescriptor Source { get; } = source;
    public int SourceIndex { get; } = sourceIndex;
    public bool IncludesWholeSubtree { get; } = includesWholeSubtree;
    public IReadOnlyList<PreviewWarmupSelectedNode> Children { get; } = children;
}

internal static class PreviewWarmupPolicy
{
    private const int SmallChildListLinearLookupThreshold = 32;

    public static bool SupportsTransformationContext(ContentTransformationContext? context) =>
        context?.Redaction is null;

    public static bool ShouldBuildPreviewWarmup(
        PreviewContentMode mode,
        bool hasSelection,
        IReadOnlySet<string> selectedPaths,
        TreeNodeDescriptor? treeRoot)
        => treeRoot is not null;

    public static PreviewWarmupSelectionPlan? CreateSelectionPlan(
        TreeNodeDescriptor? treeRoot,
        IReadOnlySet<string> selectedPaths)
    {
        if (treeRoot is null)
            return null;

        var effectiveSelectedPaths =
            ProjectTreeSelectionProjection.NormalizeSelectedPaths(
                treeRoot,
                selectedPaths);
        if (effectiveSelectedPaths.Count == 0)
        {
            return new PreviewWarmupSelectionPlan(
                treeRoot,
                new PreviewWarmupSelectedNode(
                    treeRoot,
                    sourceIndex: -1,
                    includesWholeSubtree: true,
                    children: []),
                hasExplicitSelection: false);
        }

        var selectionTrie = BuildSelectionTrie(
            treeRoot.FullPath,
            effectiveSelectedPaths);
        var selectedRoot = ResolveSelectedNode(
            treeRoot,
            sourceIndex: -1,
            selectionTrie);
        return new PreviewWarmupSelectionPlan(
            treeRoot,
            selectedRoot,
            hasExplicitSelection: true);
    }

    public static TreeNodeDescriptor? CreateBoundedTreeProjection(
        TreeNodeDescriptor? treeRoot,
        IReadOnlySet<string> selectedPaths,
        int maxNodeCount)
    {
        var selectionPlan = CreateSelectionPlan(treeRoot, selectedPaths);
        return CreateBoundedTreeProjection(selectionPlan, maxNodeCount);
    }

    public static TreeNodeDescriptor? CreateBoundedTreeProjection(
        PreviewWarmupSelectionPlan? selectionPlan,
        int maxNodeCount)
    {
        if (selectionPlan is null || maxNodeCount <= 0)
            return null;

        return CloneBoundedTree(
            selectionPlan.SelectedRoot,
            selectionPlan.Root,
            maxNodeCount);
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
        var effectiveSelectedPaths = hasSelection
            ? selectedPaths
            : EmptySelectedPaths;
        var selectionPlan = CreateSelectionPlan(
            treeRoot,
            effectiveSelectedPaths);
        var maxNodeVisitCount = maxFileCount > (int.MaxValue / 32)
            ? int.MaxValue
            : Math.Max(64, maxFileCount * 32);
        return CollectInitialPreviewFiles(
            selectionPlan,
            maxFileCount,
            maxNodeVisitCount);
    }

    public static List<string> CollectInitialPreviewFiles(
        PreviewWarmupSelectionPlan? selectionPlan,
        int maxFileCount,
        int maxNodeVisitCount,
        IReadOnlyList<string>? orderedFilePaths = null)
    {
        if (selectionPlan is null ||
            maxFileCount <= 0 ||
            maxNodeVisitCount <= 0)
        {
            return [];
        }

        var uniqueFiles = new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer);
        if (!selectionPlan.HasExplicitSelection &&
            orderedFilePaths is not null)
        {
            CollectInitialPreviewFiles(
                orderedFilePaths,
                uniqueFiles,
                maxFileCount,
                maxNodeVisitCount);
        }
        else if (selectionPlan.SelectedRoot is not null)
        {
            var remainingNodeVisits = maxNodeVisitCount;
            CollectInitialPreviewFiles(
                selectionPlan.SelectedRoot,
                uniqueFiles,
                maxFileCount,
                ref remainingNodeVisits);
        }

        if (uniqueFiles.Count == 0)
            return [];

        var files = new List<string>(uniqueFiles);
        files.Sort(ProjectTreePathIdentity.CanonicalComparer);
        if (files.Count > maxFileCount)
            files.RemoveRange(maxFileCount, files.Count - maxFileCount);

        return files;
    }

    private static void CollectInitialPreviewFiles(
        IReadOnlyList<string> orderedFilePaths,
        HashSet<string> uniqueFiles,
        int maxFileCount,
        int maxPathChecks)
    {
        var pathCheckCount = Math.Min(
            orderedFilePaths.Count,
            maxPathChecks);
        for (var index = 0;
             index < pathCheckCount &&
             uniqueFiles.Count < maxFileCount;
             index++)
        {
            var path = orderedFilePaths[index];
            if (File.Exists(path))
                uniqueFiles.Add(path);
        }
    }

    private static void CollectInitialPreviewFiles(
        PreviewWarmupSelectedNode selectedNode,
        HashSet<string> uniqueFiles,
        int maxFileCount,
        ref int remainingNodeVisits)
    {
        if (remainingNodeVisits <= 0 ||
            uniqueFiles.Count >= maxFileCount)
        {
            return;
        }

        remainingNodeVisits--;
        var node = selectedNode.Source;

        if (!node.IsDirectory)
        {
            if (File.Exists(node.FullPath))
                uniqueFiles.Add(node.FullPath);
            return;
        }

        if (selectedNode.IncludesWholeSubtree)
        {
            foreach (var child in node.Children)
            {
                CollectInitialPreviewFilesFromWholeSubtree(
                    child,
                    uniqueFiles,
                    maxFileCount,
                    ref remainingNodeVisits);
                if (remainingNodeVisits <= 0 ||
                    uniqueFiles.Count >= maxFileCount)
                {
                    break;
                }
            }

            return;
        }

        foreach (var child in selectedNode.Children)
        {
            CollectInitialPreviewFiles(
                child,
                uniqueFiles,
                maxFileCount,
                ref remainingNodeVisits);
            if (remainingNodeVisits <= 0 ||
                uniqueFiles.Count >= maxFileCount)
            {
                break;
            }
        }
    }

    private static readonly IReadOnlySet<string> EmptySelectedPaths =
        new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer);

    private static void CollectInitialPreviewFilesFromWholeSubtree(
        TreeNodeDescriptor node,
        HashSet<string> uniqueFiles,
        int maxFileCount,
        ref int remainingNodeVisits)
    {
        if (remainingNodeVisits <= 0 ||
            uniqueFiles.Count >= maxFileCount)
        {
            return;
        }

        remainingNodeVisits--;
        if (!node.IsDirectory)
        {
            if (File.Exists(node.FullPath))
                uniqueFiles.Add(node.FullPath);
            return;
        }

        foreach (var child in node.Children)
        {
            CollectInitialPreviewFilesFromWholeSubtree(
                child,
                uniqueFiles,
                maxFileCount,
                ref remainingNodeVisits);
            if (remainingNodeVisits <= 0 ||
                uniqueFiles.Count >= maxFileCount)
            {
                break;
            }
        }
    }

    private static TreeNodeDescriptor CloneBoundedTree(
        PreviewWarmupSelectedNode? selectedRoot,
        TreeNodeDescriptor fallbackRoot,
        int maximumNodeCount)
    {
        var root = selectedRoot?.Source ?? fallbackRoot;
        var remainingNodeCount = maximumNodeCount - 1;
        if (!root.IsDirectory || remainingNodeCount == 0)
            return root with { Children = [] };

        var frames = new Stack<PreviewTreeCloneFrame>();
        frames.Push(new PreviewTreeCloneFrame(root, selectedRoot));
        TreeNodeDescriptor? completedTree = null;

        while (frames.TryPeek(out var frame))
        {
            if (remainingNodeCount > 0 && frame.TryTakeNextChild(out var child, out var selectedChild))
            {
                remainingNodeCount--;
                if (!child.IsDirectory || remainingNodeCount == 0)
                {
                    frame.Children.Add(child with { Children = [] });
                    continue;
                }

                frames.Push(new PreviewTreeCloneFrame(child, selectedChild));
                continue;
            }

            var completedNode = frame.Source with { Children = frame.Children };
            frames.Pop();
            if (frames.TryPeek(out var parent))
                parent.Children.Add(completedNode);
            else
                completedTree = completedNode;
        }

        return completedTree!;
    }

    private sealed class PreviewTreeCloneFrame(
        TreeNodeDescriptor source,
        PreviewWarmupSelectedNode? selectedNode)
    {
        private readonly IReadOnlyList<TreeNodeDescriptor>? _wholeChildren =
            selectedNode is null || selectedNode.IncludesWholeSubtree
                ? source.Children
                : null;
        private readonly IReadOnlyList<PreviewWarmupSelectedNode>? _selectedChildren =
            selectedNode is not null && !selectedNode.IncludesWholeSubtree
                ? selectedNode.Children
                : null;
        private int _nextChildIndex;

        public TreeNodeDescriptor Source { get; } = source;
        public List<TreeNodeDescriptor> Children { get; } = [];

        public bool TryTakeNextChild(
            out TreeNodeDescriptor child,
            out PreviewWarmupSelectedNode? selectedChild)
        {
            if (_wholeChildren is not null)
            {
                if (_nextChildIndex >= _wholeChildren.Count)
                {
                    child = null!;
                    selectedChild = null;
                    return false;
                }

                child = _wholeChildren[_nextChildIndex++];
                selectedChild = null;
                return true;
            }

            if (_selectedChildren is not null && _nextChildIndex < _selectedChildren.Count)
            {
                selectedChild = _selectedChildren[_nextChildIndex++];
                child = selectedChild.Source;
                return true;
            }

            child = null!;
            selectedChild = null;
            return false;
        }
    }

    private static SelectionPathTrieNode BuildSelectionTrie(
        string rootPath,
        IReadOnlySet<string> selectedPaths)
    {
        var root = new SelectionPathTrieNode();
        var normalizedRootPath = NormalizePathOrOriginal(rootPath);

        foreach (var selectedPath in selectedPaths)
        {
            var normalizedSelectedPath = NormalizePathOrOriginal(selectedPath);
            if (!IsPathInside(normalizedSelectedPath, normalizedRootPath))
                continue;

            if (PathComparer.Default.Equals(
                    normalizedSelectedPath,
                    normalizedRootPath))
            {
                root.IsSelected = true;
                continue;
            }

            string relativePath;
            try
            {
                relativePath = Path.GetRelativePath(
                    normalizedRootPath,
                    normalizedSelectedPath);
            }
            catch
            {
                continue;
            }

            var current = root;
            foreach (var segment in relativePath.Split(
                         DirectorySeparators,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment is "." or "..")
                {
                    current = null!;
                    break;
                }

                if (!current.Children.TryGetValue(segment, out var child))
                {
                    child = new SelectionPathTrieNode();
                    current.Children.Add(segment, child);
                }

                current = child;
            }

            if (current is not null)
                current.IsSelected = true;
        }

        return root;
    }

    private static PreviewWarmupSelectedNode? ResolveSelectedNode(
        TreeNodeDescriptor source,
        int sourceIndex,
        SelectionPathTrieNode selection)
    {
        if (selection.IsSelected)
        {
            return new PreviewWarmupSelectedNode(
                source,
                sourceIndex,
                includesWholeSubtree: true,
                children: []);
        }

        if (!source.IsDirectory ||
            selection.Children.Count == 0)
        {
            return null;
        }

        var selectedChildren = new List<PreviewWarmupSelectedNode>(
            selection.Children.Count);
        foreach (var (childName, childSelection) in selection.Children)
        {
            if (!TryFindChild(
                    source,
                    childName,
                    out var child,
                    out var childIndex))
            {
                continue;
            }

            var selectedChild = ResolveSelectedNode(
                child,
                childIndex,
                childSelection);
            if (selectedChild is not null)
                selectedChildren.Add(selectedChild);
        }

        if (selectedChildren.Count == 0)
            return null;

        selectedChildren.Sort(static (left, right) =>
            left.SourceIndex.CompareTo(right.SourceIndex));
        return new PreviewWarmupSelectedNode(
            source,
            sourceIndex,
            includesWholeSubtree: false,
            children: selectedChildren);
    }

    private static bool TryFindChild(
        TreeNodeDescriptor parent,
        string childName,
        out TreeNodeDescriptor child,
        out int childIndex)
    {
        var children = parent.Children;
        var expectedPath = NormalizePathOrOriginal(
            Path.Combine(parent.FullPath, childName));
        if (children.Count <= SmallChildListLinearLookupThreshold)
        {
            for (var index = 0; index < children.Count; index++)
            {
                var candidate = children[index];
                if (!PathsEqual(candidate.FullPath, expectedPath))
                    continue;

                child = candidate;
                childIndex = index;
                return true;
            }

            child = null!;
            childIndex = -1;
            return false;
        }

        // Runtime descriptors preserve inventory order: directories first, then
        // ordinal-ignore-case names. Binary lookup keeps first-content work independent
        // of the number of siblings while the small-list path remains test-friendly.
        var firstFileIndex = FindFirstFileIndex(children);
        if (TryFindChildInSortedRange(
                children,
                childName,
                expectedPath,
                startIndex: 0,
                endIndex: firstFileIndex,
                out child,
                out childIndex))
        {
            return true;
        }

        return TryFindChildInSortedRange(
            children,
            childName,
            expectedPath,
            firstFileIndex,
            children.Count,
            out child,
            out childIndex);
    }

    private static int FindFirstFileIndex(
        IReadOnlyList<TreeNodeDescriptor> children)
    {
        var low = 0;
        var high = children.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (children[middle].IsDirectory)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private static bool TryFindChildInSortedRange(
        IReadOnlyList<TreeNodeDescriptor> children,
        string childName,
        string expectedPath,
        int startIndex,
        int endIndex,
        out TreeNodeDescriptor child,
        out int childIndex)
    {
        var low = startIndex;
        var high = endIndex;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (ComparePathName(children[middle].FullPath, childName) < 0)
                low = middle + 1;
            else
                high = middle;
        }

        for (var index = low;
             index < endIndex;
             index++)
        {
            var candidate = children[index];
            if (ComparePathName(candidate.FullPath, childName) != 0)
                break;
            if (!PathsEqual(candidate.FullPath, expectedPath))
                continue;

            child = candidate;
            childIndex = index;
            return true;
        }

        child = null!;
        childIndex = -1;
        return false;
    }

    private static int ComparePathName(
        string path,
        string expectedName)
    {
        var name = Path.GetFileName(path.AsSpan());
        return name.CompareTo(
            expectedName.AsSpan(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(
        string candidate,
        string expected)
    {
        if (ProjectTreePathIdentity.CanonicalComparer.Equals(candidate, expected))
            return true;

        return ProjectTreePathIdentity.CanonicalComparer.Equals(
            NormalizePathOrOriginal(candidate),
            expected);
    }

    private static bool IsPathInside(string path, string rootPath)
    {
        try
        {
            return PathUtility.IsPathInside(path, rootPath);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizePathOrOriginal(string path)
    {
        try
        {
            return PathUtility.Normalize(path);
        }
        catch
        {
            return path;
        }
    }

    private static readonly char[] DirectorySeparators =
        Path.DirectorySeparatorChar == Path.AltDirectorySeparatorChar
            ? [Path.DirectorySeparatorChar]
            : [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private sealed class SelectionPathTrieNode
    {
        public Dictionary<string, SelectionPathTrieNode> Children { get; } =
            new(ProjectTreePathIdentity.CanonicalComparer);

        public bool IsSelected { get; set; }
    }
}

internal static class PreviewFileCollectionPolicy
{
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

    public static List<string> CollectOrderedPreviewFiles(
        IReadOnlySet<string> selectedPaths,
        bool hasSelection,
        TreeNodeDescriptor? treeRoot)
    {
        if (treeRoot is null)
            return [];

        var effectiveSelectedPaths =
            ProjectTreeSelectionProjection.NormalizeSelectedPaths(
                treeRoot,
                selectedPaths);
        return hasSelection && effectiveSelectedPaths.Count > 0
            ? BuildOrderedSelectedFilePaths(
                effectiveSelectedPaths,
                treeRoot)
            : BuildOrderedAllFilePaths(treeRoot);
    }

    public static PreviewCacheKeyData BuildPreviewCacheKey(
        string? projectPath,
        TreeNodeDescriptor? treeRoot,
        PreviewContentMode mode,
        TreeTextFormat treeFormat,
        IReadOnlySet<string> selectedPaths)
    {
        var effectiveSelectedPaths = treeRoot is null
            ? selectedPaths
            : ProjectTreeSelectionProjection.NormalizeSelectedPaths(
                treeRoot,
                selectedPaths);

        return new PreviewCacheKeyData(
            ProjectPath: projectPath,
            TreeIdentity: treeRoot is null ? 0 : RuntimeHelpers.GetHashCode(treeRoot),
            Mode: mode,
            TreeFormat: treeFormat,
            SelectedCount: effectiveSelectedPaths.Count,
            SelectedHash: BuildPathSetHash(effectiveSelectedPaths));
    }

    public static int BuildPathSetHash(IReadOnlySet<string> selectedPaths)
		=> BuildPathSetHashWithCancellation(selectedPaths, CancellationToken.None);

	public static int BuildPathSetHashWithCancellation(
		IReadOnlySet<string> selectedPaths,
		CancellationToken cancellationToken)
    {
		cancellationToken.ThrowIfCancellationRequested();
        if (selectedPaths.Count == 0)
            return 0;

		return selectedPaths is HashSet<string> hashSet
			? BuildHashSetFingerprint(hashSet, cancellationToken)
			: BuildSetFingerprint(selectedPaths, cancellationToken);
    }

	private static int BuildHashSetFingerprint(
		HashSet<string> selectedPaths,
		CancellationToken cancellationToken)
	{
		var accumulator = new PathSetHashAccumulator();
		foreach (var path in selectedPaths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			accumulator.Add(path);
		}

		return accumulator.Complete();
	}

	private static int BuildSetFingerprint(
		IEnumerable<string> selectedPaths,
		CancellationToken cancellationToken)
	{
		var accumulator = new PathSetHashAccumulator();
		foreach (var path in selectedPaths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			accumulator.Add(path);
		}

		return accumulator.Complete();
	}

	private struct PathSetHashAccumulator
	{
		private int _count;
		private uint _sum;
		private uint _sumOfSquares;
		private uint _xor;

		public void Add(string path)
		{
			_count++;
			var hash = Mix(unchecked((uint)
				ProjectTreePathIdentity.CanonicalComparer.GetHashCode(path)));
			unchecked
			{
				_sum += hash;
				_sumOfSquares += hash * hash;
				_xor ^= hash;
			}
		}

		public readonly int Complete() => HashCode.Combine(_count, _sum, _sumOfSquares, _xor);

		private static uint Mix(uint value)
		{
			unchecked
			{
				value ^= value >> 16;
				value *= 0x7FEB352D;
				value ^= value >> 15;
				value *= 0x846CA68B;
				return value ^ (value >> 16);
			}
		}
	}

    public static int CountSelectedFilesUpToLimit(
        IReadOnlySet<string> selectedPaths,
        TreeNodeDescriptor treeRoot,
        int maxCount,
        bool ensureExists = true)
    {
        if (maxCount <= 0)
            return 0;

        var uniquePaths = new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer);
        ProjectTreeSelectionProjection.CollectSelectedFilePaths(
            treeRoot,
            selectedPaths,
            uniquePaths,
            maxCount,
            ensureExists);
        return uniquePaths.Count;
    }

    public static List<string> BuildOrderedSelectedFilePaths(
        IReadOnlySet<string> selectedPaths,
        TreeNodeDescriptor treeRoot,
        bool ensureExists = true) =>
        ProjectTreeSelectionProjection.BuildOrderedSelectedFilePaths(treeRoot, selectedPaths, ensureExists);

	public static List<string> BuildOrderedSelectedFilePathsWithCancellation(
		IReadOnlySet<string> selectedPaths,
		TreeNodeDescriptor treeRoot,
		bool ensureExists,
		CancellationToken cancellationToken) =>
		ProjectTreeSelectionProjection.BuildOrderedSelectedFilePathsWithCancellation(
			treeRoot,
			selectedPaths,
			ensureExists,
			cancellationToken);

    public static List<string> BuildOrderedAllFilePaths(TreeNodeDescriptor treeRoot)
		=> BuildOrderedAllFilePathsWithCancellation(treeRoot, CancellationToken.None);

	public static List<string> BuildOrderedAllFilePathsWithCancellation(
		TreeNodeDescriptor treeRoot,
		CancellationToken cancellationToken)
    {
		cancellationToken.ThrowIfCancellationRequested();
        // Keep a path-based uniqueness pass even though runtime trees should already be unique.
        // Tests intentionally synthesize case-variant nodes to verify cross-platform comparer semantics.
        var uniquePaths = new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer);
        var stack = new Stack<TreeNodeDescriptor>();
        stack.Push(treeRoot);

        while (stack.Count > 0)
        {
			cancellationToken.ThrowIfCancellationRequested();
            var node = stack.Pop();
            if (!node.IsDirectory)
            {
                uniquePaths.Add(node.FullPath);
                continue;
            }

            for (var index = node.Children.Count - 1; index >= 0; index--)
			{
				cancellationToken.ThrowIfCancellationRequested();
                stack.Push(node.Children[index]);
			}
        }

        var orderedPaths = new List<string>(uniquePaths.Count);
        orderedPaths.AddRange(uniquePaths);
		CancellationAwareSort.Sort(
			orderedPaths,
			ProjectTreePathIdentity.CanonicalComparer,
			cancellationToken);
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
