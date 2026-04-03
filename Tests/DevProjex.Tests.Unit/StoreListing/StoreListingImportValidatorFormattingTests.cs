using DevProjex.Tests.Shared.StoreListing;

namespace DevProjex.Tests.Unit.StoreListing;

public sealed class StoreListingImportValidatorFormattingTests
{
    [Fact]
    public void ValidateImportFolder_Fails_WhenHeaderContainsEmptyColumn()
    {
        var builder = new StoreListingValidationTestBuilder();
        var fixture = builder.Build();
        using var tempDirectory = fixture.TempDirectory;

        var lines = File.ReadAllLines(fixture.ImportCsvPath, Encoding.UTF8);
        lines[0] += ",";
        File.WriteAllLines(fixture.ImportCsvPath, lines, new UTF8Encoding(false));

        var report = StoreListingImportValidator.ValidateImportFolder(
            fixture.ImportFolderPath,
            fixture.ImportCsvPath,
            fixture.TemplateCsvPath,
            new StoreListingValidationOptions());

        Assert.Contains(report.Errors, issue => issue.Code == "SLI023");
    }

    [Fact]
    public void ValidateImportFolder_Fails_WhenHeaderContainsDuplicateColumn()
    {
        var builder = new StoreListingValidationTestBuilder();
        var fixture = builder.Build();
        using var tempDirectory = fixture.TempDirectory;

        var lines = File.ReadAllLines(fixture.ImportCsvPath, Encoding.UTF8);
        lines[0] = lines[0].Replace(",fr-fr", ",en-us", StringComparison.Ordinal);
        File.WriteAllLines(fixture.ImportCsvPath, lines, new UTF8Encoding(false));

        var report = StoreListingImportValidator.ValidateImportFolder(
            fixture.ImportFolderPath,
            fixture.ImportCsvPath,
            fixture.TemplateCsvPath,
            new StoreListingValidationOptions());

        Assert.Contains(report.Errors, issue => issue.Code == "SLI024");
    }

    [Fact]
    public void ValidateImportFolder_Fails_WhenFieldRowIsDuplicated()
    {
        var builder = new StoreListingValidationTestBuilder();
        var fixture = builder.Build();
        using var tempDirectory = fixture.TempDirectory;

        var document = StoreListingCsvDocument.Load(fixture.ImportCsvPath);
        var duplicatedTitleRow = document.Rows.First(row => row.Field == "Title");
        var rows = document.Rows.Concat([duplicatedTitleRow]).ToArray();
        StoreListingCsvWriter.Save(fixture.ImportCsvPath, document.Headers, rows, utf8Bom: false);

        var report = StoreListingImportValidator.ValidateImportFolder(
            fixture.ImportFolderPath,
            fixture.ImportCsvPath,
            fixture.TemplateCsvPath,
            new StoreListingValidationOptions());

        Assert.Contains(report.Errors, issue => issue.Code == "SLI025");
    }

    [Fact]
    public void ValidateImportFolder_Fails_WhenSearchTermHasTrailingWhitespace()
    {
        var builder = new StoreListingValidationTestBuilder()
            .WithValue("SearchTerm1", "en-us", "project context ");

        var fixture = builder.Build();
        using var tempDirectory = fixture.TempDirectory;

        var report = StoreListingImportValidator.ValidateImportFolder(
            fixture.ImportFolderPath,
            fixture.ImportCsvPath,
            fixture.TemplateCsvPath,
            new StoreListingValidationOptions());

        Assert.Contains(report.Errors, issue => issue.Code == "SLI026");
    }

    [Fact]
    public void ValidateImportFolder_Fails_WhenAssetPathHasLeadingWhitespace()
    {
        var builder = new StoreListingValidationTestBuilder()
            .WithValue("DesktopScreenshot1", "en-us", " ImportFolder/Screenshots/1/EN.png");

        var fixture = builder.Build();
        using var tempDirectory = fixture.TempDirectory;

        var report = StoreListingImportValidator.ValidateImportFolder(
            fixture.ImportFolderPath,
            fixture.ImportCsvPath,
            fixture.TemplateCsvPath,
            new StoreListingValidationOptions());

        Assert.Contains(report.Errors, issue => issue.Code == "SLI026");
    }
}
