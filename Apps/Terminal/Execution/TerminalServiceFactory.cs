using DevProjex.Terminal.Tui;
using DevProjex.Infrastructure.Persistence;
using DevProjex.Infrastructure.Secrets;
using DevProjex.Application.Secrets;

namespace DevProjex.Terminal.Execution;

public sealed class TerminalServiceFactory(
	Func<string>? appDataPathProvider = null)
{
	private readonly Func<AppLanguage, TerminalServices>? _servicesProvider;
	private readonly Action? _fullServiceCreationObserver;
	internal Func<string>? AppDataPathProvider => appDataPathProvider;

	internal TerminalServiceFactory(Func<AppLanguage, TerminalServices> servicesProvider)
		: this()
	{
		_servicesProvider = servicesProvider ??
			throw new ArgumentNullException(nameof(servicesProvider));
	}

	internal TerminalServiceFactory(
		Func<string> appDataPathProvider,
		Action fullServiceCreationObserver)
		: this(appDataPathProvider)
	{
		_fullServiceCreationObserver = fullServiceCreationObserver ??
			throw new ArgumentNullException(nameof(fullServiceCreationObserver));
	}

	public TerminalServices Create(AppLanguage language)
	{
		if (_servicesProvider is not null)
			return _servicesProvider(language);
		_fullServiceCreationObserver?.Invoke();

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
		var portableProfiles = new PortableProjectProfileService();
		var selectionResolver = new ProjectSelectionResolver(
			localProfiles,
			portableProfiles.LoadAsync);
		var repoCache = CreateRepositoryCache(resolvedAppDataPathProvider);
		var recentProjects = CreateRecentProjectsStore(resolvedAppDataPathProvider);
		var gitRepository = new GitRepositoryService();
		var sourceIdentityResolver = new ProjectSourceIdentityResolver(gitRepository, repoCache);
		var repositoryCacheCatalog = new RepositoryCacheCatalog(gitRepository, repoCache);
		var persistentSecretIdentity = new PersistentSecretIdentityProvider(resolvedAppDataPathProvider);
		SecretRedactionSession secretRedactionSession;
		try
		{
			secretRedactionSession = SecretRedactionSession.CreateWithPrivateData(
				new SmartSecretsDetector(new GitleaksSecretDetector(), smartIgnore),
				new PrivateDataDetector(),
				localProfiles,
				persistentSecretIdentity);
		}
		catch
		{
			persistentSecretIdentity.Dispose();
			throw;
		}

		CodeCompressionSession codeCompressionSession;
		try
		{
			codeCompressionSession = CodeCompressionFactory.CreateSession();
		}
		catch
		{
			secretRedactionSession.Dispose();
			throw;
		}

		try
		{
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
			var contextFactory = new TerminalProjectContextFactory(
				contextPlanner,
				sourceIdentityResolver,
				secretRedactionSession,
				new GitScopePathProvider(),
				new GitRemoteDiffRangeResolver());

			return new TerminalServices(
				Localization: localization,
				AnalysisService: analysis,
				IgnoreRulesService: ignoreRules,
				IgnoreOptionsService: ignoreOptions,
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
				SecretRedactionOutputPreparer: new SecretRedactionOutputPreparer(contentAnalyzer))
				.AttachOwnedLifetime();
		}
		catch
		{
			codeCompressionSession.Dispose();
			secretRedactionSession.Dispose();
			throw;
		}
	}

	internal TerminalServiceScope CreateScope(AppLanguage language) =>
		new(Create(language), ownsServices: _servicesProvider is null);

	internal TerminalCacheServiceScope CreateCacheScope(AppLanguage language)
	{
		if (_servicesProvider is not null)
		{
			var fullScope = CreateScope(language);
			return new TerminalCacheServiceScope(
				new TerminalCacheServices(
					fullScope.Services.Localization,
					fullScope.Services.RepoCacheService,
					fullScope.Services.GitRepositoryService),
				fullScope);
		}

		var resolvedAppDataPathProvider =
			appDataPathProvider ?? UserDataPathResolver.GetConfigurationRoot;
		var localization = new LocalizationService(new JsonLocalizationCatalog(), language);
		var repositoryCache = CreateRepositoryCache(resolvedAppDataPathProvider);
		return new TerminalCacheServiceScope(
			new TerminalCacheServices(
				localization,
				repositoryCache,
				new GitRepositoryService()),
			repositoryCache);
	}

	internal TerminalRecentServiceScope CreateRecentScope(AppLanguage language)
	{
		if (_servicesProvider is not null)
		{
			var fullScope = CreateScope(language);
			return new TerminalRecentServiceScope(
				new TerminalRecentServices(
					fullScope.Services.RecentProjectsStore,
					fullScope.Services.RecentWorkspacesService,
					fullScope.Services.Localization),
				fullScope);
		}

		var resolvedAppDataPathProvider =
			appDataPathProvider ?? UserDataPathResolver.GetConfigurationRoot;
		return new TerminalRecentServiceScope(
			new TerminalRecentServices(
				CreateRecentProjectsStore(resolvedAppDataPathProvider),
				new RecentWorkspacesService(),
				new LocalizationService(new JsonLocalizationCatalog(), language)));
	}

	private RepoCacheService CreateRepositoryCache(Func<string> resolvedAppDataPathProvider) =>
		appDataPathProvider is null
			? new RepoCacheService()
			: new RepoCacheService(Path.Combine(resolvedAppDataPathProvider(), "RepoCache"));

	private RecentProjectsStore CreateRecentProjectsStore(Func<string> resolvedAppDataPathProvider) =>
		appDataPathProvider is null
			? new RecentProjectsStore()
			: new RecentProjectsStore(resolvedAppDataPathProvider);
}

internal sealed record TerminalCacheServices(
	LocalizationService Localization,
	IRepoCacheService RepoCacheService,
	IGitRepositoryService GitRepositoryService);

internal sealed record TerminalRecentServices(
	RecentProjectsStore RecentProjectsStore,
	RecentWorkspacesService RecentWorkspacesService,
	LocalizationService Localization);

internal sealed class TerminalCacheServiceScope(
	TerminalCacheServices services,
	IDisposable ownedLifetime) : IDisposable
{
	private TerminalCacheServices? _services = services ??
		throw new ArgumentNullException(nameof(services));
	private IDisposable? _ownedLifetime = ownedLifetime ??
		throw new ArgumentNullException(nameof(ownedLifetime));

	public TerminalCacheServices Services =>
		_services ?? throw new ObjectDisposedException(nameof(TerminalCacheServiceScope));

	public void Dispose()
	{
		_services = null;
		Interlocked.Exchange(ref _ownedLifetime, null)?.Dispose();
	}
}

internal sealed class TerminalRecentServiceScope(
	TerminalRecentServices services,
	IDisposable? ownedLifetime = null) : IDisposable
{
	private TerminalRecentServices? _services = services ??
		throw new ArgumentNullException(nameof(services));
	private IDisposable? _ownedLifetime = ownedLifetime;

	public TerminalRecentServices Services =>
		_services ?? throw new ObjectDisposedException(nameof(TerminalRecentServiceScope));

	public void Dispose()
	{
		_services = null;
		Interlocked.Exchange(ref _ownedLifetime, null)?.Dispose();
	}
}

internal sealed class TerminalServiceScope(
	TerminalServices services,
	bool ownsServices) : IDisposable
{
	private TerminalServices? _services = services ?? throw new ArgumentNullException(nameof(services));

	public TerminalServices Services =>
		_services ?? throw new ObjectDisposedException(nameof(TerminalServiceScope));

	public void Dispose()
	{
		var servicesToRelease = Interlocked.Exchange(ref _services, null);
		if (ownsServices)
			servicesToRelease?.Dispose();
	}
}
