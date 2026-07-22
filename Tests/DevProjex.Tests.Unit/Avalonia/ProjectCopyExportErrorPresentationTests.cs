using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class ProjectCopyExportErrorPresentationTests
{
    [Fact]
    public void EveryProjectCopyErrorHasExplicitLocalizationKey()
    {
        var keys = Enum.GetValues<ProjectCopyExportError>()
            .Select(ProjectCopyExportErrorPresentation.ResolveLocalizationKey)
            .ToArray();

        Assert.Equal(Enum.GetValues<ProjectCopyExportError>().Length, keys.Length);
        Assert.All(keys, key => Assert.StartsWith("Error.ProjectCopy.", key, StringComparison.Ordinal));
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ResolverNeverReturnsRawTechnicalExceptionMessage()
    {
        const string technicalMessage = "The ZIP destination directory is unavailable.";
        var exception = new ProjectCopyExportException(ProjectCopyExportError.DestinationUnavailable, technicalMessage);

        var key = ProjectCopyExportErrorPresentation.ResolveLocalizationKey(exception);

        Assert.Equal("Error.ProjectCopy.DestinationUnavailable", key);
        Assert.DoesNotContain(technicalMessage, key, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ExceptionMappings))]
    public void SystemAndUnknownExceptionsMapToStableLocalizedCategories(Exception exception, string expectedKey)
    {
        Assert.Equal(expectedKey, ProjectCopyExportErrorPresentation.ResolveLocalizationKey(exception));
    }

    [Fact]
    public void RussianPresentationCannotContainEnglishTechnicalMessage()
    {
        const string technicalMessage = "Access to the path is denied.";
        var key = ProjectCopyExportErrorPresentation.ResolveLocalizationKey(new UnauthorizedAccessException(technicalMessage));
        var russian = ReadLocalizationValue("ru.json", key);

        Assert.Equal("Недостаточно прав для чтения исходных данных или записи результата.", russian);
        Assert.DoesNotContain(technicalMessage, russian, StringComparison.Ordinal);
    }

    public static TheoryData<Exception, string> ExceptionMappings => new()
    {
        { new IOException("technical IO details"), "Error.ProjectCopy.IoFailure" },
        { new UnauthorizedAccessException("technical access details"), "Error.ProjectCopy.AccessDenied" },
        { new InvalidOperationException("technical unexpected details"), "Error.ProjectCopy.UnexpectedFailure" }
    };

    private static string ReadLocalizationValue(string fileName, string key)
    {
        var path = Path.Combine(FindRepositoryRoot(), "Assets", "Localization", fileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty(key).GetString() ?? string.Empty;
    }

    private static string FindRepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (File.Exists(Path.Combine(directory, "DevProjex.sln")))
                return directory;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
