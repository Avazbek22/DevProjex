namespace DevProjex.Tests.Integration;

/// <summary>
/// Integration tests for Git-related localization keys.
/// Verifies that all required localization keys exist in all language files.
///
/// Test categories:
/// - Git menu localization
/// - Git dialog localization
/// - Git progress messages
/// - Git error messages
/// - Consistency across all languages
///
/// IMPORTANT: These tests verify that localization is complete.
/// All languages must have all Git-related keys defined.
/// </summary>
public class GitLocalizationTests
{
    private readonly string _localizationPath;
    private readonly string[] _supportedLanguages = { "en", "ru", "uz", "tg", "kk", "de", "fr", "it" };

    // All required Git localization keys
    private readonly string[] _requiredGitKeys =
    {
        // Menu items
        "Menu.Git.Clone",
        "Menu.Git.Branch",
        "Menu.Git.GetUpdates",

        // Clone dialog
        "Git.Clone.Title",
        "Git.Clone.Description",
        "Git.Clone.UrlPlaceholder",

        // Progress messages
        "Git.Clone.Progress.CheckingGit",
        "Git.Clone.Progress.Cloning",
        "Git.Clone.Progress.Downloading",
        "Git.Clone.Progress.Extracting",
        "Git.Clone.Progress.Preparing",
        "Git.Clone.Progress.SwitchingBranch",

        // Error messages
        "Git.Error.GitNotFound",
        "Git.Error.CloneFailed",
        "Git.Error.InvalidUrl",
        "Git.Error.NetworkError",
        "Git.Error.NoInternetConnection",
        "Git.Error.BranchSwitchFailed",
        "Git.Error.UpdateFailed",

        // Dialog buttons
        "Dialog.OK",
        "Dialog.Cancel"
    };

    public GitLocalizationTests()
    {
        // Find Assets/Localization directory relative to test assembly
        var testDir = Directory.GetCurrentDirectory();
        var solutionRoot = FindSolutionRoot(testDir);
        _localizationPath = Path.Combine(solutionRoot, "Assets", "Localization");
    }

    /// <summary>
    /// Finds solution root by looking for .sln file in parent directories.
    /// </summary>
    private static string FindSolutionRoot(string startPath)
    {
        var dir = new DirectoryInfo(startPath);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Any())
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find solution root directory");
    }

    #region Key Existence Tests

    [Fact]
    public void AllLanguages_HaveAllGitKeys()
    {
        // Verify that each language file contains all required Git keys
        foreach (var lang in _supportedLanguages)
        {
            var filePath = Path.Combine(_localizationPath, $"{lang}.json");
            Assert.True(File.Exists(filePath), $"Language file {lang}.json not found");

            var json = File.ReadAllText(filePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            Assert.NotNull(dict);

            foreach (var key in _requiredGitKeys)
            {
                Assert.True(dict!.ContainsKey(key),
                    $"Language {lang} is missing required Git key: {key}");
            }
        }
    }

    [Fact]
    public void AllLanguages_HaveNonEmptyGitValues()
    {
        // Verify that Git keys have non-empty values in all languages
        foreach (var lang in _supportedLanguages)
        {
            var filePath = Path.Combine(_localizationPath, $"{lang}.json");
            var json = File.ReadAllText(filePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            Assert.NotNull(dict);

            foreach (var key in _requiredGitKeys)
            {
                var value = dict![key];
                Assert.False(string.IsNullOrWhiteSpace(value),
                    $"Language {lang} has empty value for Git key: {key}");
            }
        }
    }

    #endregion

    #region Format String Tests

    [Fact]
    public void ErrorMessages_HaveCorrectFormatPlaceholders()
    {
        // Error messages that should have {0} placeholder
        var messagesWithPlaceholder = new[]
        {
            "Git.Error.CloneFailed",
            "Git.Error.NetworkError",
            "Git.Error.BranchSwitchFailed",
            "Git.Error.UpdateFailed"
        };

        foreach (var lang in _supportedLanguages)
        {
            var filePath = Path.Combine(_localizationPath, $"{lang}.json");
            var json = File.ReadAllText(filePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

            foreach (var key in messagesWithPlaceholder)
            {
                var value = dict![key];
                Assert.Contains("{0}", value);
            }
        }
    }

    [Fact]
    public void NoInternetConnectionMessage_HasNoPlaceholders()
    {
        // "NoInternetConnection" should not have placeholders
        var key = "Git.Error.NoInternetConnection";

        foreach (var lang in _supportedLanguages)
        {
            var filePath = Path.Combine(_localizationPath, $"{lang}.json");
            var json = File.ReadAllText(filePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

            var value = dict![key];
            Assert.DoesNotContain("{0}", value);
        }
    }

    #endregion

    #region Progress Message Tests

    [Fact]
    public void ProgressMessages_EndWithEllipsis()
    {
        // Progress messages should end with "..." for consistency
        var progressKeys = _requiredGitKeys.Where(k => k.Contains(".Progress.")).ToArray();

        foreach (var lang in _supportedLanguages)
        {
            var filePath = Path.Combine(_localizationPath, $"{lang}.json");
            var json = File.ReadAllText(filePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

            foreach (var key in progressKeys)
            {
                var value = dict![key];
                Assert.True(value.EndsWith("...") || value.EndsWith("…"),
                    $"Language {lang} progress message should end with ellipsis: {key} = '{value}'");
            }
        }
    }

    #endregion

    #region Menu Item Tests

    [Fact]
    public void MenuItems_AreNotTooLong()
    {
        // Menu items should be reasonably short (UI constraint)
        var menuKeys = _requiredGitKeys.Where(k => k.StartsWith("Menu.Git.")).ToArray();
        const int maxMenuLength = 50;

        foreach (var lang in _supportedLanguages)
        {
            var filePath = Path.Combine(_localizationPath, $"{lang}.json");
            var json = File.ReadAllText(filePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

            foreach (var key in menuKeys)
            {
                var value = dict![key];
                Assert.True(value.Length <= maxMenuLength,
                    $"Language {lang} menu item too long (>{maxMenuLength} chars): {key} = '{value}'");
            }
        }
    }

    #endregion

    #region Dialog Button Tests

    [Fact]
    public void DialogButtons_ExistInAllLanguages()
    {
        // Dialog buttons are critical - must exist in all languages
        var buttonKeys = new[] { "Dialog.OK", "Dialog.Cancel" };

        foreach (var lang in _supportedLanguages)
        {
            var filePath = Path.Combine(_localizationPath, $"{lang}.json");
            var json = File.ReadAllText(filePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

            foreach (var key in buttonKeys)
            {
                Assert.True(dict!.ContainsKey(key),
                    $"Language {lang} is missing critical dialog button: {key}");
                Assert.False(string.IsNullOrWhiteSpace(dict[key]),
                    $"Language {lang} has empty dialog button: {key}");
            }
        }
    }

    #endregion

    #region Consistency Tests

    [Fact]
    public void AllLanguageFiles_HaveSameKeys()
    {
        // All language files should have identical key sets
        Dictionary<string, string>? referenceDict = null;
        string? referenceLang = null;

        foreach (var lang in _supportedLanguages)
        {
            var filePath = Path.Combine(_localizationPath, $"{lang}.json");
            var json = File.ReadAllText(filePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

            if (referenceDict == null)
            {
                referenceDict = dict;
                referenceLang = lang;
                continue;
            }

            // Compare key sets
            var referenceKeys = referenceDict.Keys.OrderBy(k => k).ToList();
            var currentKeys = dict!.Keys.OrderBy(k => k).ToList();

            Assert.Equal(referenceKeys.Count, currentKeys.Count);

            var missingInCurrent = referenceKeys.Except(currentKeys).ToList();
            var extraInCurrent = currentKeys.Except(referenceKeys).ToList();

            Assert.Empty(missingInCurrent);
            Assert.Empty(extraInCurrent);
        }
    }

    [Fact]
    public void GitErrorMessages_StartWithConsistentPrefix()
    {
        // All Git error messages should be under "Git.Error." namespace
        var errorKeys = _requiredGitKeys.Where(k => k.Contains(".Error.")).ToArray();

        foreach (var key in errorKeys)
        {
            Assert.StartsWith("Git.Error.", key);
        }
    }

    #endregion

    #region Special Character Tests

    [Fact]
    public void GitKeys_DoNotContainInvalidJsonCharacters()
    {
        // Verify that values don't have unescaped special characters
        foreach (var lang in _supportedLanguages)
        {
            var filePath = Path.Combine(_localizationPath, $"{lang}.json");
            var json = File.ReadAllText(filePath);

            // File should be valid JSON (no parsing errors)
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            Assert.NotNull(dict);

            // Verify Git keys specifically
            foreach (var key in _requiredGitKeys)
            {
                var value = dict![key];
                // Value should not contain raw control characters
                Assert.DoesNotContain("\t", value);
            }
        }
    }

    #endregion

    #region URL Placeholder Tests

    [Fact]
    public void UrlPlaceholder_LooksLikeValidUrl()
    {
        // Git.Clone.UrlPlaceholder should look like a valid GitHub URL
        var key = "Git.Clone.UrlPlaceholder";

        foreach (var lang in _supportedLanguages)
        {
            var filePath = Path.Combine(_localizationPath, $"{lang}.json");
            var json = File.ReadAllText(filePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

            var value = dict![key];
            Assert.StartsWith("https://github.com/", value);
        }
    }

    #endregion
}
