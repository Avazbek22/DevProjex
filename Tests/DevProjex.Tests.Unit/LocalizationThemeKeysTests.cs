namespace DevProjex.Tests.Unit;

public sealed class LocalizationThemeKeysTests
{
    private static readonly string[] RequiredThemeKeys =
    [
        "Theme.ModeLabel",
        "Theme.System",
        "Theme.Light",
        "Theme.Dark"
    ];

    [Fact]
    public void LocalizationFiles_ContainNonEmptyThemeSelectionLabels()
    {
        var localizationDirectory = Path.Combine(
            FindRepositoryRoot(),
            "Assets",
            "Localization");

        foreach (var file in Directory.GetFiles(localizationDirectory, "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var key in RequiredThemeKeys)
            {
                Assert.True(
                    document.RootElement.TryGetProperty(key, out var value),
                    $"Missing {key} in {Path.GetFileName(file)}.");
                Assert.False(
                    string.IsNullOrWhiteSpace(value.GetString()),
                    $"{key} is empty in {Path.GetFileName(file)}.");
            }
        }
    }

    [Fact]
    public void LocalizationFiles_ContainTreeExpansionAnimationLabel()
    {
        var localizationDirectory = Path.Combine(
            FindRepositoryRoot(),
            "Assets",
            "Localization");

        foreach (var file in Directory.GetFiles(localizationDirectory, "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            Assert.True(
                document.RootElement.TryGetProperty(
                    "Menu.View.TreeExpansionAnimation",
                    out var value),
                $"Missing tree expansion animation label in {Path.GetFileName(file)}.");
            Assert.False(
                string.IsNullOrWhiteSpace(value.GetString()),
                $"Tree expansion animation label is empty in {Path.GetFileName(file)}.");
            Assert.False(
                document.RootElement.TryGetProperty("Menu.View.TreeAnimation", out _),
                $"Obsolete tree hover animation label remains in {Path.GetFileName(file)}.");
        }

        using var russian = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(localizationDirectory, "ru.json")));
        Assert.Equal(
            "Анимация раскрытия дерева",
            russian.RootElement
                .GetProperty("Menu.View.TreeExpansionAnimation")
                .GetString());
    }

    private static string FindRepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "DevProjex.sln")))
                return directory;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
