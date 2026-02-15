using DevProjex.Application.Services;
using DevProjex.Application.UseCases;
using DevProjex.Infrastructure.ResourceStore;
using DevProjex.Infrastructure.ThemePresets;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Avalonia.Services;

public sealed record AvaloniaAppServices(
    LocalizationService Localization,
    HelpContentProvider HelpContentProvider,
    ThemePresetStore ThemePresetStore,
    IElevationService Elevation,
    ScanOptionsUseCase ScanOptionsUseCase,
    BuildTreeUseCase BuildTreeUseCase,
    IgnoreOptionsService IgnoreOptionsService,
    IgnoreRulesService IgnoreRulesService,
    FilterOptionSelectionService FilterOptionSelectionService,
    TreeExportService TreeExportService,
    SelectedContentExportService ContentExportService,
    TreeAndContentExportService TreeAndContentExportService,
    TextFileExportService TextFileExportService,
    IToastService ToastService,
    IIconStore IconStore,
    IGitRepositoryService GitRepositoryService,
    IRepoCacheService RepoCacheService,
    IZipDownloadService ZipDownloadService,
    IFileContentAnalyzer FileContentAnalyzer);
