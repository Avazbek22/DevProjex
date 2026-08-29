namespace DevProjex.Mcp;

internal sealed class McpProjectSourceResolver : IDisposable
{
	private readonly McpRootRegistry _localRoots;
	private readonly bool _allowRemote;
	private readonly Lazy<McpRemoteProjectServices> _remoteServices;
	private readonly object _sync = new();
	private readonly Dictionary<RemoteProjectKey, McpResolvedProjectSource> _remoteSources = [];
	private readonly Dictionary<string, McpResolvedProjectSource> _remoteRoots =
		new(PathComparer.Default);
	private bool _disposed;

	public McpProjectSourceResolver(
		McpRootRegistry localRoots,
		bool allowRemote,
		Func<McpRemoteProjectServices> remoteServicesFactory)
	{
		_localRoots = localRoots ?? throw new ArgumentNullException(nameof(localRoots));
		_allowRemote = allowRemote;
		ArgumentNullException.ThrowIfNull(remoteServicesFactory);
		_remoteServices = new Lazy<McpRemoteProjectServices>(
			remoteServicesFactory,
			LazyThreadSafetyMode.ExecutionAndPublication);
	}

	public async Task<McpResolvedProjectSource> ResolveAsync(
		string? project,
		string? branch,
		CancellationToken cancellationToken)
	{
		ThrowIfDisposed();
		if (!LooksLikeRepositoryUrl(project))
		{
			if (branch is not null)
			{
				throw new McpToolException(
					McpErrorCodes.InvalidArguments,
					$"{McpErrorCodes.InvalidArguments}: 'branch' is valid only when 'project' is a Git URL.");
			}

			var localRoot = _localRoots.ResolveProject(project);
			return McpResolvedProjectSource.Local(localRoot, _localRoots);
		}

		var safeUrl = RepositoryUrlUtility.ToSafeDisplay(project);
		if (!_allowRemote)
		{
			throw new McpToolException(
				McpErrorCodes.RemoteDisabled,
				$"{McpErrorCodes.RemoteDisabled}: remote project '{DisplayRemote(safeUrl)}' is unavailable because the MCP server was started without --allow-remote.");
		}
		if (!RepositoryUrlUtility.IsSupportedCloneSource(project) || safeUrl.Length == 0)
		{
			throw new McpToolException(
				McpErrorCodes.InvalidArguments,
				$"{McpErrorCodes.InvalidArguments}: 'project' is not a supported Git URL.");
		}
		if (branch is not null && !GitBranchNameValidator.IsValid(branch))
		{
			throw new McpToolException(
				McpErrorCodes.InvalidArguments,
				$"{McpErrorCodes.InvalidArguments}: 'branch' is not a valid Git branch name.");
		}

		var key = new RemoteProjectKey(
			RepositoryUrlUtility.GetComparisonKey(safeUrl),
			branch ?? string.Empty);
		lock (_sync)
		{
			if (_remoteSources.TryGetValue(key, out var existing))
				return existing;
		}

		return await AcquireRemoteAsync(project!, safeUrl, branch, key, cancellationToken)
			.ConfigureAwait(false);
	}

	public bool TryGetRemoteRoot(string projectRoot, out McpResolvedProjectSource source)
	{
		lock (_sync)
			return _remoteRoots.TryGetValue(projectRoot, out source!);
	}

	public IReadOnlyList<McpResolvedProjectSource> GetRemoteRootsSnapshot()
	{
		lock (_sync)
			return _remoteRoots.Values.ToArray();
	}

	public void Dispose()
	{
		McpResolvedProjectSource[] sources;
		lock (_sync)
		{
			if (_disposed)
				return;
			_disposed = true;
			sources = _remoteSources.Values.ToArray();
			_remoteRoots.Clear();
			_remoteSources.Clear();
		}

		try
		{
			foreach (var source in sources)
				source.Dispose();
		}
		finally
		{
			if (_remoteServices.IsValueCreated)
				_remoteServices.Value.Dispose();
		}
	}

	private async Task<McpResolvedProjectSource> AcquireRemoteAsync(
		string sourceUrl,
		string safeUrl,
		string? branch,
		RemoteProjectKey key,
		CancellationToken cancellationToken)
	{
		McpRemoteProjectServices? services = null;
		string? stagingPath = null;
		try
		{
			services = _remoteServices.Value;
			await using (await services.RepoCacheService
				.AcquireRepositoryOperationAsync(safeUrl, cancellationToken)
				.ConfigureAwait(false))
			{
				lock (_sync)
				{
					if (_remoteSources.TryGetValue(key, out var existing))
						return existing;
				}

				var cached = await TryAcquireSessionAsync(
					services.RepoCacheService,
					safeUrl,
					branch,
					cancellationToken).ConfigureAwait(false);
				if (cached is not null)
					return RegisterRemote(key, safeUrl, branch, cached);

				if (!await services.GitRepositoryService
					    .IsGitAvailableAsync(cancellationToken)
					    .ConfigureAwait(false))
				{
					throw RemoteFailed(safeUrl, "Git is unavailable");
				}

				stagingPath = services.RepoCacheService.CreateRepositoryStagingDirectory(safeUrl);
				var clone = await services.GitRepositoryService.CloneAsync(
						sourceUrl.Trim(),
						stagingPath,
						progress: null,
						cancellationToken)
					.ConfigureAwait(false);
				if (!clone.Success || !Directory.Exists(clone.LocalPath))
					throw RemoteFailed(safeUrl, "clone failed");

				var repositoryUrl = string.IsNullOrWhiteSpace(clone.RepositoryUrl)
					? safeUrl
					: RepositoryUrlUtility.ToSafeDisplay(clone.RepositoryUrl);
				if (repositoryUrl.Length == 0)
					repositoryUrl = safeUrl;
				var publishedPath = services.RepoCacheService.PublishRepositoryDirectory(
					stagingPath,
					repositoryUrl);
				stagingPath = null;
				services.RepoCacheService.RecordIndexedRepository(
					repositoryUrl,
					publishedPath,
					clone.DefaultBranch);

				var session = await TryAcquireSessionAsync(
					services.RepoCacheService,
					repositoryUrl,
					branch,
					cancellationToken).ConfigureAwait(false);
				if (session is null)
					throw RemoteFailed(safeUrl, "the cached checkout could not be pinned");

				return RegisterRemote(key, safeUrl, branch, session);
			}
		}
		catch (McpToolException)
		{
			throw;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception)
		{
			throw RemoteFailed(safeUrl, "repository preparation failed");
		}
		finally
		{
			if (stagingPath is not null && services is not null)
				services.RepoCacheService.DeleteRepositoryDirectory(stagingPath);
		}
	}

	private McpResolvedProjectSource RegisterRemote(
		RemoteProjectKey key,
		string safeUrl,
		string? requestedBranch,
		IRepositoryCacheSession session)
	{
		McpResolvedProjectSource source;
		try
		{
			var registry = new McpRootRegistry([session.RepositoryPath]);
			var root = registry.Roots[0];
			var identity = new ProjectSourceIdentity(
				RepositoryUrlUtility.GetRepositoryName(safeUrl),
				ProjectSourceType.GitClone,
				safeUrl,
				safeUrl,
				session.Branch ?? requestedBranch,
				IsCachedRepository: true);
			source = McpResolvedProjectSource.Remote(root, safeUrl, registry, identity, session);
		}
		catch
		{
			session.Dispose();
			throw;
		}

		lock (_sync)
		{
			if (_remoteSources.TryGetValue(key, out var existing))
			{
				source.Dispose();
				return existing;
			}
			_remoteSources.Add(key, source);
			_remoteRoots[source.Root] = source;
			return source;
		}
	}

	private static async Task<IRepositoryCacheSession?> TryAcquireSessionAsync(
		IRepoCacheService cache,
		string safeUrl,
		string? branch,
		CancellationToken cancellationToken)
	{
		try
		{
			return await cache.TryAcquireRepositorySessionAsync(safeUrl, branch, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (RepositoryBranchUnavailableException)
		{
			throw RemoteFailed(safeUrl, "the requested branch is unavailable");
		}
	}

	private static bool LooksLikeRepositoryUrl(string? source)
	{
		if (string.IsNullOrWhiteSpace(source))
			return false;
		if (source.Contains("://", StringComparison.Ordinal) ||
		    source.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (Directory.Exists(source))
			return false;
		var colon = source.IndexOf(':');
		return colon > 0 &&
		       (source[..colon].Contains('@') || source[..colon].Contains('.'));
	}

	private static McpToolException RemoteFailed(string safeUrl, string reason) =>
		new(
			McpErrorCodes.RemoteFailed,
			$"{McpErrorCodes.RemoteFailed}: remote repository '{DisplayRemote(safeUrl)}' could not be prepared ({reason}).");

	private static string DisplayRemote(string safeUrl) =>
		safeUrl.Length == 0 ? "<invalid remote source>" : safeUrl;

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

	private readonly record struct RemoteProjectKey(string Identity, string Branch);
}

internal sealed class McpResolvedProjectSource : IDisposable
{
	private IRepositoryCacheSession? _session;

	private McpResolvedProjectSource(
		string root,
		string address,
		McpRootRegistry registry,
		ProjectSourceIdentity? identity,
		IRepositoryCacheSession? session)
	{
		Root = root;
		Address = address;
		Registry = registry;
		Identity = identity;
		_session = session;
	}

	public string Root { get; }
	public string Address { get; }
	public McpRootRegistry Registry { get; }
	public ProjectSourceIdentity? Identity { get; }

	public static McpResolvedProjectSource Local(string root, McpRootRegistry registry) =>
		new(root, root, registry, identity: null, session: null);

	public static McpResolvedProjectSource Remote(
		string root,
		string address,
		McpRootRegistry registry,
		ProjectSourceIdentity identity,
		IRepositoryCacheSession session) =>
		new(root, address, registry, identity, session);

	public void Dispose() => Interlocked.Exchange(ref _session, null)?.Dispose();
}
