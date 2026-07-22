namespace DevProjex.Tests.Integration;

public sealed class SearchFilterHelpContentIntegrationTests
{
    [Theory]
    [InlineData("help.ru.txt", "Ограничения:", "при открытии поиска фильтр закрывается автоматически", "поиск доступен в превью, пока виден остров дерева", "если дерево скрыто, поиск временно недоступен")]
    [InlineData("help.en.txt", "Constraints:", "opening Search closes Filter automatically", "Search is available in Preview while the tree pane is visible", "if the tree is hidden, Search is temporarily unavailable")]
    [InlineData("help.de.txt", "Einschränkungen:", "Beim Öffnen der Suche wird der Filter automatisch geschlossen", "Die Suche ist in der Vorschau verfügbar, solange die Bauminsel sichtbar ist", "Wenn der Baum ausgeblendet ist, ist die Suche vorübergehend nicht verfügbar")]
    [InlineData("help.fr.txt", "Limitations :", "à l’ouverture de la recherche, le filtre se ferme automatiquement", "la recherche reste disponible dans l’aperçu tant que le panneau de l’arborescence est visible", "si l’arborescence est masquée, la recherche devient temporairement indisponible")]
    [InlineData("help.it.txt", "Limitazioni:", "all’apertura della ricerca, il filtro si chiude automaticamente", "la ricerca è disponibile in anteprima finché il pannello dell’albero è visibile", "se l’albero è nascosto, la ricerca è temporaneamente non disponibile")]
    [InlineData("help.es.txt", "Restricciones:", "al abrir la búsqueda se cierra el filtro automáticamente", "La búsqueda está disponible en Vista previa mientras el panel de árbol está visible", "si el árbol está oculto, la búsqueda no está disponible temporalmente")]
    [InlineData("help.pt.txt", "Restrições:", "abrir a Pesquisa fecha o Filtro automaticamente", "A pesquisa está disponível na Visualização enquanto o painel em árvore está visível", "se a árvore estiver oculta, a Pesquisa ficará temporariamente indisponível")]
    [InlineData("help.pt-pt.txt", "Restrições:", "abrir a Pesquisa fecha o Filtro automaticamente", "A pesquisa está disponível na Pré-visualização enquanto o painel em árvore está visível", "se a árvore estiver oculta, a Pesquisa ficará temporariamente indisponível")]
    [InlineData("help.kk.txt", "Шектеулер:", "іздеуді ашқанда сүзгі автоматты түрде жабылады", "іздеу превью ішінде ағаш аралы көрініп тұрған кезде қолжетімді", "ағаш жасырылса, іздеу уақытша қолжетімсіз")]
    [InlineData("help.tg.txt", "Маҳдудиятҳо:", "ҳангоми кушодани ҷустуҷӯ, филтр худкор баста мешавад", "ҷустуҷӯ дар пешнамоиш то вақте дастрас аст, ки ҷазираи дарахт намоён бошад", "агар дарахт пинҳон шавад, ҷустуҷӯ муваққатан дастрас нест")]
    [InlineData("help.uz.txt", "Cheklovlar:", "qidiruv ochilganda, filtr avtomatik yopiladi", "qidiruv preview ichida daraxt oroli ko‘rinib turgan paytda mavjud", "daraxt yashirilsa, qidiruv vaqtincha mavjud emas")]
    public void HelpContent_SearchSection_ContainsExpectedConstraints(
        string fileName,
        string expectedHeader,
        string expectedRule1,
        string expectedRule2,
        string expectedRule3)
    {
        var content = ReadHelpFile(fileName);
        var section = ExtractSection(content, "## 9)", "## 10)");

        Assert.Contains(expectedHeader, section, StringComparison.Ordinal);
        Assert.Contains(expectedRule1, section, StringComparison.Ordinal);
        Assert.Contains(expectedRule2, section, StringComparison.Ordinal);
        Assert.Contains(expectedRule3, section, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("help.ru.txt", "Ограничения:", "при открытии фильтра поиск закрывается автоматически", "фильтр доступен в превью, пока виден остров дерева", "если дерево скрыто, фильтр временно недоступен")]
    [InlineData("help.en.txt", "Constraints:", "opening Filter closes Search automatically", "Filter is available in Preview while the tree pane is visible", "if the tree is hidden, Filter is temporarily unavailable")]
    [InlineData("help.de.txt", "Einschränkungen:", "Beim Öffnen des Filters wird die Suche automatisch geschlossen", "Der Filter ist in der Vorschau verfügbar, solange die Bauminsel sichtbar ist", "Wenn der Baum ausgeblendet ist, ist der Filter vorübergehend nicht verfügbar")]
    [InlineData("help.fr.txt", "Limitations :", "à l’ouverture du filtre, la recherche se ferme automatiquement", "le filtre reste disponible dans l’aperçu tant que le panneau de l’arborescence est visible", "si l’arborescence est masquée, le filtre devient temporairement indisponible")]
    [InlineData("help.it.txt", "Limitazioni:", "all’apertura del filtro, la ricerca si chiude automaticamente", "il filtro è disponibile in anteprima finché il pannello dell’albero è visibile", "se l’albero è nascosto, il filtro è temporaneamente non disponibile")]
    [InlineData("help.es.txt", "Restricciones:", "al abrir el filtro se cierra la búsqueda automáticamente", "El filtro está disponible en Vista previa mientras el panel de árbol está visible", "si el árbol está oculto, el filtro no está disponible temporalmente")]
    [InlineData("help.pt.txt", "Restrições:", "abrir o Filtro fecha a Pesquisa automaticamente", "O filtro está disponível na Visualização enquanto o painel em árvore está visível", "se a árvore estiver oculta, o Filtro ficará temporariamente indisponível")]
    [InlineData("help.pt-pt.txt", "Restrições:", "abrir o Filtro fecha a Pesquisa automaticamente", "O filtro está disponível na Visualização enquanto o painel em árvore estiver visível", "se a árvore estiver oculta, o Filtro ficará temporariamente indisponível")]
    [InlineData("help.kk.txt", "Шектеулер:", "сүзгіні ашқанда іздеу автоматты түрде жабылады", "сүзгі превью ішінде ағаш аралы көрініп тұрған кезде қолжетімді", "ағаш жасырылса, сүзгі уақытша қолжетімсіз")]
    [InlineData("help.tg.txt", "Маҳдудиятҳо:", "ҳангоми кушодани филтр, ҷустуҷӯ худкор баста мешавад", "филтр дар пешнамоиш то вақте дастрас аст, ки ҷазираи дарахт намоён бошад", "агар дарахт пинҳон шавад, филтр муваққатан дастрас нест")]
    [InlineData("help.uz.txt", "Cheklovlar:", "filtr ochilganda, qidiruv avtomatik yopiladi", "filtr preview ichida daraxt oroli ko‘rinib turgan paytda mavjud", "daraxt yashirilsa, filtr vaqtincha mavjud emas")]
    public void HelpContent_FilterSection_ContainsExpectedConstraints(
        string fileName,
        string expectedHeader,
        string expectedRule1,
        string expectedRule2,
        string expectedRule3)
    {
        var content = ReadHelpFile(fileName);
        var section = ExtractSection(content, "## 10)", "## 11)");

        Assert.Contains(expectedHeader, section, StringComparison.Ordinal);
        Assert.Contains(expectedRule1, section, StringComparison.Ordinal);
        Assert.Contains(expectedRule2, section, StringComparison.Ordinal);
        Assert.Contains(expectedRule3, section, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("help.ru.txt")]
    [InlineData("help.en.txt")]
    [InlineData("help.de.txt")]
    [InlineData("help.fr.txt")]
    [InlineData("help.it.txt")]
    [InlineData("help.es.txt")]
    [InlineData("help.pt.txt")]
    [InlineData("help.pt-pt.txt")]
    [InlineData("help.kk.txt")]
    [InlineData("help.tg.txt")]
    [InlineData("help.uz.txt")]
    public void HelpContent_SearchAndFilterSections_ContainSingleConstraintsBlockEach(string fileName)
    {
        var content = ReadHelpFile(fileName);
        var searchSection = ExtractSection(content, "## 9)", "## 10)");
        var filterSection = ExtractSection(content, "## 10)", "## 11)");

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
            "Restricciones:",
            "Restrições:",
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
