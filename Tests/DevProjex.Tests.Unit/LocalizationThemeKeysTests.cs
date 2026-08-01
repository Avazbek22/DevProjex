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
