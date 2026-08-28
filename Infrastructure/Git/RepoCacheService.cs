using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Infrastructure.Git;

/// <summary>
/// Manages persistent repository bases, session checkouts and short-lived staging directories.
/// Cache roots must reside on a local file system because network file systems do not consistently
/// honor the exclusive file-handle leases used for cross-process ownership.
/// </summary>
public sealed class RepoCacheService : IRepoCacheService, IDisposable, IAsyncDisposable
{
	private const string AppFolderName = "DevProjex";
	private const string CacheFolderName = "RepoCache";
	private const string CacheIndexFileName = "cache-index.json";
	private const string LinkedCacheRootMessage =
		"Repository cache root must not be a symbolic link or junction.";
	private const int CacheIndexSchemaVersion = 2;
	private const int UniquePathSuffixLength = 29;
	private const int MaximumRepositoryNameUtf16Length = 100;
	private const int MaximumPortablePathComponentBytes = 255;
	private const int MaximumRepositoryNameUtf8Bytes =
		MaximumPortablePathComponentBytes - UniquePathSuffixLength - 1;
	private const UnixFileMode PrivateUnixDirectoryMode =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
	internal const long MaximumCacheIndexBytes = 64L * 1024 * 1024;
	private const byte RepositorySizeRefreshRunning = 1;
	private const byte RepositorySizeRefreshPending = 2;
	private static readonly TimeSpan IndexLockTimeout = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan WorktreeCleanupTimeout = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan RepositorySizeRefreshTimeout = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan BackgroundShutdownTimeout = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan FinalGarbageCollectionTimeout = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan MaximumPersistedClockSkew = TimeSpan.FromDays(1);
	private static readonly JsonSerializerOptions IndexSerializerOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
	};
	private static readonly SearchValues<char> InvalidFileNameChars =
		SearchValues.Create("<>:\"/\\|?*");
	private static readonly EnumerationOptions RecursiveCacheEnumeration = new()
	{
		RecurseSubdirectories = true,
		AttributesToSkip = FileAttributes.ReparsePoint,
		IgnoreInaccessible = true
	};

	private readonly RepositoryCachePolicy _policy;
	private readonly TimeProvider _timeProvider;
	private readonly IGitWorktreeManager _worktreeManager;
	private readonly RepoCacheTestHooks? _testHooks;
	private readonly BackgroundTaskOwner _backgroundTasks = new(BackgroundShutdownTimeout);
	private readonly object _disposalSync = new();
	private readonly object _garbageCollectionSync = new();
	private readonly ConcurrentDictionary<string, WorktreeCleanupState> _worktreeCleanupInFlight =
		new(PathComparer.Default);
	private readonly ConcurrentDictionary<string, byte> _repositorySizeRefreshInFlight = new(PathComparer.Default);
	private int _scheduledGarbageCollectionState;
	private Task? _disposalTask;

	public string CacheRootPath { get; }
	public IReadOnlyList<string> CacheSearchRootPaths { get; }

	public RepoCacheService()
		: this(
			UserDataPathResolver.GetCacheRoot,
			UserDataPathResolver.GetLegacyLocalDataRoot,
			RepositoryCachePolicy.Default,
			TimeProvider.System,
			new GitWorktreeManager(),
			testHooks: null)
	{
	}

	internal RepoCacheService(Func<string> cacheRootProvider)
		: this(cacheRootProvider, legacyDataRootProvider: null)
	{
	}

	internal RepoCacheService(
		Func<string> cacheRootProvider,
		Func<string>? legacyDataRootProvider)
		: this(
			cacheRootProvider,
			legacyDataRootProvider,
			RepositoryCachePolicy.Default,
			TimeProvider.System,
			new GitWorktreeManager(),
			testHooks: null)
	{
	}

	private RepoCacheService(
		Func<string> cacheRootProvider,
		Func<string>? legacyDataRootProvider,
		RepositoryCachePolicy policy,
		TimeProvider timeProvider,
		IGitWorktreeManager worktreeManager,
		RepoCacheTestHooks? testHooks)
	{
		ArgumentNullException.ThrowIfNull(cacheRootProvider);
		CacheRootPath = Path.Combine(cacheRootProvider(), AppFolderName, CacheFolderName);
		CacheSearchRootPaths = BuildCacheSearchRoots(CacheRootPath, legacyDataRootProvider);
		_policy = policy.Validate();
		_timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
		_worktreeManager = worktreeManager ?? throw new ArgumentNullException(nameof(worktreeManager));
		_testHooks = testHooks;
	}

	/// <summary>
	/// Constructor for testing with a custom cache path.
	/// </summary>
	public RepoCacheService(string customCachePath)
		: this(
			customCachePath,
			RepositoryCachePolicy.Default,
			TimeProvider.System,
			new GitWorktreeManager(),
			testHooks: null)
	{
	}

	internal RepoCacheService(
		string customCachePath,
		RepositoryCachePolicy policy,
		TimeProvider timeProvider,
		IGitWorktreeManager worktreeManager,
		RepoCacheTestHooks? testHooks = null)
	{
		CacheRootPath = customCachePath ?? throw new ArgumentNullException(nameof(customCachePath));
		CacheSearchRootPaths = Array.AsReadOnly([CacheRootPath]);
		_policy = policy.Validate();
		_timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
		_worktreeManager = worktreeManager ?? throw new ArgumentNullException(nameof(worktreeManager));
		_testHooks = testHooks;
	}

	public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

	public ValueTask DisposeAsync()
	{
		lock (_disposalSync)
		{
			_disposalTask ??= DisposeCoreAsync();
			return new ValueTask(_disposalTask);
		}
	}

	private async Task DisposeCoreAsync()
	{
		var collectGarbageOnShutdown = Volatile.Read(ref _scheduledGarbageCollectionState) != 0;
		await _backgroundTasks.DisposeAsync().ConfigureAwait(false);
		if (!collectGarbageOnShutdown)
			return;

		using var timeout = new CancellationTokenSource(FinalGarbageCollectionTimeout);
		try
		{
			await Task.Run(
				() => CollectGarbage(timeout.Token),
				CancellationToken.None).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (timeout.IsCancellationRequested)
		{
			Trace.TraceWarning("Repository cache final garbage collection timed out.");
		}
		catch (Exception exception)
		{
			Trace.TraceWarning("Repository cache final garbage collection failed: {0}", exception.Message);
		}
	}

	public string CreateRepositoryDirectory(string repositoryUrl)
	{
		EnsurePrivateCacheDirectory(CacheRootPath);
		var path = CreateUniqueRepositoryPath(CacheRootPath, repositoryUrl);
		Directory.CreateDirectory(path);
		return path;
	}

	public string CreateRepositoryStagingDirectory(string repositoryUrl)
	{
		EnsurePrivateCacheDirectory(CacheRootPath);
		var stagingRoot = Path.Combine(CacheRootPath, RepositoryCacheLayout.StagingDirectoryName);
		var path = CreateUniqueRepositoryPath(stagingRoot, repositoryUrl);
		Directory.CreateDirectory(path);
		return path;
	}

	public string PublishRepositoryDirectory(string stagingPath, string repositoryUrl)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(stagingPath);
		var stagingRoot = Path.Combine(CacheRootPath, RepositoryCacheLayout.StagingDirectoryName);
		var normalizedStagingPath = PathUtility.Normalize(stagingPath);
		if (!PathUtility.IsPathInside(normalizedStagingPath, stagingRoot) ||
		    !Directory.Exists(normalizedStagingPath))
		{
			throw new InvalidOperationException("Repository staging path is invalid.");
		}

		EnsurePrivateCacheDirectory(CacheRootPath);
		var container = CreateUniqueRepositoryPath(CacheRootPath, repositoryUrl);
		Directory.CreateDirectory(container);
		var contentKind = Directory.Exists(Path.Combine(normalizedStagingPath, ".git"))
			? RepositoryCacheContentKind.Git
			: RepositoryCacheContentKind.Zip;
		var contentName = contentKind == RepositoryCacheContentKind.Git
			? RepositoryCacheLayout.BaseDirectoryName
			: RepositoryCacheLayout.SnapshotDirectoryName;
		var destination = Path.Combine(container, contentName);
		var publicationLockPath = Path.Combine(
			container,
			RepositoryCacheLayout.LeasesDirectoryName,
			"base-operation.lock");
		if (!RepositoryFileLease.TryAcquireExclusive(publicationLockPath, out var publicationLease))
		{
			TryDeleteTree(container);
			throw new IOException("Repository cache publication could not be locked.");
		}

		try
		{
			using (publicationLease)
			{
				File.WriteAllText(
					Path.Combine(container, RepositoryCacheLayout.MarkerFileName),
					contentKind == RepositoryCacheContentKind.Git ? "git" : "zip");
				Directory.Move(normalizedStagingPath, destination);
				RecordIndexedRepositoryCore(
					repositoryUrl,
					destination,
					branch: null,
					commitHash: null,
					RepositoryCacheEntryState.Ready,
					CalculateDirectorySize(container),
					contentKind);
			}
			return destination;
		}
		catch
		{
			TryDeleteTree(container);
			throw;
		}
	}

	public RepositoryCacheIndexEntry? FindIndexedRepository(string repositoryUrl)
	{
		var identity = RepositoryUrlUtility.GetComparisonKey(repositoryUrl);
		if (identity.Length == 0)
			return null;

		RepositoryCacheIndexEntry? matchingEntry = null;
		foreach (var searchRoot in CacheSearchRootPaths)
		{
			if (IsLinkedCacheRoot(searchRoot))
				continue;
			var fileSet = GetIndexFileSet(searchRoot);
			if (!PathComparer.Default.Equals(searchRoot, CacheRootPath) &&
			    !File.Exists(fileSet.PrimaryPath) &&
			    !File.Exists(fileSet.BackupPath))
			{
				continue;
			}

			if (!CrossProcessFileLock.TryAcquire(fileSet, IndexLockTimeout, out var heldLock))
				continue;

			using (heldLock)
			{
				var candidate = FindByIdentity(LoadIndex(fileSet), identity);
				if (candidate is not null &&
				    (matchingEntry is null || candidate.LastOpenedUtc > matchingEntry.LastOpenedUtc))
				{
					matchingEntry = candidate;
				}
			}
		}

		if (matchingEntry is not null &&
		    !PathUtility.IsPathInside(matchingEntry.LocalPath, CacheRootPath))
		{
			RecordIndexedRepositoryCore(
				matchingEntry.RepositoryUrl,
				matchingEntry.LocalPath,
				matchingEntry.Branch,
				matchingEntry.CommitHash,
				matchingEntry.State,
				matchingEntry.ApproximateSizeBytes,
				matchingEntry.ContentKind);
		}

		return matchingEntry;
	}

	public IReadOnlyList<RepositoryCacheCatalogEntry> ListIndexedRepositories()
	{
		var latestByIdentity = new Dictionary<string, RepositoryCacheIndexEntry>(
			StringComparer.Ordinal);
		foreach (var searchRoot in CacheSearchRootPaths)
		{
			if (IsLinkedCacheRoot(searchRoot))
				continue;
			var fileSet = GetIndexFileSet(searchRoot);
			if (!File.Exists(fileSet.PrimaryPath) && !File.Exists(fileSet.BackupPath))
				continue;
			if (!CrossProcessFileLock.TryAcquire(fileSet, IndexLockTimeout, out var heldLock))
				continue;

			using (heldLock)
			{
				if (HasUnsupportedIndexDocument(fileSet))
					continue;

				var document = LoadIndex(fileSet);
				List<RepositoryCacheIndexEntry>? retained = null;
				for (var index = 0; index < document.Entries.Count; index++)
				{
					var entry = document.Entries[index];
					if (!Directory.Exists(entry.LocalPath))
					{
						if (retained is null)
						{
							retained = new List<RepositoryCacheIndexEntry>(document.Entries.Count - 1);
							for (var retainedIndex = 0; retainedIndex < index; retainedIndex++)
								retained.Add(document.Entries[retainedIndex]);
						}
						continue;
					}
					retained?.Add(entry);
					if (entry.State != RepositoryCacheEntryState.Ready)
						continue;
					if (!latestByIdentity.TryGetValue(entry.Identity, out var previous) ||
					    entry.LastOpenedUtc > previous.LastOpenedUtc)
					{
						latestByIdentity[entry.Identity] = entry;
					}
				}

				if (retained is not null)
					WriteIndex(fileSet, retained);
			}
		}

		var catalog = new List<RepositoryCacheCatalogEntry>(latestByIdentity.Count);
		foreach (var entry in latestByIdentity.Values)
		{
			catalog.Add(new RepositoryCacheCatalogEntry(
				RepositoryUrlUtility.ToSafeDisplay(entry.RepositoryUrl),
				RepositoryUrlUtility.GetRepositoryName(entry.RepositoryUrl),
				entry.Branch,
				entry.LastOpenedUtc,
				Math.Max(0, entry.ApproximateSizeBytes),
				ResolveContentKind(entry),
				entry.LocalPath,
				entry.CommitHash,
				entry.State));
		}

		catalog.Sort(static (left, right) =>
		{
			var byLastOpened = right.LastOpenedUtc.CompareTo(left.LastOpenedUtc);
			return byLastOpened != 0
				? byLastOpened
				: StringComparer.OrdinalIgnoreCase.Compare(left.RepositoryUrl, right.RepositoryUrl);
		});
		return catalog.ToArray();
	}

	public RepositoryCacheManagementListResult ListCacheEntriesForManagement()
	{
		var catalog = new List<RepositoryCacheCatalogEntry>();
		var unavailableRootCount = 0;
		foreach (var searchRoot in CacheSearchRootPaths)
		{
			if (IsLinkedCacheRoot(searchRoot))
			{
				unavailableRootCount++;
				continue;
			}
			var fileSet = GetIndexFileSet(searchRoot);
			if (!File.Exists(fileSet.PrimaryPath) && !File.Exists(fileSet.BackupPath))
				continue;
			if (!CrossProcessFileLock.TryAcquire(fileSet, IndexLockTimeout, out var heldLock))
			{
				unavailableRootCount++;
				continue;
			}

			using (heldLock)
			{
				if (HasUnsupportedIndexDocument(fileSet))
				{
					unavailableRootCount++;
					continue;
				}

				var document = LoadIndex(fileSet);
				List<RepositoryCacheIndexEntry>? retained = null;
				for (var index = 0; index < document.Entries.Count; index++)
				{
					var entry = document.Entries[index];
					if (!Directory.Exists(entry.LocalPath))
					{
						if (retained is null)
						{
							retained = new List<RepositoryCacheIndexEntry>(document.Entries.Count - 1);
							for (var retainedIndex = 0; retainedIndex < index; retainedIndex++)
								retained.Add(document.Entries[retainedIndex]);
						}
						continue;
					}

					retained?.Add(entry);
					catalog.Add(ToCatalogEntry(entry));
				}

				if (retained is not null)
					WriteIndex(fileSet, retained);
			}
		}

		catalog.Sort(static (left, right) =>
		{
			var byLastOpened = right.LastOpenedUtc.CompareTo(left.LastOpenedUtc);
			if (byLastOpened != 0)
				return byLastOpened;
			var byUrl = StringComparer.OrdinalIgnoreCase.Compare(left.RepositoryUrl, right.RepositoryUrl);
			return byUrl != 0
				? byUrl
				: PathComparer.Default.Compare(left.LocalPath, right.LocalPath);
		});
		return new RepositoryCacheManagementListResult(
			catalog.ToArray(),
			unavailableRootCount);
	}

	private static RepositoryCacheCatalogEntry ToCatalogEntry(RepositoryCacheIndexEntry entry) =>
		new(
			RepositoryUrlUtility.ToSafeDisplay(entry.RepositoryUrl),
			RepositoryUrlUtility.GetRepositoryName(entry.RepositoryUrl),
			entry.Branch,
			entry.LastOpenedUtc,
			Math.Max(0, entry.ApproximateSizeBytes),
			ResolveContentKind(entry),
			entry.LocalPath,
			entry.CommitHash,
			entry.State);

	public async Task<IRepositoryCacheSession?> TryAcquireRepositorySessionAsync(
		string repositoryUrl,
		string? branch = null,
		CancellationToken cancellationToken = default)
	{
		var identity = RepositoryUrlUtility.GetComparisonKey(repositoryUrl);
		if (identity.Length == 0)
			return null;

		var session = await TryAcquireSessionCoreAsync(
			identity,
			requestedPath: null,
			branch,
			cancellationToken).ConfigureAwait(false);
		if (session is not null)
			RequestGarbageCollection();
		return session;
	}

	public async Task<IRepositoryCacheSession?> TryAcquireRepositorySessionByPathAsync(
		string repositoryPath,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(repositoryPath) || !IsInCache(repositoryPath))
			return null;

		string normalizedPath;
		try
		{
			normalizedPath = PathUtility.Normalize(repositoryPath);
		}
		catch
		{
			return null;
		}

		var entry = FindIndexedRepositoryByPathAcrossRoots(normalizedPath);
		if (entry is null)
			return null;
		if (!PathUtility.IsPathInside(entry.LocalPath, CacheRootPath))
		{
			RecordIndexedRepositoryCore(
				entry.RepositoryUrl,
				entry.LocalPath,
				entry.Branch,
				entry.CommitHash,
				entry.State,
				entry.ApproximateSizeBytes,
				entry.ContentKind);
		}

		var session = await TryAcquireSessionCoreAsync(
			entry.Identity,
			normalizedPath,
			entry.Branch,
			cancellationToken).ConfigureAwait(false);
		if (session is not null)
			RequestGarbageCollection();
		return session;
	}

	public async Task<IAsyncDisposable> AcquireRepositoryOperationAsync(
		string repositoryUrl,
		CancellationToken cancellationToken = default)
	{
		var identity = RepositoryUrlUtility.GetComparisonKey(repositoryUrl);
		if (identity.Length == 0)
			throw new ArgumentException("Repository URL is invalid.", nameof(repositoryUrl));

		return await RepositoryFileLease.AcquireExclusiveAsync(
			RepositoryCacheLayout.GetRepositoryOperationLockPath(CacheRootPath, identity),
			cancellationToken).ConfigureAwait(false);
	}

	public void RecordIndexedRepository(
		string repositoryUrl,
		string localPath,
		string? branch = null,
		string? commitHash = null,
		RepositoryCacheEntryState state = RepositoryCacheEntryState.Ready)
	{
		RecordIndexedRepositoryCore(
			repositoryUrl,
			localPath,
			branch,
			commitHash,
			state,
			approximateSizeBytes: null,
			RepositoryCacheContentKind.Unknown);
	}

	public void DeleteRepositoryDirectory(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || !IsInCache(path))
			return;

		string normalizedPath;
		try
		{
			normalizedPath = PathUtility.Normalize(path);
		}
		catch
		{
			return;
		}

		var owningCacheRoot = GetOwningCacheRoot(normalizedPath);
		if (PathUtility.IsPathInside(
			normalizedPath,
			Path.Combine(owningCacheRoot, RepositoryCacheLayout.StagingDirectoryName)))
		{
			MoveToTrashAndClean(normalizedPath);
			return;
		}

		var fileSet = GetIndexFileSet(owningCacheRoot);
		if (!CrossProcessFileLock.TryAcquire(fileSet, IndexLockTimeout, out var heldLock))
			return;

		string? pathToTrash = null;
		using (heldLock)
		{
			if (HasUnsupportedIndexDocument(fileSet))
				return;

			var document = LoadIndex(fileSet);
			var entry = FindByPath(document, normalizedPath);
			if (entry is null)
			{
				if (!Directory.Exists(normalizedPath) ||
				    !TryAcquireAllRepositoryLeases(normalizedPath, out var unindexedLeases))
				{
					return;
				}

				using (unindexedLeases)
					pathToTrash = RepositoryCacheLayout.GetContainer(normalizedPath);
			}
			else
			{
				var container = RepositoryCacheLayout.GetContainer(entry.LocalPath);
				if (!TryAcquireAllRepositoryLeases(entry.LocalPath, out var verificationLeases))
					return;

				using (verificationLeases)
				{
					var entries = document.Entries
						.Where(candidate => !string.Equals(
							candidate.Identity,
							entry.Identity,
							StringComparison.Ordinal))
						.ToList();
					if (!WriteIndex(fileSet, entries))
						return;
					pathToTrash = container;
				}
			}
		}

		if (pathToTrash is not null)
			MoveToTrashAndClean(pathToTrash);
	}

	public void ClearAllCache()
	{
		foreach (var cacheRoot in CacheSearchRootPaths)
			ClearCacheRoot(cacheRoot);
	}

	public CacheClearResult ClearAllCacheWithResult()
	{
		var result = new CacheClearResult(0, 0, 0);
		foreach (var cacheRoot in CacheSearchRootPaths)
			result += RemoveCacheEntriesWithResult(cacheRoot, identity: null);
		return result;
	}

	public CacheClearResult RemoveCachedRepositoryWithResult(string repositoryUrl)
	{
		string identity;
		try
		{
			identity = RepositoryUrlUtility.GetComparisonKey(repositoryUrl);
		}
		catch
		{
			return new CacheClearResult(0, 0, 1);
		}
		if (identity.Length == 0)
			return new CacheClearResult(0, 0, 1);

		var result = new CacheClearResult(0, 0, 0);
		foreach (var cacheRoot in CacheSearchRootPaths)
			result += RemoveCacheEntriesWithResult(cacheRoot, identity);
		return result;
	}

	private CacheClearResult RemoveCacheEntriesWithResult(string cacheRoot, string? identity)
	{
		if (IsLinkedCacheRoot(cacheRoot))
			return new CacheClearResult(0, 0, 1);
		var fileSet = GetIndexFileSet(cacheRoot);
		if (!Directory.Exists(cacheRoot) &&
		    !File.Exists(fileSet.PrimaryPath) &&
		    !File.Exists(fileSet.BackupPath))
		{
			return new CacheClearResult(0, 0, 0);
		}
		if (!CrossProcessFileLock.TryAcquire(fileSet, IndexLockTimeout, out var heldLock))
			return CacheRootFailure(cacheRoot);

		var trashPaths = new List<string>();
		CacheClearResult result;
		using (heldLock)
		{
			if (HasUnsupportedIndexDocument(fileSet))
				return CacheRootFailure(cacheRoot);

			var document = LoadIndex(fileSet);
			var indexedEntries = new List<CacheRemovalEntry>(document.Entries.Count);
			var indexedContainers = new HashSet<string>(PathComparer.Default);
			var containersWithNonTargets = new HashSet<string>(PathComparer.Default);
			foreach (var entry in document.Entries)
			{
				var isTarget = identity is null || string.Equals(
					entry.Identity,
					identity,
					StringComparison.Ordinal);
				var container = TryResolveCacheContainer(entry.LocalPath);
				indexedEntries.Add(new CacheRemovalEntry(entry, container, isTarget));
				if (container is null)
					continue;
				indexedContainers.Add(container);
				if (!isTarget)
					containersWithNonTargets.Add(container);
			}

			var unindexedContainers = identity is null
				? EnumerateRepositoryRootDirectories(cacheRoot)
					.Where(directory => !indexedContainers.Contains(directory))
					.ToArray()
				: [];
			var retainedEntries = new List<RepositoryCacheIndexEntry>(document.Entries.Count);
			var containerOutcomes = new Dictionary<string, CacheRemovalOutcome>(PathComparer.Default);
			var indexedRemoved = 0;
			var unindexedRemoved = 0;
			var retained = 0;
			var failed = 0;
			foreach (var candidate in indexedEntries)
			{
				if (!candidate.IsTarget)
				{
					retainedEntries.Add(candidate.Entry);
					continue;
				}

				var outcome = ResolveRemovalOutcome(
					candidate,
					containersWithNonTargets,
					containerOutcomes,
					trashPaths);
				switch (outcome)
				{
					case CacheRemovalOutcome.Removed:
						indexedRemoved++;
						break;
					case CacheRemovalOutcome.Retained:
						retained++;
						retainedEntries.Add(candidate.Entry);
						break;
					case CacheRemovalOutcome.Failed:
						failed++;
						retainedEntries.Add(candidate.Entry);
						break;
					default:
						throw new ArgumentOutOfRangeException();
				}
			}

			foreach (var directory in unindexedContainers)
			{
				var outcome = ResolveUnindexedRemovalOutcome(directory, trashPaths);
				switch (outcome)
				{
					case CacheRemovalOutcome.Removed:
						unindexedRemoved++;
						break;
					case CacheRemovalOutcome.Retained:
						retained++;
						break;
					case CacheRemovalOutcome.Failed:
						failed++;
						break;
					default:
						throw new ArgumentOutOfRangeException();
				}
			}

			if (indexedRemoved > 0 && !WriteIndex(fileSet, retainedEntries))
			{
				failed = checked(failed + indexedRemoved);
				indexedRemoved = 0;
			}
			result = new CacheClearResult(
				checked(indexedRemoved + unindexedRemoved),
				retained,
				failed);
		}

		CleanupMovedCacheContainers(trashPaths);
		return result;
	}

	private CacheRemovalOutcome ResolveRemovalOutcome(
		CacheRemovalEntry candidate,
		IReadOnlySet<string> containersWithNonTargets,
		IDictionary<string, CacheRemovalOutcome> containerOutcomes,
		ICollection<string> trashPaths)
	{
		if (candidate.Container is null || containersWithNonTargets.Contains(candidate.Container))
			return CacheRemovalOutcome.Failed;
		if (containerOutcomes.TryGetValue(candidate.Container, out var existing))
			return existing;

		var outcome = ResolveUnindexedRemovalOutcome(candidate.Container, trashPaths);
		containerOutcomes[candidate.Container] = outcome;
		return outcome;
	}

	private CacheRemovalOutcome ResolveUnindexedRemovalOutcome(
		string container,
		ICollection<string> trashPaths)
	{
		if (!Directory.Exists(container))
			return CacheRemovalOutcome.Removed;
		if (!TryAcquireAllRepositoryLeases(container, out var leases))
			return CacheRemovalOutcome.Retained;

		leases!.Dispose();
		if (!TryMoveCacheContainerToTrash(container, out var trashPath))
			return CacheRemovalOutcome.Failed;
		if (trashPath is not null)
			trashPaths.Add(trashPath);
		return CacheRemovalOutcome.Removed;
	}

	private bool TryMoveCacheContainerToTrash(string path, out string? trashPath)
	{
		trashPath = null;
		if (!Directory.Exists(path))
			return true;
		if (!IsInCache(path))
			return false;

		try
		{
			var trashRoot = RepositoryCacheLayout.GetTrashRoot(GetOwningCacheRoot(path));
			Directory.CreateDirectory(trashRoot);
			var destination = Path.Combine(trashRoot, $"trash-{Guid.NewGuid():N}");
			Directory.Move(path, destination);
			trashPath = destination;
			return true;
		}
		catch (Exception exception) when (exception is
		       IOException or
		       UnauthorizedAccessException or
		       System.Security.SecurityException or
		       ArgumentException or
		       NotSupportedException)
		{
			return false;
		}
	}

	private static void CleanupMovedCacheContainers(IEnumerable<string> trashPaths)
	{
		foreach (var path in trashPaths)
		{
			TryDeleteTree(path);
			var parent = Path.GetDirectoryName(path);
			if (parent is not null)
				TryDeleteEmptyDirectory(parent);
		}
	}

	private static string? TryResolveCacheContainer(string path)
	{
		try
		{
			return RepositoryCacheLayout.GetContainer(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			return null;
		}
	}

	private static CacheClearResult CacheRootFailure(string cacheRoot)
	{
		var failed = 0;
		try
		{
			failed = EnumerateRepositoryRootDirectories(cacheRoot).Count();
		}
		catch
		{
		}
		return new CacheClearResult(0, 0, Math.Max(1, failed));
	}

	private sealed record CacheRemovalEntry(
		RepositoryCacheIndexEntry Entry,
		string? Container,
		bool IsTarget);

	private enum CacheRemovalOutcome
	{
		Removed,
		Retained,
		Failed
	}

	private void ClearCacheRoot(string cacheRoot)
	{
		if (!Directory.Exists(cacheRoot) || IsLinkedCacheRoot(cacheRoot))
			return;

		var fileSet = GetIndexFileSet(cacheRoot);
		if (!CrossProcessFileLock.TryAcquire(fileSet, IndexLockTimeout, out var heldLock))
			return;

		var trashPaths = new List<string>();
		using (heldLock)
		{
			if (HasUnsupportedIndexDocument(fileSet))
				return;

			var document = LoadIndex(fileSet);
			var retained = new List<RepositoryCacheIndexEntry>();
			foreach (var entry in document.Entries)
			{
				if (!TryAcquireAllRepositoryLeases(entry.LocalPath, out var leases))
				{
					retained.Add(entry);
					continue;
				}

				using (leases)
					trashPaths.Add(RepositoryCacheLayout.GetContainer(entry.LocalPath));
			}

			var indexedContainers = document.Entries
				.Select(entry => RepositoryCacheLayout.GetContainer(entry.LocalPath))
				.ToHashSet(PathComparer.Default);
			foreach (var directory in EnumerateRepositoryRootDirectories(cacheRoot))
			{
				if (indexedContainers.Contains(directory))
					continue;
				if (!TryAcquireAllRepositoryLeases(directory, out var lease))
				{
					continue;
				}
				using (lease)
					trashPaths.Add(directory);
			}

			if (!WriteIndex(fileSet, retained))
				return;
		}

		foreach (var trashPath in trashPaths)
			MoveToTrashAndClean(trashPath);
		CleanupUnindexedRepositories(cacheRoot);
		CleanupTrash(cacheRoot);
	}

	public void CleanupStaleCacheOnStartup()
	{
		CleanupStaging();
		CollectGarbage();
	}

	public void CollectGarbage()
		=> CollectGarbage(CancellationToken.None);

	private void CollectGarbage(CancellationToken cancellationToken)
	{
		// A synchronous caller must observe completed cleanup even when session warm-up
		// has already queued a background collection for the same service instance.
		var lockTaken = false;
		try
		{
			while (!Monitor.TryEnter(_garbageCollectionSync, millisecondsTimeout: 100))
				cancellationToken.ThrowIfCancellationRequested();
			lockTaken = true;
			CollectGarbageCore(cancellationToken);
		}
		finally
		{
			if (lockTaken)
				Monitor.Exit(_garbageCollectionSync);
		}
	}

	public void RequestGarbageCollection()
	{
		if (!_backgroundTasks.IsAccepting)
			return;

		while (true)
		{
			var state = Volatile.Read(ref _scheduledGarbageCollectionState);
			if (state == 2)
				return;
			if (state == 1)
			{
				if (Interlocked.CompareExchange(ref _scheduledGarbageCollectionState, 2, 1) == 1)
					return;
				continue;
			}
			if (Interlocked.CompareExchange(ref _scheduledGarbageCollectionState, 1, 0) == 0)
			{
				if (!_backgroundTasks.TryRun(RunScheduledGarbageCollectionAsync))
					Interlocked.Exchange(ref _scheduledGarbageCollectionState, 0);
				return;
			}
		}
	}

	private Task RunScheduledGarbageCollectionAsync(CancellationToken cancellationToken)
	{
		try
		{
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				_testHooks?.BeforeScheduledGarbageCollection?.Invoke();
				CollectGarbage(cancellationToken);
				_testHooks?.AfterScheduledGarbageCollection?.Invoke();

				var state = Interlocked.CompareExchange(ref _scheduledGarbageCollectionState, 0, 1);
				if (state == 1)
					return Task.CompletedTask;
				if (state == 2 &&
				    Interlocked.CompareExchange(ref _scheduledGarbageCollectionState, 1, 2) == 2)
				{
					continue;
				}
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			Interlocked.Exchange(ref _scheduledGarbageCollectionState, 0);
		}
		catch (Exception exception)
		{
			var state = Interlocked.Exchange(ref _scheduledGarbageCollectionState, 0);
			Trace.TraceWarning("Repository cache garbage collection failed: {0}", exception.Message);
			if (state == 2)
				RequestGarbageCollection();
		}
		return Task.CompletedTask;
	}

	private void CollectGarbageCore(CancellationToken cancellationToken)
	{
		CleanupTrash(cancellationToken);
		CleanupStaging(cancellationToken);
		cancellationToken.ThrowIfCancellationRequested();
		if (!Directory.Exists(CacheRootPath) || IsLinkedCacheRoot(CacheRootPath))
			return;

		var fileSet = GetIndexFileSet();
		if (!CrossProcessFileLock.TryAcquireWithCancellation(
			    fileSet,
			    IndexLockTimeout,
			    cancellationToken,
			    out var heldLock))
			return;

		var trashPaths = new List<string>();
		using (heldLock)
		{
			if (HasUnsupportedIndexDocument(fileSet))
				return;

			var document = LoadIndex(fileSet);
			var entries = document.Entries.ToList();
			var totalSize = CalculateIndexedSize(entries);
			var expiration = _timeProvider.GetUtcNow() - _policy.MaximumUnusedAge;

			foreach (var entry in entries.OrderBy(static entry => entry.LastOpenedUtc).ToArray())
			{
				cancellationToken.ThrowIfCancellationRequested();
				var expired = entry.LastOpenedUtc < expiration;
				if (!expired && totalSize <= _policy.MaximumSizeBytes)
					continue;
				if (!TryAcquireAllRepositoryLeases(entry.LocalPath, out var leases))
					continue;

				using (leases)
				{
					entries.Remove(entry);
					totalSize = totalSize == long.MaxValue
						? CalculateIndexedSize(entries)
						: totalSize - Math.Max(0, entry.ApproximateSizeBytes);
					trashPaths.Add(RepositoryCacheLayout.GetContainer(entry.LocalPath));
				}
			}

			if (entries.Count != document.Entries.Count && !WriteIndex(fileSet, entries))
				return;
		}

		foreach (var trashPath in trashPaths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			MoveToTrashAndClean(trashPath, cancellationToken);
		}
		CleanupUnindexedRepositories(cancellationToken);
		CleanupTrash(cancellationToken);
	}

	public void RefreshIndexedRepositorySize(string localPath)
	{
		RefreshIndexedRepositorySize(localPath, CancellationToken.None);
	}

	private void RefreshIndexedRepositorySize(string localPath, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(localPath))
			return;
		cancellationToken.ThrowIfCancellationRequested();
		var size = CalculateDirectorySize(
			RepositoryCacheLayout.GetContainer(localPath),
			cancellationToken);
		_testHooks?.AfterRepositorySizeCalculated?.Invoke(localPath);

		var fileSet = GetIndexFileSet();
		if (!CrossProcessFileLock.TryAcquireWithCancellation(
			    fileSet,
			    IndexLockTimeout,
			    cancellationToken,
			    out var heldLock))
			return;

		using (heldLock)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (HasUnsupportedIndexDocument(fileSet))
				return;

			var document = LoadIndex(fileSet);
			var entry = FindByPath(document, localPath);
			if (entry is null)
				return;

			WriteIndex(
				fileSet,
				document.Entries
					.Select(candidate => string.Equals(
						candidate.Identity,
						entry.Identity,
						StringComparison.Ordinal)
						? candidate with { ApproximateSizeBytes = size }
						: candidate)
					.ToList());
		}
	}

	public bool IsInCache(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return false;

		try
		{
			return CacheSearchRootPaths.Any(root =>
				!IsLinkedCacheRoot(root) && PathUtility.IsPathInside(path, root));
		}
		catch
		{
			return false;
		}
	}

	public void RemoveIndexedRepository(string localPath)
	{
		string normalizedPath;
		try
		{
			normalizedPath = PathUtility.Normalize(localPath);
		}
		catch
		{
			return;
		}

		var fileSet = GetIndexFileSet();
		if (!CrossProcessFileLock.TryAcquire(fileSet, IndexLockTimeout, out var heldLock))
			return;

		using (heldLock)
		{
			if (HasUnsupportedIndexDocument(fileSet))
				return;

			var document = LoadIndex(fileSet);
			var entries = document.Entries
				.Where(entry => !ArePathsInSameRepository(entry.LocalPath, normalizedPath))
				.ToList();
			if (entries.Count != document.Entries.Count)
				WriteIndex(fileSet, entries);
		}
	}

	private async Task<IRepositoryCacheSession?> TryAcquireSessionCoreAsync(
		string identity,
		string? requestedPath,
		string? requestedBranch,
		CancellationToken cancellationToken)
	{
		var initial = FindIndexedRepositoryByIdentity(identity);
		if (initial is null || !Directory.Exists(initial.LocalPath))
			return null;

		var kind = ResolveContentKind(initial);
		if (kind == RepositoryCacheContentKind.Git &&
		    !RepositoryCacheLayout.IsManaged(initial.LocalPath))
		{
			initial = TryMigrateLegacyGitRepository(identity) ?? initial;
			kind = ResolveContentKind(initial);
		}
		var worktreesSupported = false;
		if (kind == RepositoryCacheContentKind.Git &&
		    RepositoryCacheLayout.IsManaged(initial.LocalPath))
		{
			var supportState = await _worktreeManager
				.GetSupportStateAsync(initial.LocalPath, cancellationToken)
				.ConfigureAwait(false);
			worktreesSupported = supportState != WorktreeSupportState.PermanentUnsupported;
		}

		var fileSet = GetIndexFileSet();
		if (!CrossProcessFileLock.TryAcquire(fileSet, IndexLockTimeout, out var heldLock))
			return null;

		RepositoryCacheIndexEntry? entry;
		RepositoryFileLease? sessionLease = null;
		string? selectedPath = null;
		string? effectiveBranch = null;
		var needsWorktreeCreation = false;
		using (heldLock)
		{
			var document = LoadIndex(fileSet);
			entry = FindByIdentity(document, identity);
			if (entry is null || !Directory.Exists(entry.LocalPath))
				return null;

			kind = ResolveContentKind(entry);
			if (kind == RepositoryCacheContentKind.Zip)
			{
				selectedPath = entry.LocalPath;
				sessionLease = AcquireUniqueSnapshotLease(entry.LocalPath, cancellationToken);
			}
			else if (!worktreesSupported)
			{
				selectedPath = entry.LocalPath;
				RepositoryFileLease.TryAcquireShared(
					RepositoryCacheLayout.GetLeasePath(CacheRootPath, selectedPath),
					out sessionLease);
			}
			else
			{
				foreach (var candidate in OrderCandidateCopies(entry.LocalPath, requestedPath))
				{
					if (!RepositoryFileLease.TryAcquireExclusive(
						    RepositoryCacheLayout.GetLeasePath(CacheRootPath, candidate),
						    out sessionLease))
					{
						continue;
					}

					selectedPath = candidate;
					break;
				}

				if (selectedPath is null)
				{
					selectedPath = RepositoryCacheLayout.CreateShortWorktreePath(entry.LocalPath);
					if (!RepositoryFileLease.TryAcquireExclusive(
						    RepositoryCacheLayout.GetLeasePath(CacheRootPath, selectedPath),
						    out sessionLease))
					{
						return null;
					}
					needsWorktreeCreation = true;
				}
			}

			if (sessionLease is null || selectedPath is null)
				return null;

			_testHooks?.AfterSessionLeaseAcquired?.Invoke(selectedPath);

			// Re-read while the same index lock is held. This makes the pin-before-delete ordering
			// explicit and keeps the protocol safe if the implementation later gains test hooks.
			var verified = FindByIdentity(LoadIndex(fileSet), identity);
			if (verified is null || !PathComparer.Default.Equals(verified.LocalPath, entry.LocalPath))
			{
				sessionLease.Dispose();
				return null;
			}

			entry = verified;
			effectiveBranch = kind == RepositoryCacheContentKind.Git &&
			                  !string.IsNullOrWhiteSpace(requestedBranch)
				? GitBranchNameValidator.ValidateAndNormalize(requestedBranch.Trim())
				: verified.Branch;
		}

		try
		{
			if (kind == RepositoryCacheContentKind.Git && worktreesSupported)
			{
				await using var baseLock = await RepositoryFileLease.AcquireExclusiveAsync(
					RepositoryCacheLayout.GetBaseOperationLockPath(CacheRootPath, entry.LocalPath),
					cancellationToken).ConfigureAwait(false);
				bool prepared;
				try
				{
					prepared = await PrepareWorktreeAsync(
						entry.LocalPath,
						selectedPath,
						effectiveBranch,
						needsWorktreeCreation,
						cancellationToken).ConfigureAwait(false);
				}
				catch (RepositoryBranchUnavailableException exception) when (
					exception.Reason == RepositoryBranchUnavailableReason.NotFound &&
					!string.IsNullOrWhiteSpace(effectiveBranch))
				{
					var originalException = exception;
					try
					{
						if (!await TryRestoreRemoteBranchAsync(
								entry.LocalPath,
								effectiveBranch,
								cancellationToken)
							.ConfigureAwait(false))
						{
							throw originalException;
						}

						prepared = await PrepareWorktreeAsync(
							entry.LocalPath,
							selectedPath,
							effectiveBranch,
							needsWorktreeCreation,
							cancellationToken).ConfigureAwait(false);
					}
					catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
					{
						throw;
					}
					catch
					{
						throw originalException;
					}
				}
				if (!prepared)
				{
					sessionLease.Dispose();
					return null;
				}
			}
			else if (kind == RepositoryCacheContentKind.Git)
			{
				effectiveBranch = await ResolveFallbackBranchAsync(
					selectedPath,
					requestedBranch,
					cancellationToken).ConfigureAwait(false);
			}

			entry = CommitSessionMetadata(
				fileSet,
				identity,
				entry.LocalPath,
				effectiveBranch,
				kind);
			if (entry is null)
			{
				sessionLease.Dispose();
				return null;
			}

			var session = new RepositoryCacheSession(
				selectedPath,
				entry.RepositoryUrl,
				entry.Branch,
				kind,
				sessionLease);
			sessionLease = null;
			if (kind == RepositoryCacheContentKind.Git && worktreesSupported)
				ScheduleUnusedWorktreeCleanup(entry.LocalPath, selectedPath);
			if (needsWorktreeCreation || entry.ApproximateSizeBytes <= 0)
				ScheduleRepositorySizeRefresh(entry.LocalPath);
			return session;
		}
		catch
		{
			sessionLease?.Dispose();
			throw;
		}
	}

	private RepositoryCacheIndexEntry? CommitSessionMetadata(
		JsonStoreFileSet fileSet,
		string identity,
		string expectedLocalPath,
		string? branch,
		RepositoryCacheContentKind kind)
	{
		if (!CrossProcessFileLock.TryAcquire(fileSet, IndexLockTimeout, out var heldLock))
			return null;

		using (heldLock)
		{
			if (HasUnsupportedIndexDocument(fileSet))
				return null;

			var document = LoadIndex(fileSet);
			var current = FindByIdentity(document, identity);
			if (current is null ||
			    !PathComparer.Default.Equals(current.LocalPath, expectedLocalPath) ||
			    !Directory.Exists(current.LocalPath))
			{
				return null;
			}

			var updated = current with
			{
				Branch = branch,
				LastUsedUtc = _timeProvider.GetUtcNow(),
				ContentKind = kind
			};
			if (!WriteIndex(
				fileSet,
				document.Entries
					.Where(candidate => !string.Equals(
						candidate.Identity,
						identity,
						StringComparison.Ordinal))
					.Append(updated)
					.OrderByDescending(static candidate => candidate.LastOpenedUtc)
					.ToList()))
			{
				return null;
			}
			return updated;
		}
	}

	private async Task<bool> PrepareWorktreeAsync(
		string basePath,
		string selectedPath,
		string? branch,
		bool needsWorktreeCreation,
		CancellationToken cancellationToken) =>
		needsWorktreeCreation
			? await _worktreeManager.CreateDetachedAsync(
				basePath,
				selectedPath,
				branch,
				cancellationToken).ConfigureAwait(false)
			: await _worktreeManager.PreparePrimaryAsync(
				selectedPath,
				branch,
				cancellationToken).ConfigureAwait(false);

	private static async Task<bool> TryRestoreRemoteBranchAsync(
		string repositoryPath,
		string branch,
		CancellationToken cancellationToken)
	{
		var normalizedBranch = GitBranchNameValidator.ValidateAndNormalize(branch);
		if (await RunGitForOutputAsync(
			    repositoryPath,
			    ["remote", "set-branches", "--add", "origin", normalizedBranch],
			    cancellationToken).ConfigureAwait(false) is null)
		{
			return false;
		}

		return await RunGitForOutputAsync(
			       repositoryPath,
			       ["fetch", "origin", normalizedBranch, "--depth", "1"],
			       cancellationToken).ConfigureAwait(false) is not null;
	}

	private static async Task<string?> ResolveFallbackBranchAsync(
		string repositoryPath,
		string? requestedBranch,
		CancellationToken cancellationToken)
	{
		var actualBranch = await ReadCurrentBranchAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
		if (!string.IsNullOrWhiteSpace(requestedBranch) &&
		    !string.Equals(actualBranch, requestedBranch, StringComparison.Ordinal))
		{
			throw new RepositoryBranchUnavailableException(
				requestedBranch,
				RepositoryBranchUnavailableReason.WorktreeUnsupported);
		}

		return actualBranch;
	}

	private static async Task<string?> ReadCurrentBranchAsync(
		string repositoryPath,
		CancellationToken cancellationToken)
	{
		var current = await RunGitForOutputAsync(
			repositoryPath,
			["rev-parse", "--abbrev-ref", "HEAD"],
			cancellationToken).ConfigureAwait(false);
		if (!string.IsNullOrWhiteSpace(current) && !string.Equals(current.Trim(), "HEAD", StringComparison.Ordinal))
			return current.Trim();

		var configured = await RunGitForOutputAsync(
			repositoryPath,
			["config", "--worktree", "--get", "devprojex.branch"],
			cancellationToken).ConfigureAwait(false);
		return string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
	}

	private static async Task<string?> RunGitForOutputAsync(
		string workingDirectory,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		using var process = new Process
		{
			StartInfo = GitProcessStartInfoFactory.Create(workingDirectory, arguments)
		};
		process.Start();
		process.StandardInput.Close();
		var output = GitProcessOutputReader.ReadAsync(
			process.StandardOutput,
			GitProcessOutputReader.MaximumOutputCharacters,
			cancellationToken);
		var error = GitProcessOutputReader.ReadAsync(
			process.StandardError,
			GitProcessOutputReader.MaximumOutputCharacters,
			cancellationToken);
		try
		{
			await GitRepositoryService
				.WaitForExitOrTerminateAsync(process, cancellationToken)
				.ConfigureAwait(false);
			if (!await GitProcessOutputReader
				    .WaitForCompletionAfterExitAsync(process, output, error)
				    .ConfigureAwait(false))
			{
				return null;
			}
			var standardOutput = await output.ConfigureAwait(false);
			var standardError = await error.ConfigureAwait(false);
			return process.ExitCode == 0 &&
			       !standardOutput.ExceededLimit &&
			       !standardError.ExceededLimit
				? standardOutput.Text
				: null;
		}
		catch (OperationCanceledException)
		{
			await GitProcessOutputReader
				.ObserveAfterTerminationAsync(process, output, error)
				.ConfigureAwait(false);
			throw;
		}
	}

	private void ScheduleUnusedWorktreeCleanup(string basePath, string retainedPath)
	{
		if (!_backgroundTasks.IsAccepting)
			return;

		while (true)
		{
			if (_worktreeCleanupInFlight.TryGetValue(basePath, out var active))
			{
				if (active.TryQueue(retainedPath))
					return;
				Thread.Yield();
				continue;
			}

			var state = new WorktreeCleanupState(retainedPath);
			if (!_worktreeCleanupInFlight.TryAdd(basePath, state))
				continue;
			if (!_backgroundTasks.TryRun(token => RunScheduledWorktreeCleanupAsync(basePath, state, token)))
			{
				_worktreeCleanupInFlight.TryRemove(
					new KeyValuePair<string, WorktreeCleanupState>(basePath, state));
			}
			return;
		}
	}

	private async Task RunScheduledWorktreeCleanupAsync(
		string basePath,
		WorktreeCleanupState state,
		CancellationToken cancellationToken)
	{
		var retainedPath = state.InitialRetainedPath;
		try
		{
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				timeout.CancelAfter(WorktreeCleanupTimeout);
				try
				{
					_testHooks?.BeforeUnusedWorktreeCleanup?.Invoke(basePath);
					await CleanupUnusedWorktreesAsync(basePath, retainedPath, timeout.Token).ConfigureAwait(false);
					_testHooks?.AfterUnusedWorktreeCleanup?.Invoke(basePath);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					return;
				}
				catch (OperationCanceledException) when (timeout.IsCancellationRequested)
				{
					Trace.TraceWarning("Repository worktree cleanup timed out for '{0}'.", basePath);
				}
				catch (Exception exception)
				{
					Trace.TraceWarning("Repository worktree cleanup failed for '{0}': {1}", basePath, exception.Message);
				}

				if (!state.TryTakePending(out retainedPath))
					return;
			}
		}
		finally
		{
			_worktreeCleanupInFlight.TryRemove(
				new KeyValuePair<string, WorktreeCleanupState>(basePath, state));
		}
	}

	private void ScheduleRepositorySizeRefresh(string basePath)
	{
		if (!_backgroundTasks.IsAccepting)
			return;

		while (true)
		{
			if (_repositorySizeRefreshInFlight.TryAdd(basePath, RepositorySizeRefreshRunning))
			{
				if (!_backgroundTasks.TryRun(token => RunScheduledRepositorySizeRefreshAsync(basePath, token)))
				{
					_repositorySizeRefreshInFlight.TryRemove(
						new KeyValuePair<string, byte>(basePath, RepositorySizeRefreshRunning));
				}
				return;
			}

			if (_repositorySizeRefreshInFlight.TryUpdate(
				    basePath,
				    RepositorySizeRefreshPending,
				    RepositorySizeRefreshRunning) ||
			    _repositorySizeRefreshInFlight.TryGetValue(basePath, out var state) &&
			    state == RepositorySizeRefreshPending)
			{
				return;
			}
		}
	}

	private Task RunScheduledRepositorySizeRefreshAsync(
		string basePath,
		CancellationToken cancellationToken)
	{
		try
		{
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
				{
					timeout.CancelAfter(RepositorySizeRefreshTimeout);
					try
					{
						_testHooks?.BeforeRepositorySizeRefresh?.Invoke(basePath);
						RefreshIndexedRepositorySize(basePath, timeout.Token);
					}
					catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
					{
						return Task.CompletedTask;
					}
					catch (OperationCanceledException) when (timeout.IsCancellationRequested)
					{
						Trace.TraceWarning("Repository size refresh timed out for '{0}'.", basePath);
					}
					catch (Exception exception)
					{
						Trace.TraceWarning(
							"Repository size refresh failed for '{0}': {1}",
							basePath,
							exception.Message);
					}
				}

				while (true)
				{
					if (_repositorySizeRefreshInFlight.TryUpdate(
						    basePath,
						    RepositorySizeRefreshRunning,
						    RepositorySizeRefreshPending))
						break;
					if (_repositorySizeRefreshInFlight.TryRemove(
						    new KeyValuePair<string, byte>(basePath, RepositorySizeRefreshRunning)))
					{
						_testHooks?.AfterRepositorySizeRefresh?.Invoke(basePath);
						return Task.CompletedTask;
					}
				}
			}
		}
		finally
		{
			_repositorySizeRefreshInFlight.TryRemove(basePath, out _);
		}
	}

	private async Task CleanupUnusedWorktreesAsync(
		string basePath,
		string retainedPath,
		CancellationToken cancellationToken)
	{
		var removedAny = false;
		await using (await RepositoryFileLease.AcquireExclusiveAsync(
			RepositoryCacheLayout.GetBaseOperationLockPath(CacheRootPath, basePath),
			cancellationToken).ConfigureAwait(false))
		{
			foreach (var candidate in RepositoryCacheLayout.EnumerateCopies(basePath))
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (PathComparer.Default.Equals(candidate, basePath) ||
				    PathComparer.Default.Equals(candidate, retainedPath))
				{
					continue;
				}

				if (!RepositoryFileLease.TryAcquireExclusive(
					    RepositoryCacheLayout.GetLeasePath(CacheRootPath, candidate),
					    out var lease))
				{
					continue;
				}

				using (lease)
				{
					try
					{
						await _worktreeManager.RemoveAsync(basePath, candidate, cancellationToken)
							.ConfigureAwait(false);
						removedAny = true;
					}
					catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
					{
						throw;
					}
					catch (Exception exception)
					{
						Trace.TraceWarning(
							"Repository worktree cleanup failed for '{0}': {1}",
							candidate,
							exception.Message);
					}
				}
			}

			if (removedAny)
			{
				try
				{
					await _worktreeManager.PruneAsync(basePath, cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception exception)
				{
					Trace.TraceWarning(
						"Repository worktree prune failed for '{0}': {1}",
						basePath,
						exception.Message);
				}
			}
		}

		if (removedAny)
			RefreshIndexedRepositorySize(basePath, cancellationToken);
	}

	private RepositoryFileLease? AcquireUniqueSnapshotLease(
		string snapshotPath,
		CancellationToken cancellationToken)
	{
		var container = RepositoryCacheLayout.GetContainer(snapshotPath);
		var leasesRoot = Path.Combine(container, RepositoryCacheLayout.LeasesDirectoryName);
		Directory.CreateDirectory(leasesRoot);
		for (var index = 1; ; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var leasePath = Path.Combine(leasesRoot, $"s-{index}.lock");
			if (RepositoryFileLease.TryAcquireExclusive(leasePath, out var lease))
				return lease;
		}
	}

	private static IEnumerable<string> OrderCandidateCopies(string basePath, string? requestedPath)
	{
		var copies = RepositoryCacheLayout.EnumerateCopies(basePath);
		if (!string.IsNullOrWhiteSpace(requestedPath))
		{
			foreach (var copy in copies)
			{
				if (PathComparer.Default.Equals(copy, requestedPath))
					yield return copy;
			}
		}

		foreach (var copy in copies)
		{
			if (string.IsNullOrWhiteSpace(requestedPath) ||
			    !PathComparer.Default.Equals(copy, requestedPath))
			{
				yield return copy;
			}
		}
	}

	private void RecordIndexedRepositoryCore(
		string repositoryUrl,
		string localPath,
		string? branch,
		string? commitHash,
		RepositoryCacheEntryState state,
		long? approximateSizeBytes,
		RepositoryCacheContentKind contentKind)
	{
		var safeUrl = RepositoryUrlUtility.ToSafeDisplay(repositoryUrl);
		var identity = RepositoryUrlUtility.GetComparisonKey(safeUrl);
		if (identity.Length == 0 || string.IsNullOrWhiteSpace(localPath))
			return;

		string normalizedPath;
		try
		{
			normalizedPath = ResolveIndexedBasePath(PathUtility.Normalize(localPath));
		}
		catch
		{
			return;
		}

		if (!IsInCache(normalizedPath))
			return;

		var fileSet = GetIndexFileSet();
		if (!CrossProcessFileLock.TryAcquire(fileSet, IndexLockTimeout, out var heldLock))
			return;

		using (heldLock)
		{
			if (HasUnsupportedIndexDocument(fileSet))
				return;

			var document = LoadIndex(fileSet);
			var previous = FindByIdentity(document, identity) ?? FindByPath(document, normalizedPath);
			var resolvedKind = contentKind != RepositoryCacheContentKind.Unknown
				? contentKind
				: previous is null ? InferContentKind(normalizedPath) : ResolveContentKind(previous);
			var entry = new RepositoryCacheIndexEntry(
				identity,
				safeUrl,
				normalizedPath,
				string.IsNullOrWhiteSpace(branch) ? previous?.Branch : branch.Trim(),
				string.IsNullOrWhiteSpace(commitHash) ? previous?.CommitHash : commitHash.Trim(),
				_timeProvider.GetUtcNow(),
				state,
				Math.Max(0, approximateSizeBytes ?? previous?.ApproximateSizeBytes ?? 0),
				resolvedKind);
			var entries = document.Entries
				.Where(candidate =>
					!string.Equals(candidate.Identity, identity, StringComparison.Ordinal) &&
					!ArePathsInSameRepository(candidate.LocalPath, normalizedPath))
				.ToList();
			entries.Add(entry);
			WriteIndex(fileSet, entries);
		}
	}

	private RepositoryCacheIndexEntry? FindIndexedRepositoryByIdentity(string identity)
	{
		var fileSet = GetIndexFileSet();
		if (!CrossProcessFileLock.TryAcquire(fileSet, IndexLockTimeout, out var heldLock))
			return null;
		using (heldLock)
			return FindByIdentity(LoadIndex(fileSet), identity);
	}

	private RepositoryCacheIndexEntry? TryMigrateLegacyGitRepository(string identity)
	{
		var fileSet = GetIndexFileSet();
		if (!CrossProcessFileLock.TryAcquire(fileSet, IndexLockTimeout, out var heldLock))
			return null;

		using (heldLock)
		{
			if (HasUnsupportedIndexDocument(fileSet))
				return null;

			var document = LoadIndex(fileSet);
			var entry = FindByIdentity(document, identity);
			if (entry is null ||
			    ResolveContentKind(entry) != RepositoryCacheContentKind.Git ||
			    RepositoryCacheLayout.IsManaged(entry.LocalPath) ||
			    !Directory.Exists(entry.LocalPath))
			{
				return entry;
			}

			var legacyPath = entry.LocalPath;
			var container = CreateUniqueRepositoryPath(GetOwningCacheRoot(legacyPath), entry.RepositoryUrl);
			var basePath = Path.Combine(container, RepositoryCacheLayout.BaseDirectoryName);
			var moved = false;
			try
			{
				Directory.CreateDirectory(container);
				Directory.Move(legacyPath, basePath);
				moved = true;
				File.WriteAllText(
					Path.Combine(container, RepositoryCacheLayout.MarkerFileName),
					"git");
				var migrated = entry with
				{
					LocalPath = basePath,
					ApproximateSizeBytes = CalculateDirectorySize(container),
					ContentKind = RepositoryCacheContentKind.Git
				};
				var entries = document.Entries
					.Where(candidate => !string.Equals(
						candidate.Identity,
						identity,
						StringComparison.Ordinal))
					.Append(migrated)
					.ToList();
				if (WriteIndex(fileSet, entries))
					return migrated;
			}
			catch (Exception ex) when (ex is
				IOException or
				UnauthorizedAccessException or
				ArgumentException or
				NotSupportedException)
			{
			}

			if (moved)
			{
				try
				{
					Directory.Move(basePath, legacyPath);
					moved = false;
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
				}
			}
			if (!moved)
				TryDeleteTree(container);
			return moved ? null : entry;
		}
	}

	private RepositoryCacheIndexEntry? FindIndexedRepositoryByPath(string path)
	{
		var fileSet = GetIndexFileSet();
		if (!CrossProcessFileLock.TryAcquire(fileSet, IndexLockTimeout, out var heldLock))
			return null;
		using (heldLock)
			return FindByPath(LoadIndex(fileSet), path);
	}

	private RepositoryCacheIndexEntry? FindIndexedRepositoryByPathAcrossRoots(string path)
	{
		RepositoryCacheIndexEntry? matchingEntry = null;
		foreach (var searchRoot in CacheSearchRootPaths)
		{
			var fileSet = GetIndexFileSet(searchRoot);
			if (!File.Exists(fileSet.PrimaryPath) && !File.Exists(fileSet.BackupPath))
				continue;
			if (!CrossProcessFileLock.TryAcquire(fileSet, IndexLockTimeout, out var heldLock))
				continue;

			using (heldLock)
			{
				var candidate = FindByPath(LoadIndex(fileSet), path);
				if (candidate is not null &&
				    (matchingEntry is null || candidate.LastOpenedUtc > matchingEntry.LastOpenedUtc))
				{
					matchingEntry = candidate;
				}
			}
		}

		return matchingEntry;
	}

	private static RepositoryCacheIndexEntry? FindByIdentity(
		RepositoryCacheIndexDocument document,
		string identity) =>
		document.Entries
			.Where(entry => string.Equals(entry.Identity, identity, StringComparison.Ordinal))
			.OrderByDescending(static entry => entry.LastOpenedUtc)
			.FirstOrDefault();

	private static RepositoryCacheIndexEntry? FindByPath(
		RepositoryCacheIndexDocument document,
		string path) =>
		document.Entries.FirstOrDefault(entry => ArePathsInSameRepository(entry.LocalPath, path));

	public bool PathsBelongToSameRepository(string left, string right) =>
		ArePathsInSameRepository(left, right);

	private static bool ArePathsInSameRepository(string left, string right)
	{
		try
		{
			return PathComparer.Default.Equals(
				RepositoryCacheLayout.GetContainer(left),
				RepositoryCacheLayout.GetContainer(right));
		}
		catch
		{
			return false;
		}
	}

	private static string ResolveIndexedBasePath(string path)
	{
		var container = RepositoryCacheLayout.GetContainer(path);
		if (!File.Exists(Path.Combine(container, RepositoryCacheLayout.MarkerFileName)))
			return path;

		var gitBase = Path.Combine(container, RepositoryCacheLayout.BaseDirectoryName);
		if (Directory.Exists(gitBase))
			return gitBase;
		var snapshot = Path.Combine(container, RepositoryCacheLayout.SnapshotDirectoryName);
		return Directory.Exists(snapshot) ? snapshot : path;
	}

	private static RepositoryCacheContentKind ResolveContentKind(RepositoryCacheIndexEntry entry) =>
		entry.ContentKind == RepositoryCacheContentKind.Unknown
			? InferContentKind(entry.LocalPath)
			: entry.ContentKind;

	private static RepositoryCacheContentKind InferContentKind(string path) =>
		Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"))
			? RepositoryCacheContentKind.Git
			: RepositoryCacheContentKind.Zip;

	private bool TryAcquireAllRepositoryLeases(
		string repositoryPath,
		out CompositeLease? composite)
	{
		composite = new CompositeLease();
		try
		{
			var container = RepositoryCacheLayout.GetContainer(repositoryPath);
			var leasePaths = new HashSet<string>(PathComparer.Default)
			{
				RepositoryCacheLayout.GetLeasePath(CacheRootPath, repositoryPath),
				RepositoryCacheLayout.GetBaseOperationLockPath(CacheRootPath, repositoryPath)
			};
			foreach (var copy in RepositoryCacheLayout.EnumerateCopies(repositoryPath))
				leasePaths.Add(RepositoryCacheLayout.GetLeasePath(CacheRootPath, copy));
			var managedContainer = File.Exists(
				Path.Combine(container, RepositoryCacheLayout.MarkerFileName));
			var leasesRoot = Path.Combine(container, RepositoryCacheLayout.LeasesDirectoryName);
			if (managedContainer && Directory.Exists(leasesRoot))
			{
				foreach (var path in Directory.EnumerateFiles(leasesRoot, "*.lock"))
					leasePaths.Add(path);
			}

			foreach (var leasePath in leasePaths)
			{
				if (!RepositoryFileLease.TryAcquireExclusive(leasePath, out var lease))
				{
					composite.Dispose();
					composite = null;
					return false;
				}
				composite.Add(lease!);
			}
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			composite?.Dispose();
			composite = null;
			return false;
		}
	}

	private void MoveToTrashAndClean(
		string path,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!Directory.Exists(path) || !IsInCache(path))
			return;

		var trashRoot = RepositoryCacheLayout.GetTrashRoot(GetOwningCacheRoot(path));
		try
		{
			Directory.CreateDirectory(trashRoot);
			string destination;
			for (var index = 1; ; index++)
			{
				destination = Path.Combine(trashRoot, $"trash-{index}");
				if (!Directory.Exists(destination) && !File.Exists(destination))
					break;
			}
			Directory.Move(path, destination);
			TryDeleteTree(destination, cancellationToken);
			TryDeleteEmptyDirectory(trashRoot);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			// A locked path remains in place or in trash and is retried by a later collection.
			try
			{
				if (Directory.Exists(path))
					File.WriteAllText(Path.Combine(path, RepositoryCacheLayout.DeletePendingMarkerName), string.Empty);
			}
			catch
			{
			}
		}
	}

	private void CleanupTrash(CancellationToken cancellationToken = default)
	{
		foreach (var cacheRoot in CacheSearchRootPaths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			CleanupTrash(cacheRoot, cancellationToken);
		}
	}

	private static void CleanupTrash(
		string cacheRoot,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (IsLinkedCacheRoot(cacheRoot))
			return;
		var trashRoot = RepositoryCacheLayout.GetTrashRoot(cacheRoot);
		if (!Directory.Exists(trashRoot))
			return;

		try
		{
			foreach (var path in Directory.EnumerateDirectories(trashRoot))
			{
				cancellationToken.ThrowIfCancellationRequested();
				TryDeleteTree(path, cancellationToken);
			}
			TryDeleteEmptyDirectory(trashRoot);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
		}
	}

	private void CleanupUnindexedRepositories(CancellationToken cancellationToken = default)
		=> CleanupUnindexedRepositories(CacheRootPath, cancellationToken);

	private void CleanupUnindexedRepositories(
		string cacheRoot,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var fileSet = GetIndexFileSet(cacheRoot);
		if (!CrossProcessFileLock.TryAcquireWithCancellation(
			    fileSet,
			    IndexLockTimeout,
			    cancellationToken,
			    out var heldLock))
			return;

		var trashPaths = new List<string>();
		using (heldLock)
		{
			var indexedContainers = LoadIndex(fileSet).Entries
				.Select(entry => RepositoryCacheLayout.GetContainer(entry.LocalPath))
				.ToHashSet(PathComparer.Default);
			foreach (var directory in EnumerateRepositoryRootDirectories(cacheRoot))
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (indexedContainers.Contains(directory))
					continue;
				if (!File.Exists(Path.Combine(directory, RepositoryCacheLayout.MarkerFileName)) &&
				    !File.Exists(Path.Combine(directory, RepositoryCacheLayout.DeletePendingMarkerName)))
				{
					continue;
				}
				if (!TryAcquireAllRepositoryLeases(directory, out var lease))
				{
					continue;
				}
				using (lease)
					trashPaths.Add(directory);
			}
		}

		foreach (var path in trashPaths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			MoveToTrashAndClean(path, cancellationToken);
		}
	}

	private void CleanupStaging(CancellationToken cancellationToken = default)
	{
		foreach (var cacheRoot in CacheSearchRootPaths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			CleanupStaging(cacheRoot, cancellationToken);
		}
	}

	private void CleanupStaging(
		string cacheRoot,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (IsLinkedCacheRoot(cacheRoot))
			return;
		var stagingRoot = Path.Combine(cacheRoot, RepositoryCacheLayout.StagingDirectoryName);
		if (!Directory.Exists(stagingRoot))
			return;

		try
		{
			var staleThreshold = _timeProvider.GetUtcNow().UtcDateTime.AddHours(-24);
			foreach (var directory in Directory.EnumerateDirectories(stagingRoot))
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (string.Equals(
					Path.GetFileName(directory),
					RepositoryCacheLayout.TrashDirectoryName,
					StringComparison.Ordinal))
				{
					continue;
				}

				try
				{
					if (Directory.GetCreationTimeUtc(directory) < staleThreshold)
						MoveToTrashAndClean(directory, cancellationToken);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				catch
				{
				}
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
		}
	}

	private static IEnumerable<string> EnumerateRepositoryRootDirectories(string cacheRoot)
	{
		if (!Directory.Exists(cacheRoot) || IsLinkedCacheRoot(cacheRoot))
			yield break;

		IEnumerable<string> directories;
		try
		{
			directories = Directory.EnumerateDirectories(cacheRoot).ToArray();
		}
		catch
		{
			yield break;
		}

		foreach (var directory in directories)
		{
			var name = Path.GetFileName(directory);
			if (string.Equals(name, RepositoryCacheLayout.StagingDirectoryName, StringComparison.Ordinal) ||
			    string.Equals(name, RepositoryCacheLayout.LeasesDirectoryName, StringComparison.Ordinal) ||
			    string.Equals(name, RepositoryCacheLayout.LocksDirectoryName, StringComparison.Ordinal))
			{
				continue;
			}
			yield return directory;
		}
	}

	private string GetOwningCacheRoot(string path)
	{
		foreach (var cacheRoot in CacheSearchRootPaths)
		{
			try
			{
				if (PathUtility.IsPathInside(path, cacheRoot))
					return cacheRoot;
			}
			catch
			{
			}
		}
		return CacheRootPath;
	}

	private static void TryDeleteTree(
		string path,
		CancellationToken cancellationToken = default)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!Directory.Exists(path))
				return;
			if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
			{
				cancellationToken.ThrowIfCancellationRequested();
				Directory.Delete(path);
				return;
			}
			foreach (var file in Directory.EnumerateFiles(path, "*", RecursiveCacheEnumeration))
			{
				cancellationToken.ThrowIfCancellationRequested();
				try
				{
					File.SetAttributes(file, FileAttributes.Normal);
				}
				catch
				{
				}
			}
			foreach (var directory in Directory
				         .EnumerateDirectories(path, "*", RecursiveCacheEnumeration)
				         .OrderByDescending(static directory => directory.Length))
			{
				cancellationToken.ThrowIfCancellationRequested();
				try
				{
					File.SetAttributes(directory, FileAttributes.Directory);
				}
				catch
				{
				}
			}
			try
			{
				File.SetAttributes(path, FileAttributes.Directory);
			}
			catch
			{
			}
			cancellationToken.ThrowIfCancellationRequested();
			Directory.Delete(path, recursive: true);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
		}
	}

	private static void TryDeleteEmptyDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
				Directory.Delete(path);
		}
		catch
		{
		}
	}

	private static long CalculateDirectorySize(
		string path,
		CancellationToken cancellationToken = default)
	{
		long total = 0;
		try
		{
			foreach (var file in Directory.EnumerateFiles(path, "*", RecursiveCacheEnumeration))
			{
				cancellationToken.ThrowIfCancellationRequested();
				try
				{
					total = checked(total + new FileInfo(file).Length);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
				{
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
		return total;
	}

	private static long CalculateIndexedSize(IEnumerable<RepositoryCacheIndexEntry> entries)
	{
		long total = 0;
		foreach (var entry in entries)
		{
			var size = Math.Max(0, entry.ApproximateSizeBytes);
			if (long.MaxValue - total < size)
				return long.MaxValue;
			total += size;
		}
		return total;
	}

	private static string CreateUniqueRepositoryPath(string root, string repositoryUrl)
	{
		var repositoryName = ExtractRepoName(repositoryUrl);
		while (true)
		{
			var suffix = $"{DateTime.UtcNow.Ticks:X}{Guid.NewGuid():N}"[..UniquePathSuffixLength]
				.ToUpperInvariant();
			var path = Path.Combine(root, $"{repositoryName}_{suffix}");
			if (!Directory.Exists(path) && !File.Exists(path))
				return path;
		}
	}

	private static string ExtractRepoName(string url)
	{
		if (string.IsNullOrWhiteSpace(url))
			return "repo";

		try
		{
			var cleanUrl = url.TrimEnd('/');
			if (cleanUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
				cleanUrl = cleanUrl[..^4];
			var lastSlashIndex = cleanUrl.LastIndexOf('/');
			var repositoryName = lastSlashIndex >= 0 ? cleanUrl[(lastSlashIndex + 1)..] : cleanUrl;
			return SanitizeFileName(repositoryName);
		}
		catch
		{
			return "repo";
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static string SanitizeFileName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return "repo";
		var span = name.AsSpan();
		if (!span.ContainsAny(InvalidFileNameChars) && !ContainsControlChars(span))
		{
			var trimmed = TrimUnsafeTrailingCharacters(name.Trim());
			return string.IsNullOrWhiteSpace(trimmed) ? "repo" : NormalizeReservedFileName(trimmed);
		}

		var sanitized = new StringBuilder(name.Length);
		foreach (var character in span)
		{
			if (!InvalidFileNameChars.Contains(character) && !char.IsControl(character))
				sanitized.Append(character);
		}
		var result = TrimUnsafeTrailingCharacters(sanitized.ToString().Trim());
		return string.IsNullOrWhiteSpace(result) ? "repo" : NormalizeReservedFileName(result);
	}

	private static string TrimUnsafeTrailingCharacters(string name) => name.TrimEnd(' ', '.');

	private static string NormalizeReservedFileName(string name)
	{
		if (name.Length == 0)
			return "repo";
		var dotIndex = name.IndexOf('.');
		var baseName = dotIndex >= 0 ? name.AsSpan(0, dotIndex) : name.AsSpan();
		if (IsWindowsReservedFileName(baseName))
		{
			name = dotIndex >= 0
				? string.Concat(name.AsSpan(0, dotIndex), "_repo", name.AsSpan(dotIndex))
				: name + "_repo";
		}
		var bounded = new StringBuilder(Math.Min(name.Length, MaximumRepositoryNameUtf16Length));
		var utf8Bytes = 0;
		foreach (var rune in name.EnumerateRunes())
		{
			if (bounded.Length + rune.Utf16SequenceLength > MaximumRepositoryNameUtf16Length ||
			    utf8Bytes + rune.Utf8SequenceLength > MaximumRepositoryNameUtf8Bytes)
			{
				break;
			}
			bounded.Append(rune);
			utf8Bytes += rune.Utf8SequenceLength;
		}
		var result = TrimUnsafeTrailingCharacters(bounded.ToString());
		return result.Length == 0 ? "repo" : result;
	}

	private static bool IsWindowsReservedFileName(ReadOnlySpan<char> name)
	{
		if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
		    name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
		    name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
		    name.Equals("NUL", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		return name.Length == 4 && name[3] is >= '1' and <= '9' &&
		       (name[..3].Equals("COM", StringComparison.OrdinalIgnoreCase) ||
		        name[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool ContainsControlChars(ReadOnlySpan<char> span)
	{
		foreach (var character in span)
		{
			if (char.IsControl(character))
				return true;
		}
		return false;
	}

	private JsonStoreFileSet GetIndexFileSet() => GetIndexFileSet(CacheRootPath);

	private static JsonStoreFileSet GetIndexFileSet(string cacheRoot)
	{
		EnsurePrivateCacheDirectory(cacheRoot);
		var primaryPath = Path.Combine(cacheRoot, CacheIndexFileName);
		return new JsonStoreFileSet(primaryPath, $"{primaryPath}.bak", $"{primaryPath}.lock");
	}

	private static void EnsurePrivateCacheDirectory(string path)
	{
		if (IsLinkedCacheRoot(path))
			throw new IOException(LinkedCacheRootMessage);

		if (OperatingSystem.IsWindows())
		{
			Directory.CreateDirectory(path);
			if (IsLinkedCacheRoot(path))
				throw new IOException(LinkedCacheRootMessage);
			return;
		}

		Directory.CreateDirectory(path, PrivateUnixDirectoryMode);
		if (IsLinkedCacheRoot(path))
			throw new IOException(LinkedCacheRootMessage);
		File.SetUnixFileMode(path, PrivateUnixDirectoryMode);
	}

	private static bool IsLinkedCacheRoot(string path)
	{
		try
		{
			return Directory.Exists(path) &&
			       File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
		}
		catch (Exception exception) when (exception is
			       IOException or
			       UnauthorizedAccessException or
			       ArgumentException or
			       NotSupportedException or
			       System.Security.SecurityException)
		{
			return true;
		}
	}

	private static IReadOnlyList<string> BuildCacheSearchRoots(
		string currentCacheRoot,
		Func<string>? legacyDataRootProvider)
	{
		var roots = new List<string> { currentCacheRoot };
		if (legacyDataRootProvider is not null)
		{
			try
			{
				var legacyCacheRoot = Path.Combine(
					legacyDataRootProvider(),
					AppFolderName,
					CacheFolderName);
				if (!roots.Any(root => PathComparer.Default.Equals(root, legacyCacheRoot)))
					roots.Add(legacyCacheRoot);
			}
			catch (Exception ex) when (ex is
				ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException or
				InvalidOperationException or System.Security.SecurityException)
			{
			}
		}
		return roots.AsReadOnly();
	}

	private RepositoryCacheIndexDocument LoadIndex(JsonStoreFileSet fileSet)
	{
		if (TryLoadIndex(fileSet.PrimaryPath, out var primary))
			return primary;
		if (TryLoadIndex(fileSet.BackupPath, out var backup))
			return backup;
		return RepositoryCacheIndexDocument.Empty;
	}

	private bool TryLoadIndex(string path, out RepositoryCacheIndexDocument document)
	{
		if (!JsonStorePersistence.TryReadNormalized(
			    path,
			    IndexSerializerOptions,
			    static () => RepositoryCacheIndexDocument.Empty,
			    NormalizeIndex,
			    out document,
			    out _,
			    MaximumCacheIndexBytes))
		{
			document = RepositoryCacheIndexDocument.Empty;
			return false;
		}
		return document.SchemaVersion <= CacheIndexSchemaVersion;
	}

	private RepositoryCacheIndexDocument NormalizeIndex(RepositoryCacheIndexDocument document)
	{
		var utcNow = _timeProvider.GetUtcNow();
		var maximumAcceptedTimestamp = utcNow <= DateTimeOffset.MaxValue - MaximumPersistedClockSkew
			? utcNow + MaximumPersistedClockSkew
			: DateTimeOffset.MaxValue;
		var entries = (document.Entries ?? [])
			.Where(entry => entry is not null &&
			                !string.IsNullOrWhiteSpace(entry.Identity) &&
			                !string.IsNullOrWhiteSpace(entry.RepositoryUrl) &&
			                !string.IsNullOrWhiteSpace(entry.LocalPath) &&
			                IsInCache(entry.LocalPath))
			.Select(entry => NormalizeIndexEntryOrNull(entry, maximumAcceptedTimestamp))
			.Where(static entry => entry is not null)
			.Select(static entry => entry!)
			.GroupBy(static entry => entry.Identity, StringComparer.Ordinal)
			.Select(static group => group.OrderByDescending(entry => entry.LastOpenedUtc).First())
			.OrderByDescending(static entry => entry.LastOpenedUtc)
			.ToList();
		return new RepositoryCacheIndexDocument(CacheIndexSchemaVersion, entries);
	}

	private static RepositoryCacheIndexEntry? NormalizeIndexEntryOrNull(
		RepositoryCacheIndexEntry entry,
		DateTimeOffset maximumAcceptedTimestamp)
	{
		try
		{
			var safeUrl = RepositoryUrlUtility.ToSafeDisplay(entry.RepositoryUrl);
			if (safeUrl.Length == 0)
				return null;
			return entry with
			{
				RepositoryUrl = safeUrl,
				LastUsedUtc = entry.LastUsedUtc <= DateTimeOffset.UnixEpoch ||
				              entry.LastUsedUtc > maximumAcceptedTimestamp
					? DateTimeOffset.UnixEpoch
					: entry.LastUsedUtc
			};
		}
		catch
		{
			return null;
		}
	}

	private static bool WriteIndex(
		JsonStoreFileSet fileSet,
		List<RepositoryCacheIndexEntry> entries) =>
		JsonStorePersistence.TryWriteAtomic(
			fileSet,
			new RepositoryCacheIndexDocument(CacheIndexSchemaVersion, entries),
			IndexSerializerOptions,
			MaximumCacheIndexBytes);

	private static bool HasUnsupportedIndexDocument(JsonStoreFileSet fileSet) =>
		JsonStorePersistence.ContainsFutureDocument(
			fileSet,
			CacheIndexSchemaVersion,
			maximumDocumentBytes: MaximumCacheIndexBytes);

	private sealed class BackgroundTaskOwner(TimeSpan shutdownTimeout) : IDisposable, IAsyncDisposable
	{
		private readonly object _sync = new();
		private readonly CancellationTokenSource _shutdown = new();
		private readonly HashSet<Task> _tasks = [];
		private Task? _disposeTask;
		private int _stopping;

		public bool IsAccepting => Volatile.Read(ref _stopping) == 0;

		public bool TryRun(Func<CancellationToken, Task> operation)
		{
			ArgumentNullException.ThrowIfNull(operation);
			Task task;
			lock (_sync)
			{
				if (_stopping != 0)
					return false;
				task = Task.Run(() => operation(_shutdown.Token), CancellationToken.None);
				_tasks.Add(task);
			}
			_ = task.ContinueWith(
				static (completedTask, state) => ((BackgroundTaskOwner)state!).OnTaskCompleted(completedTask),
				this,
				CancellationToken.None,
				TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);
			return true;
		}

		public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

		public ValueTask DisposeAsync()
		{
			Task completion;
			Task[]? tasks = null;
			TaskCompletionSource<object?>? completionSource = null;
			lock (_sync)
			{
				if (_disposeTask is null)
				{
					Volatile.Write(ref _stopping, 1);
					tasks = [.. _tasks];
					completionSource = new TaskCompletionSource<object?>(
						TaskCreationOptions.RunContinuationsAsynchronously);
					_disposeTask = completionSource.Task;
				}
				completion = _disposeTask;
			}

			if (completionSource is not null)
			{
				try
				{
					_shutdown.Cancel(throwOnFirstException: false);
				}
				catch (Exception exception)
				{
					Trace.TraceWarning("Repository cache background cancellation failed: {0}", exception.Message);
				}
				_ = CompleteDisposalAsync(tasks!, completionSource);
			}
			return new ValueTask(completion);
		}

		private void OnTaskCompleted(Task task)
		{
			_ = task.Exception;
			lock (_sync)
				_tasks.Remove(task);
		}

		private async Task CompleteDisposalAsync(
			Task[] tasks,
			TaskCompletionSource<object?> completionSource)
		{
			var allTasks = Task.WhenAll(tasks);
			try
			{
				await allTasks.WaitAsync(shutdownTimeout).ConfigureAwait(false);
				_shutdown.Dispose();
			}
			catch (TimeoutException)
			{
				Trace.TraceWarning(
					"Repository cache background shutdown exceeded {0} seconds.",
					shutdownTimeout.TotalSeconds);
				_ = ObserveLateCompletionAsync(allTasks);
			}
			catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
			{
				_shutdown.Dispose();
			}
			catch (Exception exception)
			{
				Trace.TraceWarning("Repository cache background operation failed during shutdown: {0}", exception.Message);
				_shutdown.Dispose();
			}
			finally
			{
				completionSource.TrySetResult(null);
			}
		}

		private async Task ObserveLateCompletionAsync(Task tasks)
		{
			try
			{
				await tasks.ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				Trace.TraceWarning("Repository cache background operation failed after shutdown: {0}", exception.Message);
			}
			finally
			{
				_shutdown.Dispose();
			}
		}
	}

	private sealed class WorktreeCleanupState(string initialRetainedPath)
	{
		private readonly object _sync = new();
		private string _pendingRetainedPath = initialRetainedPath;
		private bool _hasPending;
		private bool _isCompleting;

		public string InitialRetainedPath { get; } = initialRetainedPath;

		public bool TryQueue(string retainedPath)
		{
			lock (_sync)
			{
				if (_isCompleting)
					return false;
				_pendingRetainedPath = retainedPath;
				_hasPending = true;
				return true;
			}
		}

		public bool TryTakePending(out string retainedPath)
		{
			lock (_sync)
			{
				if (_hasPending)
				{
					_hasPending = false;
					retainedPath = _pendingRetainedPath;
					return true;
				}
				_isCompleting = true;
				retainedPath = string.Empty;
				return false;
			}
		}
	}

	private sealed record RepositoryCacheIndexDocument(
		int SchemaVersion,
		List<RepositoryCacheIndexEntry> Entries)
	{
		public static RepositoryCacheIndexDocument Empty => new(CacheIndexSchemaVersion, []);
	}

	private sealed class CompositeLease : IDisposable
	{
		private readonly List<RepositoryFileLease> _leases = [];
		public void Add(RepositoryFileLease lease) => _leases.Add(lease);
		public void Dispose()
		{
			foreach (var lease in _leases)
				lease.Dispose();
			_leases.Clear();
		}
	}
}

internal sealed class RepoCacheTestHooks
{
	public Action<string>? AfterSessionLeaseAcquired { get; init; }
	public Action? BeforeScheduledGarbageCollection { get; init; }
	public Action? AfterScheduledGarbageCollection { get; init; }
	public Action<string>? BeforeUnusedWorktreeCleanup { get; init; }
	public Action<string>? AfterUnusedWorktreeCleanup { get; init; }
	public Action<string>? BeforeRepositorySizeRefresh { get; init; }
	public Action<string>? AfterRepositorySizeCalculated { get; init; }
	public Action<string>? AfterRepositorySizeRefresh { get; init; }
}
