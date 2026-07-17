namespace DevProjex.Tests.Integration;

public sealed class HelpContentDocumentationContractTests
{
    public static TheoryData<string> HelpFiles => new()
    {
        "help.ru.txt",
        "help.en.txt",
        "help.de.txt",
        "help.fr.txt",
        "help.it.txt",
        "help.kk.txt",
        "help.tg.txt",
        "help.uz.txt"
    };

    public static TheoryData<string, string, string> TreeFontAndSettingsContracts => new()
    {
        { "help.ru.txt", "### Шрифт дерева", "Изменения в списках «Игнорировать», «Типы файлов» и «Папки верхнего уровня» подготавливаются" },
        { "help.en.txt", "### Tree font", "Changes in “Ignore options”, “Extensions”, and “Root folders” are staged" },
        { "help.de.txt", "### Baum-Schrift", "Änderungen in „Ignorieren“, „Dateitypen“ und „Ordner der obersten Ebene“ werden im Panel vorbereitet" },
        { "help.fr.txt", "### Police de l’arborescence", "Les changements dans « Ignorer », « Types de fichiers » et « Dossiers de premier niveau » sont préparés" },
        { "help.it.txt", "### Font albero", "Le modifiche in « Ignora », « Tipi di file » e « Cartelle di primo livello » vengono preparate" },
        { "help.kk.txt", "### Ағаш қарпі", "«Елемеу», «Файл түрлері» және «Жоғарғы деңгей қалталары» өзгерістері панельде дайындалып" },
        { "help.tg.txt", "### Шрифти дарахт", "Тағйирот дар «Нодида гирифтан», «Навъҳои файл» ва «Ҷузвдонҳои сатҳи боло» дар панел омода мешаванд" },
        { "help.uz.txt", "### Daraxt shrifti", "«E’tiborsiz qoldirish», «Fayl turlari» va «Yuqori darajadagi jildlar» o‘zgarishlari panelda tayyorlanadi" }
    };

    public static TheoryData<string, string> IgnoreScopeContracts => new()
    {
        { "help.ru.txt", "Это ограничение относится только к обнаружению вложенных областей для `.gitignore` и Smart Ignore" },
        { "help.en.txt", "This limit only controls discovery of nested scopes for `.gitignore` and Smart Ignore" },
        { "help.de.txt", "Diese Begrenzung betrifft nur die Erkennung verschachtelter Bereiche für `.gitignore` und Smart Ignore" },
        { "help.fr.txt", "Cette limite concerne uniquement la détection des zones imbriquées pour `.gitignore` et Smart Ignore" },
        { "help.it.txt", "Questo limite riguarda solo la scoperta degli ambiti annidati per `.gitignore` e Smart Ignore" },
        { "help.kk.txt", "Бұл шектеу тек `.gitignore` және Smart Ignore үшін ішкі аймақтарды табуға қатысты" },
        { "help.tg.txt", "Ин маҳдудият танҳо ба ёфтани минтақаҳои дохилӣ барои `.gitignore` ва Smart Ignore дахл дорад" },
        { "help.uz.txt", "Bu cheklov faqat `.gitignore` va Smart Ignore uchun ichki hududlarni topishga tegishli" }
    };

    public static TheoryData<string, string> LanguagePersistenceContracts => new()
    {
        { "help.ru.txt", "Выбранный язык сохраняется между запусками приложения" },
        { "help.en.txt", "The selected language is saved between launches" },
        { "help.de.txt", "Die ausgewählte Sprache wird zwischen App-Starts gespeichert" },
        { "help.fr.txt", "La langue choisie est conservée entre les lancements" },
        { "help.it.txt", "La lingua scelta viene salvata tra gli avvii dell’app" },
        { "help.kk.txt", "Таңдалған тіл қолданба қайта іске қосылғанда сақталады" },
        { "help.tg.txt", "Забони интихобшуда байни оғозҳои барнома нигоҳ дошта мешавад" },
        { "help.uz.txt", "Tanlangan til ilova qayta ishga tushirilganda saqlanadi" }
    };

    public static TheoryData<string, string, string, string, string> CurrentBehaviorContracts => new()
    {
        { "help.ru.txt", "### 12.3 Как работает «Умный игнор»", "гибрид", "Blur", "последнему завершённому состоянию" },
        { "help.en.txt", "### 12.3 How Smart Ignore works", "hybrid", "Blur", "last completed state" },
        { "help.de.txt", "### 12.3 So funktioniert Smart Ignore", "hybrid", "Weichzeichnen", "letzten abgeschlossenen Zustand" },
        { "help.fr.txt", "### 12.3 Fonctionnement de Smart Ignore", "hybride", "Flou", "dernier état terminé" },
        { "help.it.txt", "### 12.3 Come funziona Smart Ignore", "ibrido", "Sfocatura", "ultimo stato completato" },
        { "help.kk.txt", "### 12.3 Smart Ignore қалай жұмыс істейді", "гибрид", "Бұлдырлату", "соңғы аяқталған күйіне" },
        { "help.tg.txt", "### 12.3 Тарзи кори Smart Ignore", "гибрид", "Тирагӣ", "ҳолати охирини анҷомёфта" },
        { "help.uz.txt", "### 12.3 Smart Ignore qanday ishlaydi", "gibrid", "Xiralashtirish", "oxirgi yakunlangan holatiga" }
    };

    public static TheoryData<string, string> JsonTreeFormatContracts => new()
    {
        { "help.ru.txt", "JSON-экспорт использует такой формат дерева: массивы содержат файлы, объекты содержат подпапки, `/` содержит файлы текущей папки, а `[]` обозначает пустую папку." },
        { "help.en.txt", "JSON export uses this tree format: arrays contain files, objects contain subfolders, `/` contains files in the current folder, and `[]` represents an empty folder." }
    };

    [Theory]
    [MemberData(nameof(TreeFontAndSettingsContracts))]
    public void HelpContent_TreeFontAndSettingsPanel_DescribeCurrentUi(
        string fileName,
        string expectedTreeFontHeading,
        string expectedStagedSettingsText)
    {
        var content = ReadHelpFile(fileName);
        var viewSection = ExtractSection(content, "## 8)", "## 9)");
        var optionsSection = ExtractSection(content, "## 11)", "## 12)");

        Assert.Contains(expectedTreeFontHeading, viewSection, StringComparison.Ordinal);
        Assert.Contains(expectedStagedSettingsText, optionsSection, StringComparison.Ordinal);
        foreach (var staleText in StaleSettingsPanelTreeFontTexts)
            Assert.DoesNotContain(staleText, optionsSection, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(IgnoreScopeContracts))]
    public void HelpContent_IgnoreScopeDepth_ClarifiesDiscoveryOnlyLimit(
        string fileName,
        string expectedClarification)
    {
        var content = ReadHelpFile(fileName);
        var ignoreSection = ExtractSection(content, "## 12)", "## 13)");

        Assert.Contains("2", ignoreSection, StringComparison.Ordinal);
        Assert.Contains(expectedClarification, ignoreSection, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(LanguagePersistenceContracts))]
    public void HelpContent_LanguageSection_DescribesPersistenceAndSettingsReset(
        string fileName,
        string expectedPersistenceText)
    {
        var content = ReadHelpFile(fileName);
        var languageSection = ExtractSection(content, "## 17)", "## 18)");

        Assert.Contains(expectedPersistenceText, languageSection, StringComparison.Ordinal);
        Assert.Contains(FindResetSettingsLabel(fileName), languageSection, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(CurrentBehaviorContracts))]
    public void HelpContent_DescribesCurrentIgnoreRefreshAndThemeBehavior(
        string fileName,
        string expectedSmartIgnoreHeading,
        string forbiddenInternalTerm,
        string expectedBlurLabel,
        string expectedCancelRestoreText)
    {
        var content = ReadHelpFile(fileName);
        var gitSection = ExtractSection(content, "## 5)", "## 6)");
        var ignoreSection = ExtractSection(content, "## 12)", "## 13)");
        var themeSection = ExtractSection(content, "## 15)", "## 16)");
        var progressSection = ExtractSection(content, "## 18)", "## 19)");

        Assert.Contains("F5", gitSection, StringComparison.Ordinal);
        Assert.Contains("Projects / App", content, StringComparison.Ordinal);
        Assert.Contains(expectedSmartIgnoreHeading, ignoreSection, StringComparison.Ordinal);
        Assert.DoesNotContain(forbiddenInternalTerm, ignoreSection, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedBlurLabel, themeSection, StringComparison.Ordinal);
        Assert.DoesNotContain("Acrylic", themeSection, StringComparison.Ordinal);
        Assert.DoesNotContain("[Beta]", themeSection, StringComparison.Ordinal);
        Assert.Contains(expectedCancelRestoreText, progressSection, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(JsonTreeFormatContracts))]
    public void HelpContent_JsonTreeFormat_DescribesCurrentContract(string fileName, string expectedText)
    {
        var content = ReadHelpFile(fileName);

        Assert.Contains(expectedText, content, StringComparison.Ordinal);
        Assert.DoesNotContain("\"dirs\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("\"files\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("\"name\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("\"path\"", content, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(HelpFiles))]
    public void HelpContent_SubsectionNumbers_FollowCurrentMainSection(string fileName)
    {
        var content = ReadHelpFile(fileName);
        var currentMainSection = 0;

        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            var mainMatch = Regex.Match(line, @"^## (?<number>\d+)\)");
            if (mainMatch.Success)
            {
                currentMainSection = int.Parse(mainMatch.Groups["number"].Value);
                continue;
            }

            var subsectionMatch = Regex.Match(line, @"^### (?<number>\d+)\.\d+\s");
            if (!subsectionMatch.Success)
                continue;

            var subsectionMain = int.Parse(subsectionMatch.Groups["number"].Value);
            Assert.Equal(currentMainSection, subsectionMain);
        }
    }

    [Theory]
    [MemberData(nameof(HelpFiles))]
    public void HelpContent_DoesNotMentionRemovedAdditionalCountersToggle(string fileName)
    {
        var content = ReadHelpFile(fileName);
        foreach (var staleText in StaleAdditionalCountersTexts)
            Assert.DoesNotContain(staleText, content, StringComparison.Ordinal);
    }

    private static readonly string[] StaleSettingsPanelTreeFontTexts =
    [
        "Шрифт дерева —",
        "Font (tree font)",
        "Baum‑Schrift —",
        "Police de l’arborescence —",
        "Font albero —",
        "Ағаш қарпі —",
        "Шрифти дарахт —",
        "Daraxt shrift —"
    ];

    private static readonly string[] StaleAdditionalCountersTexts =
    [
        "Дополнительные счетчики",
        "Additional counters",
        "Zusätzliche Zähler",
        "Compteurs supplémentaires",
        "Contatori aggiuntivi",
        "Қосымша санағыштар",
        "Ҳисобкунакҳои иловагӣ",
        "Qo‘shimcha hisoblagichlar"
    ];

    private static string FindResetSettingsLabel(string fileName) =>
        fileName switch
        {
            "help.ru.txt" => "Сброс настроек",
            "help.en.txt" => "Reset settings",
            "help.de.txt" => "Einstellungen zurücksetzen",
            "help.fr.txt" => "Réinitialiser les paramètres",
            "help.it.txt" => "Ripristina impostazioni",
            "help.kk.txt" => "Параметрлерді қалпына келтіру",
            "help.tg.txt" => "Барқарор кардани танзимот",
            "help.uz.txt" => "Sozlamalarni tiklash",
            _ => throw new ArgumentOutOfRangeException(nameof(fileName), fileName, null)
        };

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
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
