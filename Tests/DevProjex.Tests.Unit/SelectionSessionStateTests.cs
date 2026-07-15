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

        cache.UpdateFromVisibleOptions(
        [
            new(".cs", true),
            new(".json", true)
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
    public void ProjectSelectionSessionState_ApplyProfileAndReset_OwnsPreparedLifecycle()
    {
        var session = new ProjectSelectionSessionState();
        var profile = new ProjectSelectionProfile(
            SelectedRootFolders: ["src"],
            SelectedExtensions: [".cs"],
            SelectedIgnoreOptions: [IgnoreOptionId.SmartIgnore],
            RootFolderStates: new Dictionary<string, bool>(PathComparer.Default)
            {
                ["src"] = true,
                ["docs"] = false
            },
            ExtensionStates: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [".cs"] = true,
                [".csv"] = false
            },
            IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
            {
                [IgnoreOptionId.SmartIgnore] = true,
                [IgnoreOptionId.EmptyFiles] = false
            });

        session.ApplyProfile(@"C:\ProjectA", profile);

        Assert.Equal(PreparedSelectionMode.Profile, session.PreparedMode);
        Assert.True(session.HasPreparedSelectionForPath(@"C:\ProjectA"));
        Assert.True(session.RootFolders.IsInitialized);
        Assert.True(session.Extensions.IsInitialized);
        Assert.True(session.IgnoreOptions.IsInitialized);
        Assert.True(session.IgnoreOptionStateCacheIsComplete);
        Assert.False(session.RootFolders.OptionStates["docs"]);
        Assert.False(session.Extensions.OptionStates[".csv"]);
        Assert.False(session.IgnoreOptions.OptionStateCache[IgnoreOptionId.EmptyFiles]);

        session.ConsumePreparedSelectionForPath(@"C:\ProjectA");

        Assert.Equal(PreparedSelectionMode.None, session.PreparedMode);
        Assert.False(session.HasPreparedSelectionForPath(@"C:\ProjectA"));

        session.ResetToDefaultsForProject(@"C:\ProjectB");

        Assert.Equal(PreparedSelectionMode.Defaults, session.PreparedMode);
        Assert.True(session.HasPreparedSelectionForPath(@"C:\ProjectB"));
        Assert.False(session.RootFolders.IsInitialized);
        Assert.False(session.Extensions.IsInitialized);
        Assert.False(session.IgnoreOptions.IsInitialized);
        Assert.False(session.IgnoreOptionStateCacheIsComplete);
    }

    [Fact]
    public void ProjectSelectionProfileBuilder_Create_MergesHiddenStatesWithVisibleSelections()
    {
        var profile = ProjectSelectionProfileBuilder.Create(
            visibleRootFolders:
            [
                new("src", true),
                new("docs", false)
            ],
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
            cachedRootFolderStates: new Dictionary<string, bool>(PathComparer.Default)
            {
                ["hidden-root"] = false
            },
            cachedExtensionStates: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [".csv"] = false
            },
            cachedIgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
            {
                [IgnoreOptionId.DotFolders] = false
            },
            selectedIgnoreOptions: [IgnoreOptionId.SmartIgnore],
            rootFolderComparer: PathComparer.Default,
            extensionComparer: StringComparer.OrdinalIgnoreCase);

        Assert.Equal(["src"], profile.SelectedRootFolders);
        Assert.Equal([".cs"], profile.SelectedExtensions);
        Assert.Equal([IgnoreOptionId.SmartIgnore], profile.SelectedIgnoreOptions);

        var rootStates = profile.RootFolderStates;
        Assert.NotNull(rootStates);
        Assert.True(rootStates!["src"]);
        Assert.False(rootStates["docs"]);
        Assert.False(rootStates["hidden-root"]);

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
