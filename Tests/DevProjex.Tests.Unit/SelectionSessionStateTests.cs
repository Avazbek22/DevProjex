using DevProjex.Application.Context;
using DevProjex.Application.Models;

namespace DevProjex.Tests.Unit;

public sealed class SelectionSessionStateTests
{
    [Fact]
    public void SelectionOptionStateCache_UpdateFromVisibleOptions_PreservesHiddenFullState()
    {
        var cache = new SelectionOptionStateCache(StringComparer.OrdinalIgnoreCase);
        cache.RestoreProfile(
            selectedNames: [".cs"],
            optionStates: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [".cs"] = true,
                [".csv"] = false,
                [".hidden"] = false
            });

        cache.UpdateFromVisibleOptionStates(
        [
            (".cs", true),
            (".json", true)
        ]);

        var states = cache.SnapshotOptionStatesOrNull(suppressLegacySelectedOnlyState: true);
        Assert.NotNull(states);
        Assert.True(states![".cs"]);
        Assert.True(states[".json"]);
        Assert.False(states[".csv"]);
        Assert.False(states[".hidden"]);
        Assert.Equal(new[] { ".cs", ".json" }, cache.SelectedNames.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void SelectionOptionStateCache_LegacyHiddenSelectionSurvivesVisibleEditAndLocalProfileResolution()
    {
        var cache = new SelectionOptionStateCache(StringComparer.OrdinalIgnoreCase);
        cache.RestoreProfile(selectedNames: [".md"], optionStates: null);

        cache.UpdateFromVisibleOptionStates([(".cs", false)]);

        var profile = ProjectSelectionProfileBuilder.Create(
            visibleExtensions: [new SelectionOption(".cs", false)],
            visibleIgnoreOptions: [],
            cachedExtensionStates: cache.SnapshotOptionStatesOrNull(suppressLegacySelectedOnlyState: false),
            cachedIgnoreOptionStates: null,
            selectedIgnoreOptions: [],
            extensionComparer: StringComparer.OrdinalIgnoreCase);
        var resolved = ProjectSelectionAdapter.FromLegacyProfile(profile, ProjectProfileReference.Local);

        Assert.True(profile.ExtensionStates![".md"]);
        Assert.Contains(".md", resolved.Extensions!);
    }

    [Fact]
    public void SelectionOptionStateCache_UpdateFromVisibleOptions_ReusesSelectedNamesSet()
    {
        var cache = new SelectionOptionStateCache(StringComparer.OrdinalIgnoreCase);
        cache.UpdateFromVisibleOptions([new(".cs", true), new(".md", false)]);
        var selectedNames = cache.SelectedNames;

        cache.UpdateFromVisibleOptions([new(".cs", false), new(".md", true)]);

        Assert.Same(selectedNames, cache.SelectedNames);
        Assert.Equal([".md"], cache.SelectedNames);
    }

    [Fact]
    public void SelectionOptionStateCache_TryUpdateKnownOption_UpdatesInPlaceAndPreservesHiddenState()
    {
        var cache = new SelectionOptionStateCache(StringComparer.OrdinalIgnoreCase);
        cache.RestoreProfile(
            selectedNames: [".cs", ".hidden-selected"],
            optionStates: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [".cs"] = true,
                [".hidden-selected"] = true,
                [".hidden"] = false
            });
        var selectedNames = cache.SelectedNames;
        var optionStates = cache.OptionStates;
        cache.MarkIncomplete();

        var updated = cache.TryUpdateKnownOption(".CS", isChecked: false, out var previousState);

        Assert.True(updated);
        Assert.True(previousState);
        Assert.True(cache.HasFullState);
        Assert.Same(selectedNames, cache.SelectedNames);
        Assert.Same(optionStates, cache.OptionStates);
        Assert.DoesNotContain(".cs", cache.SelectedNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(".hidden-selected", cache.SelectedNames, StringComparer.OrdinalIgnoreCase);
        Assert.False(cache.OptionStates[".cs"]);
        Assert.True(cache.OptionStates[".hidden-selected"]);
        Assert.False(cache.OptionStates[".hidden"]);
        Assert.True(cache.TryUpdateKnownOption(".cs", isChecked: true, out previousState));
        Assert.False(previousState);
        Assert.Contains(".cs", cache.SelectedNames, StringComparer.OrdinalIgnoreCase);
        Assert.True(cache.OptionStates[".cs"]);
        Assert.False(cache.TryUpdateKnownOption(".new", isChecked: true, out _));
        Assert.DoesNotContain(".new", cache.OptionStates.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectionOptionStateCache_TryUpdateKnownOption_CompletesHiddenSelectedNamesFromPartialState()
    {
        var cache = new SelectionOptionStateCache(StringComparer.OrdinalIgnoreCase);
        cache.RestoreProfile(
            selectedNames: [".cs", ".md", ".json"],
            optionStates: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [".cs"] = true,
                [".json"] = false
            });
        cache.MarkIncomplete();

        Assert.True(cache.TryUpdateKnownOption(".cs", isChecked: false, out var previousState));

        Assert.True(previousState);
        Assert.True(cache.HasFullState);
        Assert.False(cache.OptionStates[".cs"]);
        Assert.True(cache.OptionStates[".md"]);
        Assert.False(cache.OptionStates[".json"]);
    }

    [Fact]
    public void SelectionOptionStateCache_LegacyProfile_DoesNotPretendToHaveFullState()
    {
        var cache = new SelectionOptionStateCache(PathComparer.Default);
        cache.RestoreProfile(
            selectedNames: ["src"],
            optionStates: null);

        Assert.Null(cache.SnapshotOptionStatesOrNull(suppressLegacySelectedOnlyState: true));

        var nonProfileSnapshot = cache.SnapshotOptionStatesOrNull(suppressLegacySelectedOnlyState: false);
        Assert.NotNull(nonProfileSnapshot);
        Assert.Empty(nonProfileSnapshot!);
        Assert.Contains("src", cache.SelectedNames);
    }

    [Fact]
    public void ProjectSelectionProfileBuilder_Create_PersistsOnlyDesktopExtensionAndExclusionState()
    {
        var profile = ProjectSelectionProfileBuilder.Create(
            visibleExtensions:
            [
                new(".cs", true),
                new(".json", false)
            ],
            visibleIgnoreOptions:
            [
                new(IgnoreOptionId.SmartIgnore, true),
                new(IgnoreOptionId.EmptyFiles, false)
            ],
            cachedExtensionStates: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [".csv"] = false
            },
            cachedIgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
            {
                [IgnoreOptionId.DotFolders] = false
            },
            selectedIgnoreOptions: [IgnoreOptionId.SmartIgnore],
            extensionComparer: StringComparer.OrdinalIgnoreCase);

        Assert.Empty(profile.SelectedRootFolders);
        Assert.Null(profile.RootFolderStates);
        Assert.Equal([".cs"], profile.SelectedExtensions);
        Assert.Equal([IgnoreOptionId.SmartIgnore], profile.SelectedIgnoreOptions);

        var extensionStates = profile.ExtensionStates;
        Assert.NotNull(extensionStates);
        Assert.True(extensionStates![".cs"]);
        Assert.False(extensionStates[".json"]);
        Assert.False(extensionStates[".csv"]);

        var ignoreStates = profile.IgnoreOptionStates;
        Assert.NotNull(ignoreStates);
        Assert.True(ignoreStates![IgnoreOptionId.SmartIgnore]);
        Assert.False(ignoreStates[IgnoreOptionId.EmptyFiles]);
        Assert.False(ignoreStates[IgnoreOptionId.DotFolders]);
    }

    [Fact]
    public void ProjectSelectionProfileBuilder_Clone_DoesNotShareMutableState()
    {
        var profile = new ProjectSelectionProfile(
            SelectedRootFolders: ["src"],
            SelectedExtensions: [".cs"],
            SelectedIgnoreOptions: [IgnoreOptionId.SmartIgnore],
            RootFolderStates: new Dictionary<string, bool>(PathComparer.Default) { ["src"] = true },
            ExtensionStates: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { [".cs"] = true },
            IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool> { [IgnoreOptionId.SmartIgnore] = true });

        var clone = ProjectSelectionProfileBuilder.Clone(profile);
        ((Dictionary<string, bool>)profile.RootFolderStates!)["docs"] = false;
        ((Dictionary<string, bool>)profile.ExtensionStates!)[".csv"] = false;
        ((Dictionary<IgnoreOptionId, bool>)profile.IgnoreOptionStates!)[IgnoreOptionId.EmptyFiles] = false;

        Assert.DoesNotContain("docs", clone.RootFolderStates!.Keys);
        Assert.DoesNotContain(".csv", clone.ExtensionStates!.Keys);
        Assert.DoesNotContain(IgnoreOptionId.EmptyFiles, clone.IgnoreOptionStates!.Keys);
    }
}
