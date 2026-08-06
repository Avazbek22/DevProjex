namespace DevProjex.Tests.Unit;

public sealed class AppLanguageUtilityTests
{
    [Fact]
    public void EverySupportedLanguageCode_RoundTripsThroughParser()
    {
        foreach (var language in Enum.GetValues<AppLanguage>())
        {
            var code = AppLanguageUtility.ToCode(language);

            Assert.True(AppLanguageUtility.TryParseCode(code, out var parsed));
            Assert.Equal(language, parsed);
        }
    }

    [Theory]
    [InlineData("PT-PT", AppLanguage.PtPt)]
    [InlineData(" pt_pt ", AppLanguage.PtPt)]
    [InlineData("RU", AppLanguage.Ru)]
    public void TryParseCode_NormalizesCaseWhitespaceAndSeparators(
        string code,
        AppLanguage expected)
    {
        Assert.True(AppLanguageUtility.TryParseCode(code, out var language));
        Assert.Equal(expected, language);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("en-us")]
    [InlineData("unknown")]
    public void TryParseCode_RejectsTokensOutsideApplicationCatalog(string? code)
    {
        Assert.False(AppLanguageUtility.TryParseCode(code, out _));
    }
}
