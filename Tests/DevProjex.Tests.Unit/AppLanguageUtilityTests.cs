namespace DevProjex.Tests.Unit;

public sealed class AppLanguageUtilityTests
{
    [Theory]
    [InlineData(AppLanguage.ZhCn, "zh-cn")]
    [InlineData(AppLanguage.ZhTw, "zh-tw")]
    [InlineData(AppLanguage.Ja, "ja")]
    [InlineData(AppLanguage.Ko, "ko")]
    [InlineData(AppLanguage.Tr, "tr")]
    [InlineData(AppLanguage.Uk, "uk")]
    [InlineData(AppLanguage.Pl, "pl")]
    [InlineData(AppLanguage.Vi, "vi")]
    [InlineData(AppLanguage.Id, "id")]
    public void NewLanguageCodes_RoundTrip(AppLanguage language, string code)
    {
        Assert.Equal(code, AppLanguageUtility.ToCode(language));
        Assert.True(AppLanguageUtility.TryParseCode(code, out var parsed));
        Assert.Equal(language, parsed);
    }

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
    [InlineData("zh_CN", AppLanguage.ZhCn)]
    [InlineData("ZH-TW", AppLanguage.ZhTw)]
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

    [Theory]
    [InlineData("zh-Hant", AppLanguage.ZhTw)]
    [InlineData("zh-HK", AppLanguage.ZhTw)]
    [InlineData("zh-Hans", AppLanguage.ZhCn)]
    [InlineData("zh-SG", AppLanguage.ZhCn)]
    [InlineData("ja-JP", AppLanguage.Ja)]
    [InlineData("ko-KR", AppLanguage.Ko)]
    [InlineData("tr-TR", AppLanguage.Tr)]
    [InlineData("uk-UA", AppLanguage.Uk)]
    [InlineData("pl-PL", AppLanguage.Pl)]
    [InlineData("vi-VN", AppLanguage.Vi)]
    [InlineData("id-ID", AppLanguage.Id)]
    public void DetectSystemLanguage_MapsNewCultures(string cultureName, AppLanguage expected)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);

        Assert.Equal(expected, AppLanguageUtility.DetectSystemLanguage(culture));
    }
}
