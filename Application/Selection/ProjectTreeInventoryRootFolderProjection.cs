using DevProjex.Application.Models;

namespace DevProjex.Application.Selection;

public static class ProjectTreeInventoryRootFolderProjection
{
    public static IReadOnlyList<SelectionOption> RemoveCheckedRootsWithoutVisibleStructure(
        ProjectWorkspaceScanBreakdown? breakdown,
        IReadOnlyList<SelectionOption> rootOptions,
        out IReadOnlySet<string>? emptyFolderOwnedRemovedRoots,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        emptyFolderOwnedRemovedRoots = null;
        if (rootOptions.Count == 0 || breakdown is null)
            return rootOptions;

        List<SelectionOption>? projectedOptions = null;
        HashSet<string>? emptyFolderOwnedRoots = null;
        for (var optionIndex = 0; optionIndex < rootOptions.Count; optionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var option = rootOptions[optionIndex];
            var keepOption = !option.IsChecked || ShouldKeepCheckedRoot(breakdown, option.Name);
            if (keepOption)
            {
                projectedOptions?.Add(option);
                continue;
            }

            if (breakdown.SelectedRoots.TryGetValue(option.Name, out var rootSnapshot) &&
                rootSnapshot.IgnoreSection.IsTreeStructureHiddenByEmptyFolders)
            {
                emptyFolderOwnedRoots ??= new HashSet<string>(PathComparer.Default);
                emptyFolderOwnedRoots.Add(option.Name);
            }

            if (projectedOptions is null)
            {
                projectedOptions = new List<SelectionOption>(rootOptions.Count - 1);
                for (var preservedIndex = 0; preservedIndex < optionIndex; preservedIndex++)
                    projectedOptions.Add(rootOptions[preservedIndex]);
            }
        }

        emptyFolderOwnedRemovedRoots = emptyFolderOwnedRoots;
        return projectedOptions ?? rootOptions;
    }

    private static bool ShouldKeepCheckedRoot(ProjectWorkspaceScanBreakdown breakdown, string rootName)
    {
        if (!breakdown.SelectedRoots.TryGetValue(rootName, out var rootSnapshot))
        {
            return breakdown.RootEnumerationAccessDenied || breakdown.RootEnumerationHadAccessDenied;
        }

        if (rootSnapshot.RootAccessDenied || rootSnapshot.HadAccessDenied)
            return true;

        return rootSnapshot.IgnoreSection.HasVisibleTreeStructure != false;
    }

    public static IReadOnlyList<SelectionOption> RemoveCheckedRootsWithoutVisibleStructure(
        ProjectTreeInventorySnapshot inventory,
        IReadOnlyList<SelectionOption> rootOptions,
        IReadOnlySet<string> allowedExtensions,
        IgnoreRules rules,
        CancellationToken cancellationToken = default) =>
        RemoveCheckedRootsWithoutVisibleStructure(
            inventory,
            rootOptions,
            allowedExtensions,
            rules,
            out _,
            cancellationToken);

    public static IReadOnlyList<SelectionOption> RemoveCheckedRootsWithoutVisibleStructure(
        ProjectTreeInventorySnapshot inventory,
        IReadOnlyList<SelectionOption> rootOptions,
        IReadOnlySet<string> allowedExtensions,
        IgnoreRules rules,
        out IReadOnlySet<string>? emptyFolderOwnedRemovedRoots,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        emptyFolderOwnedRemovedRoots = null;
        if (rootOptions.Count == 0 || inventory.Entries.Count == 0 || inventory.RootAccessDenied)
            return rootOptions;

        ref readonly var inventoryRoot = ref inventory.GetEntryRef(0);
        var rootEntryIndexes = new Dictionary<string, int>(inventoryRoot.ChildCount, PathComparer.Default);
        for (var childOffset = 0; childOffset < inventoryRoot.ChildCount; childOffset++)
        {
            var childIndex = inventoryRoot.FirstChildIndex + childOffset;
            ref readonly var child = ref inventory.GetEntryRef(childIndex);
            if (child.IsDirectory)
                rootEntryIndexes[child.Name] = childIndex;
        }

        var gitIgnoreContext = rules.CreateGitIgnoreScanContext(
            inventoryRoot.FullPath,
            inventory.DiscoveredGitIgnoreMatchers,
            inventory.DiscoveredGitTrackedPathIndexes);
        var pendingDirectories = new Stack<int>();
        List<SelectionOption>? projectedOptions = null;
        HashSet<string>? emptyFolderOwnedRoots = null;
        for (var optionIndex = 0; optionIndex < rootOptions.Count; optionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var option = rootOptions[optionIndex];
            var projection = !option.IsChecked
                ? RootProjection.Visible
                : rootEntryIndexes.TryGetValue(option.Name, out var rootEntryIndex)
                    ? ClassifySelectableStructure(
                        inventory,
                        rootEntryIndex,
                        allowedExtensions,
                        rules,
                        gitIgnoreContext,
                        pendingDirectories,
                        cancellationToken)
                    : RootProjection.HiddenByOtherRules;
            var keepOption = projection == RootProjection.Visible;
            if (keepOption)
            {
                projectedOptions?.Add(option);
                continue;
            }

            if (projection == RootProjection.HiddenByEmptyFolders)
            {
                emptyFolderOwnedRoots ??= new HashSet<string>(PathComparer.Default);
                emptyFolderOwnedRoots.Add(option.Name);
            }

            if (projectedOptions is null)
            {
                projectedOptions = new List<SelectionOption>(rootOptions.Count - 1);
                for (var preservedIndex = 0; preservedIndex < optionIndex; preservedIndex++)
                    projectedOptions.Add(rootOptions[preservedIndex]);
            }
        }

        emptyFolderOwnedRemovedRoots = emptyFolderOwnedRoots;
        return projectedOptions ?? rootOptions;
    }

    private static RootProjection ClassifySelectableStructure(
        ProjectTreeInventorySnapshot inventory,
        int rootEntryIndex,
        IReadOnlySet<string> allowedExtensions,
        IgnoreRules rules,
        IgnoreRules.GitIgnoreScanContext gitIgnoreContext,
        Stack<int> pendingDirectories,
        CancellationToken cancellationToken)
    {
        pendingDirectories.Clear();
        pendingDirectories.Push(rootEntryIndex);
        var hasStructureHiddenByEmptyFolders = false;

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryIndex = pendingDirectories.Pop();
            ref readonly var directory = ref inventory.GetEntryRef(directoryIndex);
            var gitIgnore = rules.IsGitIgnoreTraversalEnabled
                ? gitIgnoreContext.Evaluate(
                    directory.FullPath,
                    directory.RelativePath,
                    isDirectory: true,
                    directory.Name)
                : IgnoreRules.GitIgnoreEvaluation.NotIgnored;
            if (IgnoreDecisionEngine.EvaluateDirectory(
                    directory.FullPath,
                    directory.Name,
                    directory.IsHidden,
                    rules,
                    gitIgnore).IsIgnored)
            {
                continue;
            }

            if (directory.IsAccessDenied)
                return RootProjection.Visible;

            // A traversable Git-ignored directory is only a path to possible negated descendants.
            // It is not an EmptyFolders-owned root unless a normally visible descendant exists.
            var isTraversedGitIgnoredDirectory =
                gitIgnore.IsIgnored && gitIgnore.ShouldTraverseIgnoredDirectory;
            if (!rules.IgnoreEmptyFolders && !isTraversedGitIgnoredDirectory)
                return RootProjection.Visible;
            if (!isTraversedGitIgnoredDirectory)
                hasStructureHiddenByEmptyFolders = true;
			var shouldApplySmartIgnoreToFiles = rules.ShouldApplySmartIgnore(
				directory.FullPath,
				isDirectory: true);

            for (var childOffset = 0; childOffset < directory.ChildCount; childOffset++)
            {
                var childIndex = directory.FirstChildIndex + childOffset;
                ref readonly var child = ref inventory.GetEntryRef(childIndex);
                if (child.IsDirectory)
                {
                    pendingDirectories.Push(childIndex);
                    continue;
                }

                var fileGitIgnore = rules.IsGitIgnoreTraversalEnabled
                    ? gitIgnoreContext.Evaluate(
                        child.FullPath,
                        child.RelativePath,
                        isDirectory: false,
                        child.Name)
                    : IgnoreRules.GitIgnoreEvaluation.NotIgnored;
                var fileDecision = IgnoreDecisionEngine.EvaluateFile(
                    child.FullPath,
                    child.Name,
                    child.IsHidden,
                    child.Length,
                    rules,
					shouldApplySmartIgnoreToFiles,
                    fileGitIgnore);
                if (!fileDecision.IsIgnored && IsAllowedFile(child.Name, allowedExtensions))
                    return RootProjection.Visible;
            }
        }

        return hasStructureHiddenByEmptyFolders
            ? RootProjection.HiddenByEmptyFolders
            : RootProjection.HiddenByOtherRules;
    }

    private static bool IsAllowedFile(string fileName, IReadOnlySet<string> allowedExtensions)
    {
        if (IgnoreRuleSemantics.IsExtensionlessFileName(fileName))
            return true;

        if (allowedExtensions.Count == 0)
            return false;

        var extension = Path.GetExtension(fileName.AsSpan());
        if (extension.IsEmpty)
            return false;

        if (allowedExtensions is HashSet<string> hashSet &&
            hashSet.TryGetAlternateLookup<ReadOnlySpan<char>>(out var lookup))
        {
            return lookup.Contains(extension);
        }

        return allowedExtensions.Contains(extension.ToString());
    }

    private enum RootProjection
    {
        Visible,
        HiddenByEmptyFolders,
        HiddenByOtherRules
    }
}
