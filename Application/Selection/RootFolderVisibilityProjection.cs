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
        if (candidateRootFolders.Count == 0 || !rules.UseSmartIgnore)
            return candidateRootFolders;

        // The filesystem scanner already applies index-aware Git rules before producing
        // candidate roots. This second projection exists only for Smart Ignore because its
        // project scopes can change after the final root selection becomes known.
        List<string>? visibleRootFolders = null;
        for (var index = 0; index < candidateRootFolders.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = candidateRootFolders[index];
            var fullPath = Path.Combine(rootPath, name);
            if (!rules.IsSmartIgnoredDirectory(fullPath, name))
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
