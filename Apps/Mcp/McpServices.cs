using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Mcp;

internal sealed class McpServices : IDisposable
{
	private readonly PersistentSecretIdentityProvider _persistentIdentity;

	private McpServices(
		ProjectContextPlanner planner,
		ProjectContextDocumentService documentService,
		TreeExportService treeExportService,
		IFileContentAnalyzer contentAnalyzer,
		ProjectSelectionResolver selectionResolver,
		IProjectProfileStore profileStore,
		SecretRedactionSession redactionSession,
		CodeCompressionSession compressionSession,
		SecretRedactionOutputPreparer outputPreparer,
		PersistentSecretIdentityProvider persistentIdentity)
	{
		Planner = planner;
		DocumentService = documentService;
		TreeExportService = treeExportService;
		ContentAnalyzer = contentAnalyzer;
		SelectionResolver = selectionResolver;
		ProfileStore = profileStore;
		RedactionSession = redactionSession;
		CompressionSession = compressionSession;
		OutputPreparer = outputPreparer;
		_persistentIdentity = persistentIdentity;
	}

	public ProjectContextPlanner Planner { get; }
	public ProjectContextDocumentService DocumentService { get; }
	public TreeExportService TreeExportService { get; }
	public IFileContentAnalyzer ContentAnalyzer { get; }
	public ProjectSelectionResolver SelectionResolver { get; }
	public IProjectProfileStore ProfileStore { get; }
	public SecretRedactionSession RedactionSession { get; }
	public CodeCompressionSession CompressionSession { get; }
	public SecretRedactionOutputPreparer OutputPreparer { get; }

	public static McpServices Create(Func<string>? appDataPathProvider = null)
	{
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
		var contentAnalyzer = new FileContentAnalyzer();
		var resolvedDataPath = appDataPathProvider ??
		                       DevProjex.Infrastructure.Persistence.UserDataPathResolver.GetConfigurationRoot;
		var profileStore = new ProjectProfileStore(resolvedDataPath);
		var persistentIdentity = new PersistentSecretIdentityProvider(resolvedDataPath);
		var redactionSession = SecretRedactionSession.CreateWithPrivateData(
			new SmartSecretsDetector(new GitleaksSecretDetector(), smartIgnore),
			new PrivateDataDetector(),
			profileStore,
			persistentIdentity);
		var compressionSession = CodeCompressionFactory.CreateSession();
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
			redactionSession,
			compressionSession,
			new SecretRedactionOutputPreparer(contentAnalyzer),
			persistentIdentity);
	}

	public void Dispose()
	{
		RedactionSession.Dispose();
		CompressionSession.Dispose();
		_persistentIdentity.Dispose();
	}
}
