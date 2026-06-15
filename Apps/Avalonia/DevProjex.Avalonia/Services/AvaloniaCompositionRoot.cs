using DevProjex.Infrastructure.Elevation;
using DevProjex.Infrastructure.FileSystem;
using DevProjex.Infrastructure.Git;
using DevProjex.Infrastructure.ProjectProfiles;
using DevProjex.Infrastructure.RecentProjects;
using DevProjex.Infrastructure.AppInstances;
using DevProjex.Infrastructure.SmartIgnore;
using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Avalonia.Services;

public static class AvaloniaCompositionRoot
{
    public static AvaloniaAppServices CreateDefault(CommandLineOptions options)
        => CreateDefault(options, appDataPathProvider: null);

    public static AvaloniaAppServices CreateDefault(
        CommandLineOptions options,
        Func<string>? appDataPathProvider)
    {
        var localizationCatalog = new JsonLocalizationCatalog();
        var localization = new LocalizationService(localizationCatalog, options.Language ?? CommandLineOptions.DetectSystemLanguage());
        var helpContentProvider = new HelpContentProvider();
        var iconStore = new EmbeddedIconStore();
        var iconMapper = new IconMapper();
        var treePresenter = new TreeNodePresentationService(localization, iconMapper);
        var scanner = new FileSystemScanner();
        var treeBuilder = new TreeBuilder();
        var scanOptionsUseCase = new ScanOptionsUseCase(scanner);
        var buildTreeUseCase = new BuildTreeUseCase(treeBuilder, treePresenter);
        var smartIgnoreRules = new ISmartIgnoreRule[]
        {
            new CommonSmartIgnoreRule(),
            new FrontendArtifactsIgnoreRule(),
            new DotNetArtifactsIgnoreRule(),
            new PythonArtifactsIgnoreRule(),
            new JvmArtifactsIgnoreRule(),
            new RustArtifactsIgnoreRule(),
            new GoArtifactsIgnoreRule(),
            new PhpArtifactsIgnoreRule(),
            new RubyArtifactsIgnoreRule()
        };
        var smartIgnoreService = new SmartIgnoreService(smartIgnoreRules);
        var ignoreOptionsService = new IgnoreOptionsService(localization);
        var ignoreRulesService = new IgnoreRulesService(smartIgnoreService);
        var ignoreOwnershipAuditService = new IgnoreOwnershipAuditService();
        var filterSelectionService = new FilterOptionSelectionService();
        var treeExportService = new TreeExportService();
        var fileContentAnalyzer = new FileContentAnalyzer();
        var contentExportService = new SelectedContentExportService(fileContentAnalyzer);
        var treeAndContentExportService = new TreeAndContentExportService(treeExportService, contentExportService);
        var previewDocumentBuilder = new PreviewDocumentBuilder(fileContentAnalyzer);
        var repositoryWebPathPresentationService = new RepositoryWebPathPresentationService();
        var textFileExportService = new TextFileExportService();
        var toastService = new ToastService();
        var elevation = new ElevationService();
        var appInstanceLauncher = new AppInstanceLauncher();
        // UI tests need an isolated app-data root so persisted settings/profiles from
        // previous runs cannot leak into the current window state and make workflow
        // scenarios nondeterministic on CI.
        var userSettingsStore = new UserSettingsStore(appDataPathProvider);
        var recentProjectsStore = new RecentProjectsStore(appDataPathProvider);
        var projectProfileStore = new ProjectProfileStore(appDataPathProvider);
        var gitRepositoryService = new GitRepositoryService();
        var repoCacheService = new RepoCacheService();
        var zipDownloadService = new ZipDownloadService();
        ITaskbarProgressService taskbarProgressService = OperatingSystem.IsWindows()
            ? new WindowsTaskbarProgressService()
            : new NoopTaskbarProgressService();

        return new AvaloniaAppServices(
            Localization: localization,
            HelpContentProvider: helpContentProvider,
            UserSettingsStore: userSettingsStore,
            RecentProjectsStore: recentProjectsStore,
            ProjectProfileStore: projectProfileStore,
            AppInstanceLauncher: appInstanceLauncher,
            Elevation: elevation,
            ScanOptionsUseCase: scanOptionsUseCase,
            BuildTreeUseCase: buildTreeUseCase,
            IgnoreOptionsService: ignoreOptionsService,
            IgnoreRulesService: ignoreRulesService,
            IgnoreOwnershipAuditService: ignoreOwnershipAuditService,
            FilterOptionSelectionService: filterSelectionService,
            TreeExportService: treeExportService,
            ContentExportService: contentExportService,
            TreeAndContentExportService: treeAndContentExportService,
            PreviewDocumentBuilder: previewDocumentBuilder,
            RepositoryWebPathPresentationService: repositoryWebPathPresentationService,
            TextFileExportService: textFileExportService,
            ToastService: toastService,
            IconStore: iconStore,
            GitRepositoryService: gitRepositoryService,
            RepoCacheService: repoCacheService,
            ZipDownloadService: zipDownloadService,
            FileContentAnalyzer: fileContentAnalyzer,
            TaskbarProgressService: taskbarProgressService);
    }
}
