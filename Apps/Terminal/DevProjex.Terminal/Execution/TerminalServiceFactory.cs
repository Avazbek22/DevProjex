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
		var contextDocumentService = new ProjectContextDocumentService(treeExport, contentAnalyzer);
		var projectCopyExportService = new ProjectCopyExportService(new ProjectCopyExportPlanBuilder());
		var localProfiles = new ProjectProfileStore(resolvedAppDataPathProvider);
		var portableProfiles = new PortableProjectProfileService();
		var selectionResolver = new ProjectSelectionResolver(
			localProfiles,
			portableProfiles.LoadAsync);

		return new TerminalServices(
			Localization: localization,
			AnalysisService: analysis,
			ContextPlanner: contextPlanner,
			ContextDocumentService: contextDocumentService,
			ProjectCopyExportService: projectCopyExportService,
			AnalysisReportWriter: new ProjectAnalysisReportWriter(),
			LocalProfileStore: localProfiles,
			PortableProfileService: portableProfiles,
			SelectionResolver: selectionResolver,
			TerminalSettingsStore: new TerminalSettingsStore(resolvedAppDataPathProvider),
			TerminalCommandSetupService: new TerminalCommandSetupService(),
			GitTrackedModeReadinessProbe: new GitTrackedModeReadinessProbe(),
			RecentProjectsStore: new RecentProjectsStore(resolvedAppDataPathProvider),
			GitRepositoryService: new GitRepositoryService(),
			RepoCacheService: new RepoCacheService());
	}
}
