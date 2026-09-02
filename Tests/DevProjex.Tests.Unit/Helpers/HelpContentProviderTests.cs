namespace DevProjex.Tests.Unit.Helpers;

public sealed class HelpContentProviderTests
{
    [Fact]
    public void GetHelpBody_ReturnsNonEmptyEnglishContent()
    {
        var provider = new HelpContentProvider();

        var result = provider.GetHelpBody(AppLanguage.En);

        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public void GetHelpBody_ReturnsDifferentContentForDifferentLanguages_WhenResourcesExist()
    {
        var provider = new HelpContentProvider();

        var english = provider.GetHelpBody(AppLanguage.En);
        var russian = provider.GetHelpBody(AppLanguage.Ru);

        Assert.False(string.IsNullOrWhiteSpace(english));
        Assert.False(string.IsNullOrWhiteSpace(russian));
        Assert.NotEqual(english, russian);
    }

    [Fact]
    public void ToPlainText_RemovesMarkdownLikeMarkers_AndKeepsReadableStructure()
    {
        const string raw = """
                           ## Title
                           ### Subtitle
                           * First item
                             * Nested item with `code`
                           1) Numbered item
                           Text with `.txt`
                           """;

        var plain = HelpContentProvider.ToPlainText(raw);

        Assert.DoesNotContain("## ", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("### ", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("`", plain, StringComparison.Ordinal);
        Assert.Contains("Title", plain, StringComparison.Ordinal);
        Assert.Contains("Subtitle", plain, StringComparison.Ordinal);
        Assert.Contains("- First item", plain, StringComparison.Ordinal);
        Assert.Contains("  - Nested item with code", plain, StringComparison.Ordinal);
        Assert.Contains("1) Numbered item", plain, StringComparison.Ordinal);
        Assert.Contains("Text with .txt", plain, StringComparison.Ordinal);
    }
}
