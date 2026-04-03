using DevProjex.Application.Models;

namespace DevProjex.Tests.Unit;

public sealed class SelectionSyncCoordinatorRootFolderRefreshMatrixTests
{
    [Theory]
    [MemberData(nameof(ExtensionsFallbackCases))]
    public void ApplyMissingProfileSelectionsFallbackToExtensions_Matrix(
        int caseId,
        string preparedMode,
        bool hasSavedSelections,
        string optionKind,
        bool expectedFallback)
    {
        var cachedSelections = hasSavedSelections
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "missing" }
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = CreateExtensionOptions(optionKind);

        var actual = SelectionSyncCoordinatorPolicy.ApplyMissingProfileSelectionsFallbackToExtensions(
            ParsePreparedSelectionMode(preparedMode),
            cachedSelections,
            options);

        Assert.Equal(expectedFallback, !ReferenceEquals(options, actual));
        if (expectedFallback)
        {
            Assert.All(actual, option => Assert.True(option.IsChecked));
            Assert.Equal(options.Select(option => option.Name), actual.Select(option => option.Name));
        }
        else
        {
            Assert.Same(options, actual);
        }

        Assert.True(caseId >= 0);
    }

    [Theory]
    [MemberData(nameof(RootFallbackCases))]
    public void ApplyMissingProfileSelectionsFallbackToRootFolders_Matrix(
        int caseId,
        string preparedMode,
        bool hasSavedSelections,
        bool hasAnyCheckedOption,
        bool ignoreDotFolders,
        bool includeSmartIgnoredFolder,
        bool expectedFallback)
    {
        var cachedSelections = hasSavedSelections
            ? new HashSet<string>(PathComparer.Default) { "missing-root" }
            : new HashSet<string>(PathComparer.Default);

        var scannedRootFolders = new[] { ".git", "src", "node_modules", "docs" };
        var options = scannedRootFolders
            .Select((name, index) => new SelectionOption(name, hasAnyCheckedOption && index == 0))
            .ToList();

        var ignoreRules = CreateIgnoreRules(ignoreDotFolders, includeSmartIgnoredFolder);
        var filterSelectionService = new FilterOptionSelectionService();

        var actual = SelectionSyncCoordinatorPolicy.ApplyMissingProfileSelectionsFallbackToRootFolders(
            ParsePreparedSelectionMode(preparedMode),
            cachedSelections,
            options,
            scannedRootFolders,
            ignoreRules,
            filterSelectionService,
            new HashSet<string>(PathComparer.Default));

        Assert.Equal(expectedFallback, !ReferenceEquals(options, actual));
        if (expectedFallback)
        {
            var expected = filterSelectionService.BuildRootFolderOptions(
                scannedRootFolders,
                new HashSet<string>(PathComparer.Default),
                ignoreRules,
                hasPreviousSelections: false);

            Assert.Equal(expected.Select(option => option.Name), actual.Select(option => option.Name));
            Assert.Equal(expected.Select(option => option.IsChecked), actual.Select(option => option.IsChecked));
        }
        else
        {
            Assert.Same(options, actual);
        }

        Assert.True(caseId >= 0);
    }

    [Theory]
    [MemberData(nameof(IgnoreFallbackCases))]
    public void ShouldUseIgnoreDefaultFallback_Matrix(
        int caseId,
        string preparedMode,
        string previousSelectionKind,
        string visibleOptionsKind,
        bool expected)
    {
        var previousSelections = CreatePreviousSelections(previousSelectionKind);
        var visibleOptions = CreateVisibleIgnoreOptions(visibleOptionsKind);

        var actual = SelectionSyncCoordinatorPolicy.ShouldUseIgnoreDefaultFallback(
            ParsePreparedSelectionMode(preparedMode),
            visibleOptions,
            previousSelections);

        Assert.Equal(expected, actual);
        Assert.True(caseId >= 0);
    }

    public static IEnumerable<object[]> ExtensionsFallbackCases()
    {
        var modes = new[] { "None", "Defaults", "Profile" };
        var cacheVariants = new[] { false, true };
        var optionKinds = new[] { "empty", "unchecked_pair", "single_checked", "all_checked" };
        var caseId = 0;

        foreach (var mode in modes)
        {
            foreach (var hasCache in cacheVariants)
            {
                foreach (var optionKind in optionKinds)
                {
                    var expectedFallback = mode == "Profile" &&
                                           hasCache &&
                                           optionKind == "unchecked_pair";
                    yield return
                    [
                        caseId++,
                        mode,
                        hasCache,
                        optionKind,
                        expectedFallback
                    ];
                }
            }
        }
    }

    public static IEnumerable<object[]> RootFallbackCases()
    {
        var modes = new[] { "None", "Defaults", "Profile" };
        var cacheVariants = new[] { false, true };
        var checkedVariants = new[] { false, true };
        var dotIgnoreVariants = new[] { false, true };
        var smartIgnoreVariants = new[] { false, true };
        var caseId = 0;

        foreach (var mode in modes)
        {
            foreach (var hasCache in cacheVariants)
            {
                foreach (var hasChecked in checkedVariants)
                {
                    foreach (var ignoreDot in dotIgnoreVariants)
                    {
                        foreach (var smartIgnore in smartIgnoreVariants)
                        {
                            var expectedFallback = mode == "Profile" && hasCache && !hasChecked;
                            yield return
                            [
                                caseId++,
                                mode,
                                hasCache,
                                hasChecked,
                                ignoreDot,
                                smartIgnore,
                                expectedFallback
                            ];
                        }
                    }
                }
            }
        }
    }

    public static IEnumerable<object[]> IgnoreFallbackCases()
    {
        var modes = new[] { "None", "Defaults", "Profile" };
        var previousKinds = new[] { "none", "single", "multi" };
        var visibleKinds = new[] { "empty", "contains_single", "contains_multi", "contains_none" };
        var caseId = 0;

        foreach (var mode in modes)
        {
            foreach (var previousKind in previousKinds)
            {
                foreach (var visibleKind in visibleKinds)
                {
                    var previous = CreatePreviousSelections(previousKind);
                    var visible = CreateVisibleIgnoreOptions(visibleKind);
                    var expected = mode == "Profile" &&
                                   previous.Count > 0 &&
                                   visible.Count > 0 &&
                                   visible.All(option => !previous.Contains(option.Id));

                    yield return
                    [
                        caseId++,
                        mode,
                        previousKind,
                        visibleKind,
                        expected
                    ];
                }
            }
        }
    }

    private static List<SelectionOption> CreateExtensionOptions(string optionKind)
    {
        return optionKind switch
        {
            "empty" => [],
            "unchecked_pair" => [new SelectionOption(".cs", false), new SelectionOption(".md", false)],
            "single_checked" => [new SelectionOption(".cs", true), new SelectionOption(".md", false)],
            "all_checked" => [new SelectionOption(".cs", true), new SelectionOption(".md", true)],
            _ => throw new ArgumentOutOfRangeException(nameof(optionKind), optionKind, "Unknown option kind.")
        };
    }

    private static IgnoreRules CreateIgnoreRules(bool ignoreDotFolders, bool includeSmartIgnoredFolder)
    {
        var smartIgnoredFolders = includeSmartIgnoredFolder
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules" }
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return new IgnoreRules(
            IgnoreHiddenFolders: false,
            IgnoreHiddenFiles: false,
            IgnoreDotFolders: ignoreDotFolders,
            IgnoreDotFiles: false,
            SmartIgnoredFolders: smartIgnoredFolders,
            SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static HashSet<IgnoreOptionId> CreatePreviousSelections(string kind)
    {
        return kind switch
        {
            "none" => [],
            "single" => [IgnoreOptionId.HiddenFolders],
            "multi" => [IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore],
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown previous selection kind.")
        };
    }

    private static List<IgnoreOptionDescriptor> CreateVisibleIgnoreOptions(string kind)
    {
        return kind switch
        {
            "empty" => [],
            "contains_single" =>
            [
                new IgnoreOptionDescriptor(IgnoreOptionId.HiddenFolders, "Hidden folders", false),
                new IgnoreOptionDescriptor(IgnoreOptionId.DotFiles, "Dot files", false)
            ],
            "contains_multi" =>
            [
                new IgnoreOptionDescriptor(IgnoreOptionId.SmartIgnore, "Smart ignore", false),
                new IgnoreOptionDescriptor(IgnoreOptionId.DotFolders, "Dot folders", false)
            ],
            "contains_none" =>
            [
                new IgnoreOptionDescriptor(IgnoreOptionId.DotFolders, "Dot folders", false),
                new IgnoreOptionDescriptor(IgnoreOptionId.HiddenFiles, "Hidden files", false)
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown visible options kind.")
        };
    }

    private static PreparedSelectionMode ParsePreparedSelectionMode(string modeName) =>
        Enum.Parse<PreparedSelectionMode>(modeName);
}
