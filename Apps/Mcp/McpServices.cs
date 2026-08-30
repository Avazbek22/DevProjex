using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Mcp;

internal sealed class McpServices : IDisposable
{
	private McpServices(
		ProjectContextPlanner planner,
		ProjectContextDocumentService documentService,
		TreeExportService treeExportService,
		IFileContentAnalyzer contentAnalyzer,
		ProjectSelectionResolver selectionResolver,
		IProjectProfileStore profileStore,
		IGitScopePathProvider gitScopePathProvider,
		SecretRedactionSession redactionSession,
		CodeCompressionSession compressionSession,
		SecretRedactionOutputPreparer outputPreparer)
	{
		Planner = planner;
		DocumentService = documentService;
		TreeExportService = treeExportService;
		ContentAnalyzer = contentAnalyzer;
		SelectionResolver = selectionResolver;
		ProfileStore = profileStore;
		GitScopePathProvider = gitScopePathProvider;
		RedactionSession = redactionSession;
		CompressionSession = compressionSession;
		OutputPreparer = outputPreparer;
	}

	public ProjectContextPlanner Planner { get; }
	public ProjectContextDocumentService DocumentService { get; }
	public TreeExportService TreeExportService { get; }
	public IFileContentAnalyzer ContentAnalyzer { get; }
	public ProjectSelectionResolver SelectionResolver { get; }
	public IProjectProfileStore ProfileStore { get; }
	public IGitScopePathProvider GitScopePathProvider { get; }
	public SecretRedactionSession RedactionSession { get; }
	public CodeCompressionSession CompressionSession { get; }
	public SecretRedactionOutputPreparer OutputPreparer { get; }

	public static McpServices Create(
		McpRootRegistry roots,
		Func<string>? appDataPathProvider = null) =>
		Create(new McpProjectRootJail(roots), appDataPathProvider);

	internal static McpServices Create(
		McpProjectRootJail roots,
		Func<string>? appDataPathProvider = null)
	{
		ArgumentNullException.ThrowIfNull(roots);
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		var scanner = new FileSystemScanner();
		var treeBuilder = new TreeBuilder();
		var treePresenter = new TreeNodePresentationService(localization, new IconMapper());
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
		var treeExport = new TreeExportService();
		var guardedFileOpener = new McpRootJailFileStreamOpener(roots);
		var contentAnalyzer = new FileContentAnalyzer(guardedFileOpener.OpenRead);
		var resolvedDataPath = appDataPathProvider ??
		                       DevProjex.Infrastructure.Persistence.UserDataPathResolver.GetConfigurationRoot;
		var profileStore = new ProjectProfileStore(resolvedDataPath);
		var persistentIdentity = new PersistentSecretIdentityProvider(resolvedDataPath);
		SecretRedactionSession redactionSession;
		try
		{
			redactionSession = SecretRedactionSession.CreateWithPrivateData(
				new SmartSecretsDetector(new GitleaksSecretDetector(), smartIgnore),
				new PrivateDataDetector(),
				profileStore,
				persistentIdentity);
		}
		catch
		{
			persistentIdentity.Dispose();
			throw;
		}

		CodeCompressionSession compressionSession;
		try
		{
			compressionSession = CodeCompressionFactory.CreateSession();
		}
		catch
		{
			redactionSession.Dispose();
			throw;
		}
		var analysis = new ProjectAnalysisService(
			new ScanOptionsUseCase(scanner),
			new BuildTreeUseCase(treeBuilder, treePresenter),
			new FilterOptionSelectionService(),
			new IgnoreOptionsService(localization),
			new IgnoreRulesService(smartIgnore),
			treeExport,
			contentAnalyzer);
		string Omission(FileContentClassification classification) =>
			localization[FileContentClassificationCatalog.Get(classification).LabelKey];

		try
		{
			return new McpServices(
				new ProjectContextPlanner(analysis),
				new ProjectContextDocumentService(
					treeExport,
					contentAnalyzer,
					Omission,
					redactionSession,
					compressionSession),
				treeExport,
				contentAnalyzer,
				new ProjectSelectionResolver(profileStore, new PortableProjectProfileService().LoadAsync),
				profileStore,
				new GitScopePathProvider(),
				redactionSession,
				compressionSession,
				new SecretRedactionOutputPreparer(contentAnalyzer));
		}
		catch
		{
			compressionSession.Dispose();
			redactionSession.Dispose();
			throw;
		}
	}

	public void Dispose()
	{
		RedactionSession.Dispose();
		CompressionSession.Dispose();
	}
}
