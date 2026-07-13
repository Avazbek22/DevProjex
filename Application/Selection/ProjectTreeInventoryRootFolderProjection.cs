using DevProjex.Application.Models;

namespace DevProjex.Application.Selection;

public static class ProjectTreeInventoryRootFolderProjection
{
    public static IReadOnlyList<SelectionOption> RemoveCheckedRootsWithoutVisibleStructure(
        ProjectTreeInventorySnapshot inventory,
        IReadOnlyList<SelectionOption> rootOptions,
        IReadOnlySet<string> allowedExtensions,
        IgnoreRules rules,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

        var gitIgnoreContext = rules.CreateGitIgnoreScanContext(inventoryRoot.FullPath);
        var pendingDirectories = new Stack<int>();
        List<SelectionOption>? projectedOptions = null;
        for (var optionIndex = 0; optionIndex < rootOptions.Count; optionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var option = rootOptions[optionIndex];
            var keepOption = !option.IsChecked ||
                             (rootEntryIndexes.TryGetValue(option.Name, out var rootEntryIndex) &&
                              HasSelectableStructure(
                                  inventory,
                                  rootEntryIndex,
                                  allowedExtensions,
                                  rules,
                                  gitIgnoreContext,
                                  pendingDirectories,
                                  cancellationToken));
            if (keepOption)
            {
                projectedOptions?.Add(option);
                continue;
            }

            if (projectedOptions is null)
            {
                projectedOptions = new List<SelectionOption>(rootOptions.Count - 1);
                for (var preservedIndex = 0; preservedIndex < optionIndex; preservedIndex++)
                    projectedOptions.Add(rootOptions[preservedIndex]);
            }
        }

        return projectedOptions ?? rootOptions;
    }

    private static bool HasSelectableStructure(
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

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryIndex = pendingDirectories.Pop();
            ref readonly var directory = ref inventory.GetEntryRef(directoryIndex);
            var gitIgnore = rules.UseGitIgnore
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
                return true;

            if (!rules.IgnoreEmptyFolders)
                return true;

            for (var childOffset = 0; childOffset < directory.ChildCount; childOffset++)
            {
                var childIndex = directory.FirstChildIndex + childOffset;
                ref readonly var child = ref inventory.GetEntryRef(childIndex);
                if (child.IsDirectory)
                {
                    pendingDirectories.Push(childIndex);
                    continue;
                }

                var fileGitIgnore = rules.UseGitIgnore
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
                    rules.ShouldApplySmartIgnore(directory.FullPath, isDirectory: true),
                    fileGitIgnore);
                if (!fileDecision.IsIgnored && IsAllowedFile(child.Name, allowedExtensions))
                    return true;
            }
        }

        return false;
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
}
