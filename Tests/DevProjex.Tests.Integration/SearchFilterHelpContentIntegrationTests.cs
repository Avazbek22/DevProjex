namespace DevProjex.Tests.Integration;

public sealed class SearchFilterHelpContentIntegrationTests
{
    [Theory]
    [InlineData("help.ru.txt", "Ограничения:", "при открытии поиска фильтр закрывается автоматически", "в режиме превью поиск недоступен")]
    [InlineData("help.en.txt", "Constraints:", "opening Search closes Filter automatically", "in Preview mode, Search is unavailable")]
    [InlineData("help.de.txt", "Einschränkungen:", "Beim Öffnen der Suche wird der Filter automatisch geschlossen", "Im Vorschaumodus ist die Suche nicht verfügbar")]
    [InlineData("help.fr.txt", "Limitations :", "à l’ouverture de la recherche, le filtre se ferme automatiquement", "en mode aperçu, la recherche n’est pas disponible")]
    [InlineData("help.it.txt", "Limitazioni:", "all’apertura della ricerca, il filtro si chiude automaticamente", "in modalità anteprima, la ricerca non è disponibile")]
    [InlineData("help.kk.txt", "Шектеулер:", "іздеуді ашқанда сүзгі автоматты түрде жабылады", "алдын ала қарау режимінде іздеу қолжетімсіз")]
    [InlineData("help.tg.txt", "Маҳдудиятҳо:", "ҳангоми кушодани ҷустуҷӯ, филтр худкор баста мешавад", "дар режими пешнамоиш, ҷустуҷӯ дастрас нест")]
    [InlineData("help.uz.txt", "Cheklovlar:", "qidiruv ochilganda, filtr avtomatik yopiladi", "preview rejimida qidiruv mavjud emas")]
    public void HelpContent_SearchSection_ContainsExpectedConstraints(
        string fileName,
        string expectedHeader,
        string expectedRule1,
        string expectedRule2)
    {
        var content = ReadHelpFile(fileName);
        var section = ExtractSection(content, "## 8)", "## 9)");

        Assert.Contains(expectedHeader, section, StringComparison.Ordinal);
        Assert.Contains(expectedRule1, section, StringComparison.Ordinal);
        Assert.Contains(expectedRule2, section, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("help.ru.txt", "Ограничения:", "при открытии фильтра поиск закрывается автоматически", "в режиме превью фильтр недоступен")]
    [InlineData("help.en.txt", "Constraints:", "opening Filter closes Search automatically", "in Preview mode, Filter is unavailable")]
    [InlineData("help.de.txt", "Einschränkungen:", "Beim Öffnen des Filters wird die Suche automatisch geschlossen", "Im Vorschaumodus ist der Filter nicht verfügbar")]
    [InlineData("help.fr.txt", "Limitations :", "à l’ouverture du filtre, la recherche se ferme automatiquement", "en mode aperçu, le filtre n’est pas disponible")]
    [InlineData("help.it.txt", "Limitazioni:", "all’apertura del filtro, la ricerca si chiude automaticamente", "in modalità anteprima, il filtro non è disponibile")]
    [InlineData("help.kk.txt", "Шектеулер:", "сүзгіні ашқанда іздеу автоматты түрде жабылады", "алдын ала қарау режимінде сүзгі қолжетімсіз")]
    [InlineData("help.tg.txt", "Маҳдудиятҳо:", "ҳангоми кушодани филтр, ҷустуҷӯ худкор баста мешавад", "дар режими пешнамоиш, филтр дастрас нест")]
    [InlineData("help.uz.txt", "Cheklovlar:", "filtr ochilganda, qidiruv avtomatik yopiladi", "preview rejimida filtr mavjud emas")]
    public void HelpContent_FilterSection_ContainsExpectedConstraints(
        string fileName,
        string expectedHeader,
        string expectedRule1,
        string expectedRule2)
    {
        var content = ReadHelpFile(fileName);
        var section = ExtractSection(content, "## 9)", "## 10)");

        Assert.Contains(expectedHeader, section, StringComparison.Ordinal);
        Assert.Contains(expectedRule1, section, StringComparison.Ordinal);
        Assert.Contains(expectedRule2, section, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("help.ru.txt")]
    [InlineData("help.en.txt")]
    [InlineData("help.de.txt")]
    [InlineData("help.fr.txt")]
    [InlineData("help.it.txt")]
    [InlineData("help.kk.txt")]
    [InlineData("help.tg.txt")]
    [InlineData("help.uz.txt")]
    public void HelpContent_SearchAndFilterSections_ContainSingleConstraintsBlockEach(string fileName)
    {
        var content = ReadHelpFile(fileName);
        var searchSection = ExtractSection(content, "## 8)", "## 9)");
        var filterSection = ExtractSection(content, "## 9)", "## 10)");

        Assert.Equal(1, CountAnyConstraintHeader(searchSection));
        Assert.Equal(1, CountAnyConstraintHeader(filterSection));
    }

    private static int CountAnyConstraintHeader(string section)
    {
        var knownHeaders = new[]
        {
            "Ограничения:",
            "Constraints:",
            "Einschränkungen:",
            "Limitations :",
            "Limitazioni:",
            "Шектеулер:",
            "Маҳдудиятҳо:",
            "Cheklovlar:"
        };

        return knownHeaders.Count(section.Contains);
    }

    private static string ReadHelpFile(string fileName)
    {
        var repoRoot = FindRepositoryRoot();
        var file = Path.Combine(repoRoot, "Assets", "HelpContent", fileName);
        return File.ReadAllText(file);
    }

    private static string ExtractSection(string content, string startMarker, string endMarker)
    {
        var start = content.IndexOf(startMarker, StringComparison.Ordinal);
        var end = content.IndexOf(endMarker, start + 1, StringComparison.Ordinal);

        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        Assert.True(end > start, $"End marker not found after start marker: {endMarker}");

        return content.Substring(start, end - start);
    }

    private static string FindRepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) ||
                File.Exists(Path.Combine(dir, "DevProjex.sln")))
                return dir;

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
