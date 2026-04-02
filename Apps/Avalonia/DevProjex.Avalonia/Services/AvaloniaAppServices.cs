using DevProjex.Infrastructure.ThemePresets;
using DevProjex.Infrastructure.RecentProjects;

namespace DevProjex.Avalonia.Services;

public sealed record AvaloniaAppServices(
    LocalizationService Localization,
    HelpContentProvider HelpContentProvider,
    UserSettingsStore UserSettingsStore,
    RecentProjectsStore RecentProjectsStore,
    IProjectProfileStore ProjectProfileStore,
    IAppInstanceLauncher AppInstanceLauncher,
    IElevationService Elevation,
    ScanOptionsUseCase ScanOptionsUseCase,
    BuildTreeUseCase BuildTreeUseCase,
    IgnoreOptionsService IgnoreOptionsService,
    IgnoreRulesService IgnoreRulesService,
    FilterOptionSelectionService FilterOptionSelectionService,
    TreeExportService TreeExportService,
    SelectedContentExportService ContentExportService,
    TreeAndContentExportService TreeAndContentExportService,
    PreviewDocumentBuilder PreviewDocumentBuilder,
    RepositoryWebPathPresentationService RepositoryWebPathPresentationService,
    TextFileExportService TextFileExportService,
    IToastService ToastService,
    IIconStore IconStore,
    IGitRepositoryService GitRepositoryService,
    IRepoCacheService RepoCacheService,
    IZipDownloadService ZipDownloadService,
    IFileContentAnalyzer FileContentAnalyzer);
