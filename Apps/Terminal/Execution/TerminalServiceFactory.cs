using DevProjex.Terminal.Tui;
using DevProjex.Infrastructure.Persistence;
using DevProjex.Infrastructure.Secrets;
using DevProjex.Application.Secrets;

namespace DevProjex.Terminal.Execution;

public sealed class TerminalServiceFactory(
	Func<string>? appDataPathProvider = null)
{
	private readonly Func<AppLanguage, TerminalServices>? _servicesProvider;

	internal TerminalServiceFactory(Func<AppLanguage, TerminalServices> servicesProvider)
		: this()
	{
		_servicesProvider = servicesProvider ??
			throw new ArgumentNullException(nameof(servicesProvider));
	}

	public TerminalServices Create(AppLanguage language)
	{
		if (_servicesProvider is not null)
			return _servicesProvider(language);

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
			new RubyArtifactsIgnoreRule(),
			new SwiftArtifactsIgnoreRule(),
			new DartArtifactsIgnoreRule()
		]);
		var ignoreOptions = new IgnoreOptionsService(localization);
		var ignoreRules = new IgnoreRulesService(
			smartIgnore,
			pathComparisonSemanticsResolver: gitPathComparisonSemantics);
		var selectionService = new FilterOptionSelectionService();
		var treeExport = new TreeExportService();
		var contentAnalyzer = new FileContentAnalyzer();
		var localProfiles = new ProjectProfileStore(resolvedAppDataPathProvider);
		var persistentSecretIdentity = new PersistentSecretIdentityProvider(resolvedAppDataPathProvider);
		var secretRedactionSession = SecretRedactionSession.CreateWithPrivateData(
			new SmartSecretsDetector(new GitleaksSecretDetector(), smartIgnore),
			new PrivateDataDetector(),
			localProfiles,
			persistentSecretIdentity);
		var codeCompressionSession = CodeCompressionFactory.CreateSession();
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
			secretRedactionSession,
			codeCompressionSession);
		var projectCopyExportService = new ProjectCopyExportService(
			new ProjectCopyExportPlanBuilder(),
			contentAnalyzer,
			secretRedactionSession,
			codeCompressionSession);
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
		var contextFactory = new TerminalProjectContextFactory(
			contextPlanner,
			sourceIdentityResolver,
			secretRedactionSession);

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
			CodeCompressionSession: codeCompressionSession,
			SecretRedactionOutputPreparer: new SecretRedactionOutputPreparer(contentAnalyzer));
	}
}
