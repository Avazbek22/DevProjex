using DevProjex.Terminal.Tui;
using DevProjex.Infrastructure.Persistence;
using DevProjex.Infrastructure.Secrets;
using DevProjex.Application.Secrets;

namespace DevProjex.Terminal.Execution;

public sealed class TerminalServiceFactory(
	Func<string>? appDataPathProvider = null)
{
	public TerminalServices Create(AppLanguage language)
	{
		var resolvedAppDataPathProvider =
			appDataPathProvider ?? UserDataPathResolver.GetConfigurationRoot;
		var localization = new LocalizationService(new JsonLocalizationCatalog(), language);
		var gitPathComparisonSemantics = GitConfigPathComparisonSemanticsResolver.Instance;
		var scanner = new FileSystemScanner();
		var treeBuilder = new TreeBuilder();
		var iconMapper = new IconMapper();
		var treePresenter = new TreeNodePresentationService(localization, iconMapper);
		var scanOptions = new ScanOptionsUseCase(scanner);
		var buildTree = new BuildTreeUseCase(treeBuilder, treePresenter);
		var smartIgnore = new SmartIgnoreService(
		[
			new CommonSmartIgnoreRule(),
			new FrontendArtifactsIgnoreRule(),
			new DotNetArtifactsIgnoreRule(),
			new PythonArtifactsIgnoreRule(),
			new JvmArtifactsIgnoreRule(),
			new RustArtifactsIgnoreRule(),
			new GoArtifactsIgnoreRule(),
			new PhpArtifactsIgnoreRule(),
			new RubyArtifactsIgnoreRule()
		]);
		var ignoreOptions = new IgnoreOptionsService(localization);
		var ignoreRules = new IgnoreRulesService(
			smartIgnore,
			pathComparisonSemanticsResolver: gitPathComparisonSemantics);
		var selectionService = new FilterOptionSelectionService();
		var treeExport = new TreeExportService();
		var contentAnalyzer = new FileContentAnalyzer();
		var secretRedactionSession = new SecretRedactionSession(
			new GitleaksSecretDetector(),
			new SecretRedactionLegendText(
				localization["SecretRedaction.Legend.Summary"],
				localization["SecretRedaction.Legend.Placeholder"],
				localization["SecretRedaction.Legend.Notice"],
				localization["SecretRedaction.NoFindingsNotice"]));
		var analysis = new ProjectAnalysisService(
			scanOptions,
			buildTree,
			selectionService,
			ignoreOptions,
			ignoreRules,
			treeExport,
			contentAnalyzer);
		var contextPlanner = new ProjectContextPlanner(analysis);
		string ResolveContentClassification(FileContentClassification classification) =>
			localization[FileContentClassificationCatalog.Get(classification).LabelKey];
		var contextDocumentService = new ProjectContextDocumentService(
			treeExport,
			contentAnalyzer,
			ResolveContentClassification,
			secretRedactionSession);
		var projectCopyExportService = new ProjectCopyExportService(
			new ProjectCopyExportPlanBuilder(),
			contentAnalyzer,
			secretRedactionSession);
		var localProfiles = new ProjectProfileStore(resolvedAppDataPathProvider);
		var portableProfiles = new PortableProjectProfileService();
		var selectionResolver = new ProjectSelectionResolver(
			localProfiles,
			portableProfiles.LoadAsync);
		var repoCache = appDataPathProvider is null
			? new RepoCacheService()
			: new RepoCacheService(Path.Combine(resolvedAppDataPathProvider(), "RepoCache"));
		var recentProjects = appDataPathProvider is null
			? new RecentProjectsStore()
			: new RecentProjectsStore(resolvedAppDataPathProvider);
		var gitRepository = new GitRepositoryService();
		var sourceIdentityResolver = new ProjectSourceIdentityResolver(gitRepository, repoCache);
		var repositoryCacheCatalog = new RepositoryCacheCatalog(gitRepository, repoCache);
		var contextFactory = new TerminalProjectContextFactory(contextPlanner, sourceIdentityResolver);

		return new TerminalServices(
			Localization: localization,
			AnalysisService: analysis,
			IgnoreRulesService: ignoreRules,
			ContextPlanner: contextPlanner,
			ContextFactory: contextFactory,
			SourceIdentityResolver: sourceIdentityResolver,
			RepositoryCacheCatalog: repositoryCacheCatalog,
			ContextDocumentService: contextDocumentService,
			TreeExportService: treeExport,
			PreviewDocumentBuilder: new PreviewDocumentBuilder(
				contentAnalyzer,
				ResolveContentClassification),
			ProjectCopyExportService: projectCopyExportService,
			AnalysisReportWriter: new ProjectAnalysisReportWriter(),
			LocalProfileStore: localProfiles,
			PortableProfileService: portableProfiles,
			SelectionResolver: selectionResolver,
			TerminalSettingsStore: new TerminalSettingsStore(resolvedAppDataPathProvider),
			TerminalCommandSetupService: new TerminalCommandSetupService(),
			GitTrackedModeReadinessProbe: new GitTrackedModeReadinessProbe(),
			RecentWorkspacesService: new RecentWorkspacesService(),
			RecentProjectsStore: recentProjects,
			GitRepositoryService: gitRepository,
			RepoCacheService: repoCache,
			SecretRedactionSession: secretRedactionSession,
			SecretRedactionOutputPreparer: new SecretRedactionOutputPreparer(contentAnalyzer));
	}
}
