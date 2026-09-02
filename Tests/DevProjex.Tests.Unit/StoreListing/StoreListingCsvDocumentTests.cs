using DevProjex.Tests.Shared.StoreListing;

namespace DevProjex.Tests.Unit.StoreListing;

public sealed class StoreListingCsvDocumentTests
{
    [Fact]
    public void Load_ParsesMultilineQuotedCells_AndPreservesHeaderOrder()
    {
        using var tempDirectory = new TemporaryDirectory();
        var csvPath = tempDirectory.CreateFile(
            "listing.csv",
            """
            Field,ID,Type,default,en-us
            Description,2,Text,,"Line 1
            Line 2"
            Title,4,Text,,DevProjex
            """);

        var document = StoreListingCsvDocument.Load(csvPath);

        Assert.Equal(["Field", "ID", "Type", "default", "en-us"], document.Headers);
        Assert.Equal(2, document.Rows.Count);
        var description = document.RowsByField["Description"].GetValue("en-us").Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal("Line 1\nLine 2", description);
    }

    [Fact]
    public void Load_BuildsFieldIndex_ForFastRuleBasedValidation()
    {
        using var tempDirectory = new TemporaryDirectory();
        var csvPath = tempDirectory.CreateFile(
            "listing.csv",
            """
            Field,ID,Type,default,en-us
            Title,4,Text,,DevProjex
            ShortDescription,8,Text,,Short text
            """);

        var document = StoreListingCsvDocument.Load(csvPath);

        Assert.Equal("DevProjex", document.RowsByField["Title"].GetValue("en-us"));
        Assert.Equal("Short text", document.RowsByField["ShortDescription"].GetValue("en-us"));
    }

    [Fact]
    public void ValidateImportFolder_AcceptsLocalizedTypeHeaderInTemplate()
    {
        var fixture = new StoreListingValidationTestBuilder().Build();
        using var tempDirectory = fixture.TempDirectory;
        ReplaceTypeHeaderWithLocalizedAlias(fixture.TemplateCsvPath);

        var report = StoreListingImportValidator.ValidateImportFolder(
            fixture.ImportFolderPath,
            fixture.ImportCsvPath,
            fixture.TemplateCsvPath,
            new StoreListingValidationOptions());

        Assert.False(report.HasErrors, string.Join(Environment.NewLine, report.Errors));
    }

    private static void ReplaceTypeHeaderWithLocalizedAlias(string csvPath)
    {
        var csv = File.ReadAllText(csvPath, Encoding.UTF8);
        csv = csv.Replace(",Type,", ",Type (Tipo),", StringComparison.Ordinal);
        File.WriteAllText(csvPath, csv, new UTF8Encoding(false));
    }
}
