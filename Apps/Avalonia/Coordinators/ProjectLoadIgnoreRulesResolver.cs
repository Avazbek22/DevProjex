namespace DevProjex.Avalonia.Coordinators;

internal static class ProjectLoadIgnoreRulesResolver
{
    public static IgnoreRules Resolve(
        SelectionRefreshSnapshot snapshot,
        Func<IReadOnlyCollection<IgnoreOptionId>, IgnoreRules> fallback)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(fallback);

        return snapshot.EffectiveRules ?? fallback(snapshot.EffectiveIgnoreOptions);
    }
}
