using DevProjex.Terminal.Tui;
using DevProjex.Application.Secrets;

namespace DevProjex.Terminal.Execution;

public sealed record TerminalServices(
	TerminalHostCapabilities HostCapabilities,
	LocalizationService Localization,
	ProjectAnalysisService AnalysisService,
	IgnoreRulesService IgnoreRulesService,
	IgnoreOptionsService IgnoreOptionsService,
	ProjectContextPlanner ContextPlanner,
	TerminalProjectContextFactory ContextFactory,
	ProjectSourceIdentityResolver SourceIdentityResolver,
	RepositoryCacheCatalog RepositoryCacheCatalog,
	ProjectContextDocumentService ContextDocumentService,
	TreeExportService TreeExportService,
	PreviewDocumentBuilder PreviewDocumentBuilder,
	ProjectCopyExportService ProjectCopyExportService,
	ProjectAnalysisReportWriter AnalysisReportWriter,
	IProjectProfileStore LocalProfileStore,
	PortableProjectProfileService PortableProfileService,
	ProjectSelectionResolver SelectionResolver,
	TerminalSettingsStore TerminalSettingsStore,
	ITerminalCommandSetupService TerminalCommandSetupService,
	GitTrackedModeReadinessProbe GitTrackedModeReadinessProbe,
	RecentWorkspacesService RecentWorkspacesService,
	RecentProjectsStore RecentProjectsStore,
	IGitRepositoryService GitRepositoryService,
	IRepoCacheService RepoCacheService,
	SecretRedactionSession SecretRedactionSession,
	CodeCompressionSession CodeCompressionSession,
	SecretRedactionOutputPreparer SecretRedactionOutputPreparer) : IDisposable
{
	private OwnedLifetime? _ownedLifetime;

	internal TerminalServices AttachOwnedLifetime()
	{
		var lifetime = new OwnedLifetime(
			RepoCacheService as IDisposable,
			SecretRedactionSession,
			CodeCompressionSession);
		if (Interlocked.CompareExchange(ref _ownedLifetime, lifetime, null) is not null)
			throw new InvalidOperationException("Terminal service ownership is already configured.");

		return this;
	}

	public void Dispose() => Interlocked.Exchange(ref _ownedLifetime, null)?.Dispose();

	private sealed class OwnedLifetime(
		IDisposable? repoCacheLifetime,
		SecretRedactionSession secretRedactionSession,
		CodeCompressionSession codeCompressionSession) : IDisposable
	{
		private IDisposable? _repoCacheLifetime = repoCacheLifetime;
		private SecretRedactionSession? _secretRedactionSession = secretRedactionSession;
		private CodeCompressionSession? _codeCompressionSession = codeCompressionSession;

		public void Dispose()
		{
			var cacheLifetime = Interlocked.Exchange(ref _repoCacheLifetime, null);
			var redactionSession = Interlocked.Exchange(ref _secretRedactionSession, null);
			var compressionSession = Interlocked.Exchange(ref _codeCompressionSession, null);
			try
			{
				cacheLifetime?.Dispose();
			}
			finally
			{
				try
				{
					redactionSession?.Dispose();
				}
				finally
				{
					compressionSession?.Dispose();
				}
			}
		}
	}
}
