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
		DependencyFactsEngine dependencyFactsEngine,
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
		DependencyFactsEngine = dependencyFactsEngine;
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
	public DependencyFactsEngine DependencyFactsEngine { get; }
	public SecretRedactionOutputPreparer OutputPreparer { get; }

	public static McpServices Create(
		McpRootRegistry roots,
		Func<string>? appDataPathProvider = null) =>
		Create(new McpProjectRootJail(roots), appDataPathProvider);

	internal static McpServices Create(
		McpProjectRootJail roots,
		Func<string>? appDataPathProvider = null,
		DependencyFactsEngine? dependencyFactsEngine = null)
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
		var gitPathComparisonSemanticsResolver = GitConfigPathComparisonSemanticsResolver.Instance;
		var guardedFileOpener = new McpRootJailFileStreamOpener(roots);
		var contentAnalyzer = new FileContentAnalyzer(guardedFileOpener.OpenRead);
		var preparedContentAnalyzer = new FileContentAnalyzer();
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
		DependencyFactsEngine resolvedDependencyFactsEngine;
		try
		{
			resolvedDependencyFactsEngine = dependencyFactsEngine ?? new DependencyFactsEngine(
				new TreeSitterDependencyFactExtractor(guardedFileOpener.OpenRead),
				new FileDependencyConfigurationProvider(guardedFileOpener.OpenRead));
		}
		catch
		{
			compressionSession.Dispose();
			redactionSession.Dispose();
			throw;
		}
		var analysis = new ProjectAnalysisService(
			new ScanOptionsUseCase(scanner),
			new BuildTreeUseCase(treeBuilder, treePresenter),
			new FilterOptionSelectionService(),
			new IgnoreOptionsService(localization),
			new IgnoreRulesService(
				smartIgnore,
				pathComparisonSemanticsResolver: gitPathComparisonSemanticsResolver),
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
					compressionSession,
					preparedContentAnalyzer: preparedContentAnalyzer),
				treeExport,
				contentAnalyzer,
				new ProjectSelectionResolver(
					profileStore,
					(path, token) => LoadPortableProfileAsync(
						guardedFileOpener,
						resolvedDataPath(),
						path,
						token)),
				profileStore,
				new GitScopePathProvider(gitPathComparisonSemanticsResolver),
				redactionSession,
				compressionSession,
				resolvedDependencyFactsEngine,
				new SecretRedactionOutputPreparer(contentAnalyzer, preparedContentAnalyzer));
		}
		catch
		{
			resolvedDependencyFactsEngine.Dispose();
			compressionSession.Dispose();
			redactionSession.Dispose();
			throw;
		}
	}

	private static async Task<ProjectSelectionSpec> LoadPortableProfileAsync(
		McpRootJailFileStreamOpener guardedFileOpener,
		string userDataRoot,
		string path,
		CancellationToken cancellationToken)
	{
		var stagingDirectory = CreatePrivateProfileStagingDirectory(userDataRoot);
		var stagingPath = Path.Combine(stagingDirectory, $"{Guid.NewGuid():N}.json");
		try
		{
			await using (var source = guardedFileOpener.OpenRead(
				path,
				16 * 1024,
				FileShare.ReadWrite | FileShare.Delete,
				asynchronous: true))
			{
				await using (var destination = CreatePrivateProfileStagingFile(stagingPath))
				{
					var buffer = new byte[16 * 1024];
					long copied = 0;
					try
					{
						while (true)
						{
							var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
							if (read == 0)
								break;
							copied += read;
							if (copied > 4L * 1024 * 1024)
								throw new PortableProjectProfileException(
									"DPX-CLI-PROFILE-INVALID",
									"The portable profile could not be read.");
							await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
								.ConfigureAwait(false);
						}
					}
					finally
					{
						System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
					}
				}
			}
			return await new PortableProjectProfileService()
				.LoadAsync(stagingPath, cancellationToken)
				.ConfigureAwait(false);
		}
		finally
		{
			try
			{
				File.Delete(stagingPath);
				Directory.Delete(stagingDirectory, recursive: false);
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
	}

	internal static string CreatePrivateProfileStagingDirectory(string userDataRoot)
	{
		var fullDataRoot = Path.GetFullPath(userDataRoot);
		if (Directory.Exists(fullDataRoot) &&
			(File.GetAttributes(fullDataRoot) & FileAttributes.ReparsePoint) != 0)
		{
			throw UnsafeProfileStagingDirectory();
		}
		Directory.CreateDirectory(fullDataRoot);
		if ((File.GetAttributes(fullDataRoot) & FileAttributes.ReparsePoint) != 0)
			throw UnsafeProfileStagingDirectory();
		var stagingRoot = Path.Combine(fullDataRoot, "mcp-profile-staging");
		if (Directory.Exists(stagingRoot) &&
			(File.GetAttributes(stagingRoot) & FileAttributes.ReparsePoint) != 0)
		{
			throw UnsafeProfileStagingDirectory();
		}
		if (OperatingSystem.IsWindows())
			Directory.CreateDirectory(stagingRoot);
		else
			Directory.CreateDirectory(stagingRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		if ((File.GetAttributes(stagingRoot) & FileAttributes.ReparsePoint) != 0)
			throw UnsafeProfileStagingDirectory();
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(
				stagingRoot,
				UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
		return stagingRoot;
	}

	private static PortableProjectProfileException UnsafeProfileStagingDirectory() =>
		new(
			"DPX-CLI-PROFILE-INVALID",
			"The portable profile staging directory is unsafe.");

	internal static FileStream CreatePrivateProfileStagingFile(string stagingPath)
	{
		var options = new FileStreamOptions
		{
			Mode = FileMode.CreateNew,
			Access = FileAccess.Write,
			Share = FileShare.None,
			BufferSize = 16 * 1024,
			Options = FileOptions.Asynchronous | FileOptions.SequentialScan
		};
		if (!OperatingSystem.IsWindows())
			options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		return new FileStream(stagingPath, options);
	}

	public void Dispose()
	{
		RedactionSession.Dispose();
		CompressionSession.Dispose();
		DependencyFactsEngine.Dispose();
	}
}
