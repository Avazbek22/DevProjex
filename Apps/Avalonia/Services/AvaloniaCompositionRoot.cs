using DevProjex.Infrastructure.Elevation;
using DevProjex.Infrastructure.FileSystem;
using DevProjex.Infrastructure.Git;
using DevProjex.Infrastructure.ProjectProfiles;
using DevProjex.Infrastructure.RecentProjects;
using DevProjex.Infrastructure.AppInstances;
using DevProjex.Infrastructure.Persistence;
using DevProjex.Infrastructure.SmartIgnore;
using DevProjex.Infrastructure.ThemePresets;
using DevProjex.Infrastructure.TerminalCommands;
using DevProjex.Infrastructure.Updates;
using DevProjex.Infrastructure.Secrets;
using DevProjex.Infrastructure.Compression;

namespace DevProjex.Avalonia.Services;

public static class AvaloniaCompositionRoot
{
    public static AvaloniaAppServices CreateDefault(DesktopStartupOptions options)
        => CreateDefault(options, appDataPathProvider: null);

    public static AvaloniaAppServices CreateDefault(
        DesktopStartupOptions options,
        Func<string>? appDataPathProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        var language = options.OpenRequest?.Language ?? AppLanguageUtility.DetectSystemLanguage();
        return CreateDefaultCore(language, options.EffectiveSessionMetrics, appDataPathProvider);
    }

    private static AvaloniaAppServices CreateDefaultCore(
        AppLanguage language,
        SessionMetricsOptions sessionMetrics,
        Func<string>? appDataPathProvider)
    {
        var desktopPlatform = DesktopPlatformResolver.Resolve();
        var localizationCatalog = new JsonLocalizationCatalog();
        var localization = new LocalizationService(
            localizationCatalog,
            language,
            text => DesktopShortcutTextFormatter.Format(text, desktopPlatform));
        var helpContentProvider = new HelpContentProvider(desktopPlatform);
        var iconStore = new EmbeddedIconStore();
        var iconMapper = new IconMapper();
        var treePresenter = new TreeNodePresentationService(localization, iconMapper);
        var gitPathComparisonSemantics = GitConfigPathComparisonSemanticsResolver.Instance;
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
            new RubyArtifactsIgnoreRule(),
            new SwiftArtifactsIgnoreRule(),
            new DartArtifactsIgnoreRule()
        };
        var smartIgnoreService = new SmartIgnoreService(smartIgnoreRules);
        var ignoreOptionsService = new IgnoreOptionsService(localization);
        var ignoreRulesService = new IgnoreRulesService(
            smartIgnoreService,
            pathComparisonSemanticsResolver: gitPathComparisonSemantics);
        var ignoreOwnershipAuditService = new IgnoreOwnershipAuditService();
        var filterSelectionService = new FilterOptionSelectionService();
        var treeExportService = new TreeExportService();
        var fileContentAnalyzer = new FileContentAnalyzer();
		var projectProfileStore = new ProjectProfileStore(appDataPathProvider);
		var persistentSecretIdentity = new PersistentSecretIdentityProvider(appDataPathProvider);
		var secretRedactionSession = SecretRedactionSession.CreateWithPrivateData(
			new SmartSecretsDetector(new GitleaksSecretDetector(), smartIgnoreService),
			new PrivateDataDetector(),
			projectProfileStore,
			persistentSecretIdentity);
		var codeCompressionSession = CodeCompressionFactory.CreateSession();
        var contentExportService = new SelectedContentExportService(fileContentAnalyzer);
        var treeAndContentExportService = new TreeAndContentExportService(treeExportService, contentExportService);
        var projectCopyExportService = new ProjectCopyExportService(
			new ProjectCopyExportPlanBuilder(),
			fileContentAnalyzer,
			secretRedactionSession,
			codeCompressionSession);
        var projectAnalysisService = new ProjectAnalysisService(
            scanOptionsUseCase,
            buildTreeUseCase,
            filterSelectionService,
            ignoreOptionsService,
            ignoreRulesService,
            treeExportService,
            fileContentAnalyzer);
        var terminalCommandSetupService = new TerminalCommandSetupService();
        var localAppDataProvider = appDataPathProvider ?? UserDataPathResolver.GetStateRoot;
        var sessionMetricsRecorder = sessionMetrics.Enabled
            ? new SessionMetricsRecorder(sessionMetrics, localAppDataProvider)
            : SessionMetricsRecorder.Disabled;
        var previewDocumentBuilder = new PreviewDocumentBuilder(
            fileContentAnalyzer,
            classification => localization[
                FileContentClassificationCatalog.Get(classification).LabelKey]);
        var repositoryWebPathPresentationService = new RepositoryWebPathPresentationService();
        var textFileExportService = new TextFileExportService();
        var toastService = new ToastService();
        var elevation = new ElevationService();
        var appInstanceLauncher = new AppInstanceLauncher();
        var projectPathLauncher = new ProjectPathLauncher();
        // UI tests need an isolated app-data root so persisted settings/profiles from
        // previous runs cannot leak into the current window state and make workflow
        // scenarios nondeterministic on CI.
        var userSettingsStore = new UserSettingsStore(appDataPathProvider);
        var themeSettingsStore = new ThemeSettingsStore(appDataPathProvider);
        var recentProjectsStore = new RecentProjectsStore(appDataPathProvider);
        var recentFolderAvailabilityService = new RecentFolderAvailabilityService();
        var gitRepositoryService = new GitRepositoryService();
        var repoCacheService = new RepoCacheService();
        var zipDownloadService = new ZipDownloadService();
        var applicationUpdateService = new GitHubReleaseUpdateService();
        ITaskbarProgressService taskbarProgressService = OperatingSystem.IsWindows()
            ? new WindowsTaskbarProgressService()
            : new NoopTaskbarProgressService();

        return new AvaloniaAppServices(
            Localization: localization,
            HelpContentProvider: helpContentProvider,
            UserSettingsStore: userSettingsStore,
            ThemeSettingsStore: themeSettingsStore,
            RecentProjectsStore: recentProjectsStore,
            RecentWorkspacesService: new RecentWorkspacesService(),
            RecentFolderAvailabilityService: recentFolderAvailabilityService,
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
            ProjectCopyExportService: projectCopyExportService,
            PreviewDocumentBuilder: previewDocumentBuilder,
            RepositoryWebPathPresentationService: repositoryWebPathPresentationService,
            TextFileExportService: textFileExportService,
            ToastService: toastService,
            IconStore: iconStore,
            GitRepositoryService: gitRepositoryService,
            RepoCacheService: repoCacheService,
            ZipDownloadService: zipDownloadService,
            FileContentAnalyzer: fileContentAnalyzer,
            ProjectAnalysisService: projectAnalysisService,
            ApplicationUpdateService: applicationUpdateService,
            TerminalCommandSetupService: terminalCommandSetupService,
            TaskbarProgressService: taskbarProgressService,
            SessionMetricsRecorder: sessionMetricsRecorder,
			SecretRedactionSession: secretRedactionSession,
			CodeCompressionSession: codeCompressionSession,
			ProjectPathLauncher: projectPathLauncher);
    }
}
