using DevProjex.Tests.Shared.StoreListing;

namespace DevProjex.Tests.Unit.StoreListing;

public sealed class StoreListingImportValidatorSchemaTests
{
    [Fact]
    public void ValidateImportFolder_Fails_WhenHeaderDoesNotMatchTemplate()
    {
        var builder = new StoreListingValidationTestBuilder();
        var fixture = builder.Build();
        using var tempDirectory = fixture.TempDirectory;

        var text = File.ReadAllText(fixture.ImportCsvPath);
        text = text.Replace("fr-fr", "fr", StringComparison.Ordinal);
        File.WriteAllText(fixture.ImportCsvPath, text, new UTF8Encoding(false));

        var report = StoreListingImportValidator.ValidateImportFolder(
            fixture.ImportFolderPath,
            fixture.ImportCsvPath,
            fixture.TemplateCsvPath,
            new StoreListingValidationOptions());

        Assert.Contains(report.Errors, issue => issue.Code == "SLI006");
    }

    [Fact]
    public void ValidateImportFolder_AllowsLocaleColumnsAppendedAfterTemplate()
    {
        var builder = new StoreListingValidationTestBuilder()
            .WithAdditionalImportLocale("nl-nl");
        var fixture = builder.Build();
        using var tempDirectory = fixture.TempDirectory;

        var report = StoreListingImportValidator.ValidateImportFolder(
            fixture.ImportFolderPath,
            fixture.ImportCsvPath,
            fixture.TemplateCsvPath,
            new StoreListingValidationOptions());

        Assert.DoesNotContain(report.Errors, issue => issue.Code == "SLI006");
    }

    [Fact]
    public void ValidateImportFolder_Fails_WhenRowOrderDiffersFromTemplate()
    {
        var builder = new StoreListingValidationTestBuilder();
        var fixture = builder.Build();
        using var tempDirectory = fixture.TempDirectory;

        var lines = File.ReadAllLines(fixture.ImportCsvPath);
        var titleIndex = Array.FindIndex(lines, line => line.StartsWith("Title,", StringComparison.Ordinal));
        var descriptionIndex = Array.FindIndex(lines, line => line.StartsWith("Description,", StringComparison.Ordinal));
        (lines[titleIndex], lines[descriptionIndex]) = (lines[descriptionIndex], lines[titleIndex]);
        File.WriteAllLines(fixture.ImportCsvPath, lines, new UTF8Encoding(false));

        var report = StoreListingImportValidator.ValidateImportFolder(
            fixture.ImportFolderPath,
            fixture.ImportCsvPath,
            fixture.TemplateCsvPath,
            new StoreListingValidationOptions());

        Assert.Contains(report.Errors, issue => issue.Code == "SLI007");
    }
}
