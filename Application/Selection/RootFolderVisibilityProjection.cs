namespace DevProjex.Application.Selection;

public static class RootFolderVisibilityProjection
{
    public static IReadOnlyList<string> ApplyScopedControllerRules(
        string rootPath,
        IReadOnlyList<string> candidateRootFolders,
        IgnoreRules rules,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (candidateRootFolders.Count == 0 || (!rules.IsGitIgnoreTraversalEnabled && !rules.UseSmartIgnore))
            return candidateRootFolders;

        List<string>? visibleRootFolders = null;
        var gitIgnoreContext = rules.CreateGitIgnoreScanContext(rootPath);
        for (var index = 0; index < candidateRootFolders.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = candidateRootFolders[index];
            var fullPath = Path.Combine(rootPath, name);
            var gitIgnore = rules.IsGitIgnoreTraversalEnabled
                ? gitIgnoreContext.Evaluate(fullPath, name, isDirectory: true, name)
                : IgnoreRules.GitIgnoreEvaluation.NotIgnored;
            var isControllerIgnored =
                (gitIgnore.IsIgnored && !gitIgnore.ShouldTraverseIgnoredDirectory) ||
                rules.IsSmartIgnoredDirectory(fullPath, name);
            if (!isControllerIgnored)
            {
                visibleRootFolders?.Add(name);
                continue;
            }

            if (visibleRootFolders is null)
            {
                visibleRootFolders = new List<string>(candidateRootFolders.Count - 1);
                for (var visibleIndex = 0; visibleIndex < index; visibleIndex++)
                    visibleRootFolders.Add(candidateRootFolders[visibleIndex]);
            }
        }

        return visibleRootFolders ?? candidateRootFolders;
    }
}
