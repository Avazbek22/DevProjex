using DevProjex.Avalonia.Coordinators;
using DevProjex.Application.Models;

namespace DevProjex.Tests.Unit;

public sealed class ProjectLoadIgnoreRulesResolverTests
{
    [Fact]
    public void Resolve_WithEffectiveRules_ReusesSnapshotRulesWithoutFallback()
    {
        var effectiveRules = CreateRules(ignoreDotFolders: true);
        var snapshot = CreateSnapshot(effectiveRules);
        var fallbackCalls = 0;

        var result = ProjectLoadIgnoreRulesResolver.Resolve(
            snapshot,
            _ =>
            {
                fallbackCalls++;
                return CreateRules(ignoreDotFolders: false);
            });

        Assert.Same(effectiveRules, result);
        Assert.Equal(0, fallbackCalls);
    }

    [Fact]
    public void Resolve_WithoutEffectiveRules_UsesResolvedIgnoreSelectionOnce()
    {
        var snapshot = CreateSnapshot(effectiveRules: null) with
        {
            SelectedIgnoreOptions = new HashSet<IgnoreOptionId>
            {
                IgnoreOptionId.DotFolders
            }
        };
        IReadOnlyCollection<IgnoreOptionId>? capturedSelection = null;
        var fallbackRules = CreateRules(ignoreDotFolders: true);

        var result = ProjectLoadIgnoreRulesResolver.Resolve(
            snapshot,
            selection =>
            {
                capturedSelection = selection;
                return fallbackRules;
            });

        Assert.Same(fallbackRules, result);
        Assert.NotNull(capturedSelection);
        Assert.Equal([IgnoreOptionId.DotFolders], capturedSelection);
    }

    private static SelectionRefreshSnapshot CreateSnapshot(IgnoreRules? effectiveRules) =>
        new(
            RootOptions: [new SelectionOption("src", true)],
            ExtensionOptions: [new SelectionOption(".cs", true)],
            IgnoreOptions:
            [
                new ResolvedIgnoreOptionState(
                    IgnoreOptionId.DotFolders,
                    "Dot folders",
                    DefaultChecked: true,
                    IsChecked: true)
            ],
            ExtensionlessEntriesCount: 0,
            HasIgnoreOptionCounts: true,
            IgnoreOptionCounts: new IgnoreOptionCounts(DotFolders: 1),
            ControllerImpactCounts: IgnoreControllerImpactCounts.Empty,
            IgnoreOptionStateCache: new Dictionary<IgnoreOptionId, bool>
            {
                [IgnoreOptionId.DotFolders] = true
            },
            RootAccessDenied: false,
            HadAccessDenied: false,
            EffectiveRules: effectiveRules);

    private static IgnoreRules CreateRules(bool ignoreDotFolders) =>
        new(
            IgnoreHiddenFolders: false,
            IgnoreHiddenFiles: false,
            IgnoreDotFolders: ignoreDotFolders,
            IgnoreDotFiles: false,
            SmartIgnoredFolders: new HashSet<string>(),
            SmartIgnoredFiles: new HashSet<string>());
}
