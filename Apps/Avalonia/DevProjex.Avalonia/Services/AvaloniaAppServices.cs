using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Avalonia.Services;

public sealed record AvaloniaAppServices(
    LocalizationService Localization,
    HelpContentProvider HelpContentProvider,
    ThemePresetStore ThemePresetStore,
    IProjectProfileStore ProjectProfileStore,
    IElevationService Elevation,
    ScanOptionsUseCase ScanOptionsUseCase,
    BuildTreeUseCase BuildTreeUseCase,
    IgnoreOptionsService IgnoreOptionsService,
    IgnoreRulesService IgnoreRulesService,
    FilterOptionSelectionService FilterOptionSelectionService,
    TreeExportService TreeExportService,
    SelectedContentExportService ContentExportService,
    TreeAndContentExportService TreeAndContentExportService,
    RepositoryWebPathPresentationService RepositoryWebPathPresentationService,
    TextFileExportService TextFileExportService,
    IToastService ToastService,
    IIconStore IconStore,
    IGitRepositoryService GitRepositoryService,
    IRepoCacheService RepoCacheService,
    IZipDownloadService ZipDownloadService,
    IFileContentAnalyzer FileContentAnalyzer);
