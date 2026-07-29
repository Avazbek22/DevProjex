using DevProjex.Terminal.Tui;
using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Terminal.Execution;

public sealed class TerminalServiceFactory(
	Func<string>? appDataPathProvider = null)
{
	public TerminalServices Create(AppLanguage language)
	{
		var resolvedAppDataPathProvider =
			appDataPathProvider ?? UserDataPathResolver.GetConfigurationRoot;
		var localization = new LocalizationService(new JsonLocalizationCatalog(), language);
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
		var ignoreRules = new IgnoreRulesService(smartIgnore);
		var selectionService = new FilterOptionSelectionService();
		var treeExport = new TreeExportService();
		var contentAnalyzer = new FileContentAnalyzer();
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
			ResolveContentClassification);
		var projectCopyExportService = new ProjectCopyExportService(new ProjectCopyExportPlanBuilder());
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
			RepoCacheService: repoCache);
	}
}
