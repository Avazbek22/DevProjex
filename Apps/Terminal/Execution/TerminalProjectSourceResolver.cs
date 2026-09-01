using DevProjex.Infrastructure.Git;
using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Execution;

internal sealed class TerminalProjectSourceResolver(
	TerminalServices services,
	ITerminalEnvironment environment,
	TerminalOutputOptions outputOptions)
{
	public async Task<ResolvedTerminalProjectSource> ResolveAsync(
		string source,
		string? branch,
		CancellationToken cancellationToken)
	{
		if (!LooksLikeRepositoryUrl(source))
		{
			if (branch is not null)
				throw TerminalProjectSourceException.Usage("DPX-CLI-GIT-BRANCH-LOCAL");
			return ResolvedTerminalProjectSource.Local(source);
		}
		if (!RepositoryUrlUtility.IsSupportedCloneSource(source))
			throw TerminalProjectSourceException.Usage("DPX-CLI-GIT-URL-INVALID");
		if (branch is not null && !GitBranchNameValidator.IsValid(branch))
			throw TerminalProjectSourceException.Usage("DPX-CLI-GIT-BRANCH-INVALID");

		var safeUrl = RepositoryUrlUtility.ToSafeDisplay(source);
		string? stagingPath = null;
		await using (await services.RepoCacheService
			.AcquireRepositoryOperationAsync(safeUrl, cancellationToken)
			.ConfigureAwait(false))
		{
			var cached = await TryAcquireSessionAsync(safeUrl, branch, cancellationToken)
				.ConfigureAwait(false);
			if (cached is not null)
				return ResolvedTerminalProjectSource.Repository(cached.RepositoryPath, safeUrl, source, cached);

			try
			{
				if (!await services.GitRepositoryService
					    .IsGitAvailableAsync(cancellationToken)
					    .ConfigureAwait(false))
				{
					throw new TerminalProjectSourceException("DPX-CLI-GIT-UNAVAILABLE");
				}

				stagingPath = services.RepoCacheService.CreateRepositoryStagingDirectory(safeUrl);
				using var progress = CreateProgress(safeUrl);
				progress?.Start();
				var result = await services.GitRepositoryService.CloneAsync(
						source.Trim(),
						stagingPath,
						progress,
						cancellationToken)
					.ConfigureAwait(false);
				if (!result.Success || !Directory.Exists(result.LocalPath))
					throw new TerminalProjectSourceException("DPX-CLI-GIT-CLONE-FAILED");

				var repositoryUrl = string.IsNullOrWhiteSpace(result.RepositoryUrl)
					? safeUrl
					: RepositoryUrlUtility.ToSafeDisplay(result.RepositoryUrl);
				var publishedPath = services.RepoCacheService.PublishRepositoryDirectory(
					stagingPath,
					repositoryUrl);
				stagingPath = null;
				services.RepoCacheService.RecordIndexedRepository(
					repositoryUrl,
					publishedPath,
					result.DefaultBranch);
				var session = await TryAcquireSessionAsync(repositoryUrl, branch, cancellationToken)
					.ConfigureAwait(false);
				if (session is null)
					throw new TerminalProjectSourceException("DPX-CLI-GIT-CACHE-FAILED");

				services.RecentProjectsStore.AddRepository(null, repositoryUrl);
				progress?.Complete();
				return ResolvedTerminalProjectSource.Repository(
					session.RepositoryPath,
					repositoryUrl,
					source,
					session);
			}
			catch
			{
				if (stagingPath is not null)
					services.RepoCacheService.DeleteRepositoryDirectory(stagingPath);
				throw;
			}
		}
	}

	private async Task<IRepositoryCacheSession?> TryAcquireSessionAsync(
		string repositoryUrl,
		string? branch,
		CancellationToken cancellationToken)
	{
		try
		{
			return await services.RepoCacheService
				.TryAcquireRepositorySessionAsync(repositoryUrl, branch, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (RepositoryBranchUnavailableException exception)
		{
			throw new TerminalProjectSourceException(
				"DPX-CLI-GIT-BRANCH-UNAVAILABLE",
				innerException: exception);
		}
	}

	private GitOperationProgressRenderer? CreateProgress(string safeUrl) =>
		GitOperationProgressRenderer.Create(
			environment,
			outputOptions,
			services.Localization.Format("Terminal.Progress.Clone.Start", safeUrl),
			services.Localization["Terminal.Progress.Clone.Completed"]);

	private static bool LooksLikeRepositoryUrl(string source)
	{
		if (string.IsNullOrWhiteSpace(source))
			return false;
		if (source.Contains("://", StringComparison.Ordinal))
			return true;
		if (Directory.Exists(source))
			return false;
		var colon = source.IndexOf(':');
		return colon > 0 &&
		       (source[..colon].Contains('@') || source[..colon].Contains('.'));
	}

}

internal sealed class ResolvedTerminalProjectSource(
	string projectPath,
	string? safeRepositoryUrl,
	string? repositorySourceUrl,
	IRepositoryCacheSession? session) : IAsyncDisposable
{
	public string ProjectPath { get; } = projectPath;
	public string? SafeRepositoryUrl { get; } = safeRepositoryUrl;
	public string? RepositorySourceUrl { get; } = repositorySourceUrl;
	public bool IsRepositoryUrl => SafeRepositoryUrl is not null;

	public static ResolvedTerminalProjectSource Local(string projectPath) => new(projectPath, null, null, null);

	public static ResolvedTerminalProjectSource Repository(
		string projectPath,
		string safeRepositoryUrl,
		string repositorySourceUrl,
		IRepositoryCacheSession session) =>
		new(projectPath, safeRepositoryUrl, repositorySourceUrl, session);

	public ValueTask DisposeAsync()
	{
		session?.Dispose();
		return ValueTask.CompletedTask;
	}
}

internal sealed class TerminalProjectSourceException(
	string code,
	int exitCode = CommandLineExitCodes.RuntimeError,
	Exception? innerException = null) : Exception(code, innerException)
{
	public string Code { get; } = code;
	public int ExitCode { get; } = exitCode;

	public static TerminalProjectSourceException Usage(string code) =>
		new(code, CommandLineExitCodes.UsageError);
}
