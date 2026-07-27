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
        "help.es.txt",
        "help.pt.txt",
        "help.pt-pt.txt",
        "help.kk.txt",
        "help.tg.txt",
        "help.uz.txt"
    };

    public static TheoryData<string, string, string> TreeFontAndSettingsContracts => new()
    {
        { "help.ru.txt", "### Шрифт дерева", "Изменения в списках «Исключения», «Типы файлов» и «Папки верхнего уровня» подготавливаются" },
        { "help.en.txt", "### Tree font", "Changes in “Exclusions”, “Extensions”, and “Root folders” are staged" },
        { "help.de.txt", "### Baum-Schrift", "Änderungen in „Ausschlüsse“, „Dateitypen“ und „Ordner der obersten Ebene“ werden im Panel vorbereitet" },
        { "help.fr.txt", "### Police de l’arborescence", "Les changements dans « Exclusions », « Types de fichiers » et « Dossiers de premier niveau » sont préparés" },
        { "help.it.txt", "### Font albero", "Le modifiche in « Esclusioni », « Tipi di file » e « Cartelle di primo livello » vengono preparate" },
        { "help.es.txt", "### fuente de árbol", "Los cambios en \"Exclusiones\", \"Extensiones\" y \"Carpetas raíz\" se organizan" },
        { "help.pt.txt", "### Fonte de árvore", "As alterações em “Exclusões”, “Extensões” e “Pastas raiz” são preparadas" },
        { "help.pt-pt.txt", "### Fonte de árvore", "As alterações em “Exclusões”, “Extensões” e “Pastas raiz” são preparadas" },
        { "help.kk.txt", "### Ағаш қарпі", "«Ерекшеліктер», «Файл түрлері» және «Жоғарғы деңгей қалталары» өзгерістері панельде дайындалып" },
        { "help.tg.txt", "### Шрифти дарахт", "Тағйирот дар «Истисноҳо», «Навъҳои файл» ва «Ҷузвдонҳои сатҳи боло» дар панел омода мешаванд" },
        { "help.uz.txt", "### Daraxt shrifti", "«Istisnolar», «Fayl turlari» va «Yuqori darajadagi jildlar» o‘zgarishlari panelda tayyorlanadi" }
    };

    public static TheoryData<string, string, string> IgnoreScopeContracts => new()
    {
        { "help.ru.txt", "Поиск новых проектных областей ограничен", "После обнаружения области Smart Ignore применяется ко всему её поддереву без ограничения глубины" },
        { "help.en.txt", "Project scope discovery is bounded", "After a scope is found, Smart Ignore applies to its entire subtree without a depth limit" },
        { "help.de.txt", "Die Erkennung neuer Projektbereiche ist begrenzt", "Nach der Erkennung gilt Smart Ignore ohne Tiefenbegrenzung für den gesamten Unterbaum des Bereichs" },
        { "help.fr.txt", "La détection de nouvelles zones de projet est limitée", "Une fois la zone détectée, Smart Ignore s’applique à tout son sous-arbre sans limite de profondeur" },
        { "help.it.txt", "La ricerca di nuove aree di progetto è limitata", "Dopo il rilevamento, Smart Ignore si applica all’intero sottoalbero senza limite di profondità" },
        { "help.es.txt", "El descubrimiento del alcance del proyecto está limitado", "Una vez que se encuentra un alcance, Smart Ignore se aplica a todo su subárbol sin límite de profundidad" },
        { "help.pt.txt", "A descoberta do escopo do projeto é limitada", "Depois que um escopo é encontrado, o Smart Ignore se aplica a toda a sua subárvore sem limite de profundidade" },
        { "help.pt-pt.txt", "A descoberta do âmbito do projeto é limitada", "Depois de encontrado um âmbito, o Smart Ignore aplica-se a toda a sua subárvore sem limite de profundidade" },
        { "help.kk.txt", "жаңа жоба аймақтарын іздеу шектелген", "Аймақ табылғаннан кейін Smart Ignore оның бүкіл ішкі ағашына тереңдік шектеуінсіз қолданылады" },
        { "help.tg.txt", "Ҷустуҷӯи минтақаҳои нави лоиҳа маҳдуд аст", "Баъд аз ёфтани минтақа Smart Ignore ба тамоми зердарахти он бе маҳдудияти амиқӣ татбиқ мешавад" },
        { "help.uz.txt", "yangi loyiha hududlarini izlash cheklangan", "Hudud topilgach, Smart Ignore uning butun ichki daraxtiga chuqurlik cheklovisiz qo‘llanadi" }
    };

    public static TheoryData<string, string> LanguagePersistenceContracts => new()
    {
        { "help.ru.txt", "Выбранный язык сохраняется между запусками приложения" },
        { "help.en.txt", "The selected language is saved between launches" },
        { "help.de.txt", "Die ausgewählte Sprache wird zwischen App-Starts gespeichert" },
        { "help.fr.txt", "La langue choisie est conservée entre les lancements" },
        { "help.it.txt", "La lingua scelta viene salvata tra gli avvii dell’app" },
        { "help.es.txt", "El idioma seleccionado se guarda entre lanzamientos" },
        { "help.pt.txt", "O idioma selecionado é salvo entre inicializações" },
        { "help.pt-pt.txt", "O idioma selecionado é guardado entre inicializações" },
        { "help.kk.txt", "Таңдалған тіл қолданба қайта іске қосылғанда сақталады" },
        { "help.tg.txt", "Забони интихобшуда байни оғозҳои барнома нигоҳ дошта мешавад" },
        { "help.uz.txt", "Tanlangan til ilova qayta ishga tushirilganda saqlanadi" }
    };

    public static TheoryData<string, string, string, string, string> CurrentBehaviorContracts => new()
    {
        { "help.ru.txt", "### 12.2 Умный игнор", "гибрид", "Blur", "последнему завершённому состоянию" },
        { "help.en.txt", "### 12.2 Smart Ignore", "hybrid", "Blur", "last completed state" },
        { "help.de.txt", "### 12.2 Smart Ignore", "hybrid", "Weichzeichnen", "letzten abgeschlossenen Zustand" },
        { "help.fr.txt", "### 12.2 Smart Ignore", "hybride", "Flou", "dernier état terminé" },
        { "help.it.txt", "### 12.2 Smart Ignore", "ibrido", "Sfocatura", "ultimo stato completato" },
        { "help.es.txt", "### 12.2 Smart Ignore", "híbrido", "Desenfoque", "último estado completado" },
        { "help.pt.txt", "### 12.2 Smart Ignore", "híbrido", "Desfoque", "último estado concluído" },
        { "help.pt-pt.txt", "### 12.2 Smart Ignore", "híbrido", "Desfoque", "último estado concluído" },
        { "help.kk.txt", "### 12.2 Smart Ignore", "гибрид", "Бұлдырлату", "соңғы аяқталған күйіне" },
        { "help.tg.txt", "### 12.2 Smart Ignore", "гибрид", "Тирагӣ", "ҳолати охирини анҷомёфта" },
        { "help.uz.txt", "### 12.2 Smart Ignore", "gibrid", "Xiralashtirish", "oxirgi yakunlangan holatiga" }
    };

    public static TheoryData<string, string> IndependentIgnoreControllerContracts => new()
    {
        { "help.ru.txt", "независимые переключатели" },
        { "help.en.txt", "independent switches" },
        { "help.de.txt", "unabhängige Schalter" },
        { "help.fr.txt", "interrupteurs indépendants" },
        { "help.it.txt", "interruttori indipendenti" },
        { "help.es.txt", "interruptores independientes" },
        { "help.pt.txt", "controles independentes" },
        { "help.pt-pt.txt", "controlos independentes" },
        { "help.kk.txt", "тәуелсіз ауыстырып-қосқыштар" },
        { "help.tg.txt", "гузаришҳои мустақиланд" },
        { "help.uz.txt", "mustaqil almashtirgichlardir" }
    };

    public static TheoryData<string, string, string> GitIndexContracts => new()
    {
        { "help.ru.txt", "отслеживаемые файлы остаются видимыми", "без индекса или Git CLI" },
        { "help.en.txt", "tracked files remain visible", "without an index or Git CLI" },
        { "help.de.txt", "verfolgte Dateien", "ohne Index oder Git CLI" },
        { "help.fr.txt", "les fichiers suivis restent visibles", "sans index ou Git CLI" },
        { "help.it.txt", "i file tracciati restano visibili", "senza indice o Git CLI" },
        { "help.es.txt", "los archivos con seguimiento permanecen visibles", "sin índice o Git CLI" },
        { "help.pt.txt", "os arquivos rastreados permanecem visíveis", "sem índice ou Git CLI" },
        { "help.pt-pt.txt", "os ficheiros controlados permanecem visíveis", "sem índice ou Git CLI" },
        { "help.kk.txt", "бақыланатын файлдар", "индекс немесе Git CLI болмаса" },
        { "help.tg.txt", "файлҳои пайгиришаванда", "бе индекс ё Git CLI" },
        { "help.uz.txt", "kuzatiladigan fayllar", "indeks yoki Git CLI bo‘lmasa" }
    };

    public static TheoryData<string, string, string, string, string, string> TrackedOnlyGitModeContracts => new()
    {
        { "help.ru.txt", "### 12.4 Только отслеживаемые Git-файлы", "неотслеживаемые файлы исключаются", "взаимно исключают", "поддержкой профилей", "стабильной парой" },
        { "help.en.txt", "### 12.4 Tracked Git files only", "untracked files are excluded", "mutually exclusive", "support profiles", "stable pair" },
        { "help.de.txt", "### 12.4 Nur von Git verfolgte Dateien", "nicht verfolgte Dateien werden ausgeschlossen", "schließen sich gegenseitig aus", "Profilunterstützung", "stabiles Schalterpaar" },
        { "help.fr.txt", "### 12.4 Uniquement les fichiers suivis par Git", "les fichiers non suivis sont exclus", "s’excluent mutuellement", "prenant en charge les profils", "paire stable" },
        { "help.it.txt", "### 12.4 Solo file tracciati da Git", "i file non tracciati vengono esclusi", "si escludono a vicenda", "supportano i profili", "coppia stabile" },
        { "help.es.txt", "### 12.4 Solo archivos rastreados por Git", "los archivos no rastreados quedan excluidos", "se excluyen mutuamente", "admiten perfiles", "par estable" },
        { "help.pt.txt", "### 12.4 Somente arquivos rastreados pelo Git", "arquivos não rastreados são excluídos", "mutuamente exclusivos", "compatíveis com perfis", "par estável" },
        { "help.pt-pt.txt", "### 12.4 Apenas ficheiros controlados pelo Git", "os ficheiros não controlados são excluídos", "mutuamente exclusivos", "suportam perfis", "par estável" },
        { "help.kk.txt", "### 12.4 Тек Git бақылайтын файлдар", "бақыланбайтын файлдар алынып тасталады", "бір-бірін өзара жоққа шығарады", "Профильдерді қолдайтын", "тұрақты" },
        { "help.tg.txt", "### 12.4 Танҳо файлҳои пайгиришавандаи Git", "файлҳои пайгиринашаванда хориҷ мешаванд", "ҳамдигарро истисно мекунанд", "профилҳоро дастгирӣ мекунанд", "ҷуфти устувор" },
        { "help.uz.txt", "### 12.4 Faqat Git kuzatadigan fayllar", "kuzatilmaydigan fayllar chiqariladi", "o‘zaro istisno qilinadi", "Profillarni qo‘llaydigan", "barqaror" }
    };

    public static TheoryData<string, string> JsonTreeFormatContracts => new()
    {
        { "help.ru.txt", "JSON-экспорт использует такой формат дерева: массивы содержат файлы, объекты содержат подпапки, `/` содержит файлы текущей папки, а `[]` обозначает пустую папку." },
        { "help.en.txt", "JSON export uses this tree format: arrays contain files, objects contain subfolders, `/` contains files in the current folder, and `[]` represents an empty folder." }
    };

    public static TheoryData<string, string, string, string> ThemeFallbackContracts => new()
    {
        { "help.ru.txt", "сначала пробует другой системный эффект", "фон главного окна", "Если Blur недоступен, приложение пробует Mica" },
        { "help.en.txt", "first tries the other native effect", "main-window background", "If Blur is unavailable, the app tries Mica" },
        { "help.de.txt", "zunächst den jeweils anderen nativen Effekt", "Hintergrund des Hauptfensters", "Ist Weichzeichnen nicht verfügbar, versucht die App Mica" },
        { "help.fr.txt", "essaie d’abord l’autre effet natif", "arrière-plan de la fenêtre principale", "Si le Flou n’est pas disponible, l’application essaie Mica" },
        { "help.it.txt", "prova prima l’altro effetto nativo", "sfondo della finestra principale", "Se Sfocatura non è disponibile, l’app prova Mica" },
        { "help.es.txt", "prueba primero el otro efecto nativo", "fondo de la ventana principal", "Si Blur no está disponible, la aplicación prueba Mica" },
        { "help.pt.txt", "primeiro tenta o outro efeito nativo", "fundo da janela principal", "Se o Blur não estiver disponível, o aplicativo tentará o Mica" },
        { "help.pt-pt.txt", "tenta primeiro o outro efeito nativo", "fundo da janela principal", "Se o Blur não estiver disponível, a aplicação tentará o Mica" },
        { "help.kk.txt", "алдымен басқа жүйелік эффектіні қолданып көреді", "негізгі терезенің фоны", "Бұлдырлату қолжетімсіз болса, қолданба Mica-ны қолданып көреді" },
        { "help.tg.txt", "аввал эффекти дигари системавиро месанҷад", "заминаи равзанаи асосӣ", "Агар Тирагӣ дастрас набошад, барнома Mica-ро месанҷад" },
        { "help.uz.txt", "avval boshqa tizim effektini sinab ko‘radi", "asosiy oyna foni", "Xiralashtirish mavjud bo‘lmasa, ilova Mica-ni sinab ko‘radi" }
    };

    public static TheoryData<string, string, string, string, string, string, string, string> ProjectExportContracts => new()
    {
        { "help.ru.txt", "Экспорт проекта", "В папку…", "В ZIP-архив…", "Если ничего не отмечено", "структуры каталогов", "бинарные файлы", "внутрь исходного проекта" },
        { "help.en.txt", "Export project", "To folder…", "To ZIP archive…", "If nothing is checked", "directory structure", "binary files", "inside the source project" },
        { "help.de.txt", "Projekt exportieren", "In Ordner…", "In ZIP-Archiv…", "Ist nichts markiert", "Verzeichnisstruktur", "Binärdateien", "innerhalb des Quellprojekts" },
        { "help.fr.txt", "Exporter le projet", "Vers un dossier…", "Vers une archive ZIP…", "Si rien n’est coché", "structure des répertoires", "fichiers texte et binaires", "dans le projet source" },
        { "help.it.txt", "Esporta progetto", "In una cartella…", "In un archivio ZIP…", "Se non è selezionato nulla", "struttura delle directory", "file di testo e binari", "all’interno del progetto di origine" },
        { "help.es.txt", "Exportar proyecto", "A una carpeta…", "A un archivo ZIP…", "Si no hay ninguno marcado", "estructura de directorios", "archivos de texto y binarios", "dentro del proyecto de origen" },
        { "help.pt.txt", "Exportar projeto", "Para uma pasta…", "Para um arquivo ZIP…", "Se nada estiver marcado", "estrutura de diretórios", "Arquivos de texto e binários", "dentro do projeto de origem" },
        { "help.pt-pt.txt", "Exportar projeto", "Para uma pasta…", "Para um arquivo ZIP…", "Se nada estiver assinalado", "estrutura dos diretórios", "ficheiros de texto e binários", "dentro do projeto de origem" },
        { "help.kk.txt", "Жобаны экспорттау", "Қалтаға…", "ZIP мұрағатына…", "Ештеңе белгіленбесе", "каталогтар құрылымын", "бинарлық файлдар", "бастапқы жобаның ішіне" },
        { "help.tg.txt", "Содироти лоиҳа", "Ба ҷузвдон…", "Ба бойгонии ZIP…", "Агар чизе қайд нашуда бошад", "сохтори каталогҳо", "Файлҳои матнӣ ва бинарӣ", "дохили лоиҳаи аслӣ" },
        { "help.uz.txt", "Loyihani eksport qilish", "Jildga…", "ZIP arxivga…", "Hech narsa belgilanmasa", "kataloglar tuzilmasini", "Matnli va binar fayllar", "boshlang‘ich loyiha ichiga" }
    };

    public static TheoryData<string, string> ProjectExportBusyContracts => new()
    {
        { "help.ru.txt", "Во время экспорта нельзя изменять дерево и параметры, использовать фильтр, менять формат или режим предпросмотра" },
        { "help.en.txt", "During export, you cannot change the tree or settings, use the filter, change the format or preview mode" },
        { "help.de.txt", "Während des Exports können Baum und Einstellungen nicht geändert, Filter, Format oder Vorschaumodus nicht verwendet" },
        { "help.fr.txt", "Pendant l’export, vous ne pouvez pas modifier l’arborescence ou les paramètres, utiliser le filtre, changer le format ou le mode d’aperçu" },
        { "help.it.txt", "Durante l’esportazione non è possibile modificare l’albero o le impostazioni, usare il filtro, cambiare formato o modalità di anteprima" },
        { "help.es.txt", "Durante la exportación no se puede cambiar el árbol ni la configuración, usar el filtro, cambiar el formato o el modo de vista previa" },
        { "help.pt.txt", "Durante a exportação, não é possível alterar a árvore ou as configurações, usar o filtro, mudar o formato ou o modo de visualização" },
        { "help.pt-pt.txt", "Durante a exportação, não é possível alterar a árvore ou as definições, usar o filtro, mudar o formato ou o modo de pré-visualização" },
        { "help.kk.txt", "Экспорт кезінде ағаш пен параметрлерді өзгертуге, сүзгіні пайдалануға, пішімді немесе алдын ала қарау режимін ауыстыруға" },
        { "help.tg.txt", "Ҳангоми содирот тағйир додани дарахт ё параметрҳо, истифодаи филтр, иваз кардани формат ё реҷаи пешнамоиш" },
        { "help.uz.txt", "Eksport paytida daraxt yoki parametrlarni o‘zgartirish, filtrdan foydalanish, format yoki ko‘rib chiqish rejimini almashtirish" }
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
        string expectedBoundedDiscoveryText,
        string expectedUnlimitedApplicationText)
    {
        var content = ReadHelpFile(fileName);
        var ignoreSection = ExtractSection(content, "## 12)", "## 13)");

        Assert.Contains("2", ignoreSection, StringComparison.Ordinal);
        Assert.Contains(expectedBoundedDiscoveryText, ignoreSection, StringComparison.Ordinal);
        Assert.Contains(expectedUnlimitedApplicationText, ignoreSection, StringComparison.Ordinal);
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
    [MemberData(nameof(IndependentIgnoreControllerContracts))]
    public void HelpContent_DescribesIndependentGitAndSmartControllers(
        string fileName,
        string expectedIndependentText)
    {
        var ignoreSection = ExtractSection(ReadHelpFile(fileName), "## 12)", "## 13)");

        Assert.Contains(expectedIndependentText, ignoreSection, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(GitIndexContracts))]
    public void HelpContent_GitIgnoreSection_DescribesTrackedIndexAndPatternOnlyFallback(
        string fileName,
        string expectedTrackedBehavior,
        string expectedFallbackBehavior)
    {
        var ignoreSection = ExtractSection(ReadHelpFile(fileName), "## 12)", "## 13)");

        Assert.Contains(expectedTrackedBehavior, ignoreSection, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedFallbackBehavior, ignoreSection, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(TrackedOnlyGitModeContracts))]
    public void HelpContent_TrackedOnlySection_DescribesIndexOwnershipAndStableGitModePair(
        string fileName,
        string expectedHeading,
        string expectedUntrackedBehavior,
        string expectedMutualExclusion,
        string expectedProfilePersistence,
        string expectedStablePair)
    {
        var ignoreSection = ExtractSection(ReadHelpFile(fileName), "## 12)", "## 13)");
        var trackedOnlySection = ExtractSection(ignoreSection, expectedHeading, "### 12.5");

        Assert.Contains(expectedUntrackedBehavior, trackedOnlySection, StringComparison.Ordinal);
        Assert.Contains(expectedMutualExclusion, trackedOnlySection, StringComparison.Ordinal);
        Assert.Contains(expectedProfilePersistence, trackedOnlySection, StringComparison.Ordinal);
        Assert.Contains("worktree", trackedOnlySection, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HEAD", trackedOnlySection, StringComparison.Ordinal);
        Assert.Contains(".gitignore", trackedOnlySection, StringComparison.Ordinal);
        Assert.Contains(expectedStablePair, ignoreSection, StringComparison.Ordinal);
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
    [MemberData(nameof(ThemeFallbackContracts))]
    public void HelpContent_ThemeSection_DescribesNativeFallbackChainAndTransparentPopupSurfaces(
        string fileName,
        string expectedNativeFallbackText,
        string expectedTransparentWindowText,
        string expectedDefaultFallbackText)
    {
        var content = ReadHelpFile(fileName);
        var themeSection = ExtractSection(content, "## 15)", "## 16)");

        Assert.Contains(expectedNativeFallbackText, themeSection, StringComparison.Ordinal);
        Assert.Contains(expectedTransparentWindowText, themeSection, StringComparison.Ordinal);
        Assert.Contains(expectedDefaultFallbackText, content, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ProjectExportContracts))]
    public void HelpContent_FileSection_DescribesPhysicalProjectExportContract(
        string fileName,
        string expectedParent,
        string expectedFolderAction,
        string expectedZipAction,
        string expectedUncheckedBehavior,
        string expectedDirectoryStructure,
        string expectedBinarySupport,
        string expectedInsideSourceRestriction)
    {
        var fileSection = ExtractSection(ReadHelpFile(fileName), "## 3)", "## 4)");

        Assert.Contains(expectedParent, fileSection, StringComparison.Ordinal);
        Assert.Contains(expectedFolderAction, fileSection, StringComparison.Ordinal);
        Assert.Contains(expectedZipAction, fileSection, StringComparison.Ordinal);
        Assert.Contains(expectedUncheckedBehavior, fileSection, StringComparison.Ordinal);
        Assert.Contains(expectedDirectoryStructure, fileSection, StringComparison.Ordinal);
        Assert.Contains(expectedBinarySupport, fileSection, StringComparison.Ordinal);
        Assert.Contains(expectedInsideSourceRestriction, fileSection, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ProjectExportBusyContracts))]
    public void HelpContent_FileSection_DescribesUnavailableTreeChangesDuringProjectExport(
        string fileName,
        string expectedBusyBehavior)
    {
        var fileSection = ExtractSection(ReadHelpFile(fileName), "## 3)", "## 4)");

        Assert.Contains(expectedBusyBehavior, fileSection, StringComparison.Ordinal);
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
            "help.es.txt" => "Restablecer configuración",
            "help.pt.txt" => "Redefinir configurações",
            "help.pt-pt.txt" => "Repor definições",
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
