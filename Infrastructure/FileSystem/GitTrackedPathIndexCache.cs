using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using DevProjex.Infrastructure.Git;

namespace DevProjex.Infrastructure.FileSystem;

internal static class GitTrackedPathIndexCache
{
	private const int CacheEntryLimit = 128;
	private const long CacheByteLimit = 16L * 1024 * 1024;
	private const long MaximumSingleEntryBytes = 64L * 1024 * 1024;
	private const long EstimatedEmptyIndexBytes = 64;
	private const int GitFileMaximumLength = 64 * 1024;
	private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
	private static readonly object CacheSync = new();
	private static readonly Dictionary<string, LinkedListNode<CacheEntry>> Cache =
		new(PathComparer.Default);
	private static readonly LinkedList<CacheEntry> CacheLru = new();
	private static readonly ConcurrentDictionary<string, Lazy<Task<LoadedGitTrackedPathIndex?>>> InFlightLoads =
		new(PathComparer.Default);
	private static long _cacheSizeBytes;
	private static int _gitAvailability;

	public static bool TryLoad(
		string repositoryRootPath,
		string gitMetadataPath,
		CancellationToken cancellationToken,
		out GitTrackedPathIndex trackedPathIndex)
	{
		trackedPathIndex = null!;
		cancellationToken.ThrowIfCancellationRequested();

		if (!TryCreateIndexSignature(repositoryRootPath, gitMetadataPath, out var signature))
		{
			return false;
		}
		if (TryGetCached(signature, out trackedPathIndex))
			return true;
		if (!signature.HasPhysicalIndex)
		{
			trackedPathIndex = new GitTrackedPathIndex(
				signature.RepositoryRootPath,
				[],
				signature.ComparisonSemantics);
			Store(signature, new LoadedGitTrackedPathIndex(trackedPathIndex, EstimatedEmptyIndexBytes));
			return true;
		}
		if (Volatile.Read(ref _gitAvailability) < 0)
			return false;

		var loadKey = signature.CreateLoadKey();
		var lazyLoad = InFlightLoads.GetOrAdd(
			loadKey,
			_ => new Lazy<Task<LoadedGitTrackedPathIndex?>>(
				() => LoadAndStoreSafelyAsync(signature),
				LazyThreadSafetyMode.ExecutionAndPublication));
		var loadTask = lazyLoad.Value;
		RemoveCompletedLoad(loadKey, lazyLoad, loadTask);

		var loaded = loadTask
			.WaitAsync(cancellationToken)
			.GetAwaiter()
			.GetResult();
		if (loaded is null)
			return false;

		trackedPathIndex = loaded.Index;
		return true;
	}

	public static bool TryLoadNearest(
		string scanRootPath,
		CancellationToken cancellationToken,
		out GitTrackedPathIndex trackedPathIndex)
	{
		trackedPathIndex = null!;
		string? currentPath;
		try
		{
			currentPath = PathUtility.Normalize(scanRootPath);
		}
		catch
		{
			return false;
		}

		while (!string.IsNullOrWhiteSpace(currentPath))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var gitMetadataPath = Path.Combine(currentPath, ".git");
			if (TryMetadataEntryExists(gitMetadataPath))
			{
				if (TryLoad(
					currentPath,
					gitMetadataPath,
					cancellationToken,
					out trackedPathIndex))
				{
					return true;
				}

				// The nearest metadata entry still owns this subtree when its index is
				// unreadable or invalid. Retaining an unavailable boundary prevents an
				// ancestor repository from leaking tracked paths into the selection.
				trackedPathIndex = GitTrackedPathIndex.Unavailable(currentPath);
				return true;
			}

			var parentPath = Path.GetDirectoryName(currentPath);
			if (string.IsNullOrWhiteSpace(parentPath) || PathComparer.Default.Equals(parentPath, currentPath))
				break;
			currentPath = parentPath;
		}

		return false;
	}

	internal static bool TryFindNearestRepositoryBoundary(
		string scanRootPath,
		CancellationToken cancellationToken,
		out string repositoryRootPath)
	{
		repositoryRootPath = string.Empty;
		string? currentPath;
		try
		{
			currentPath = PathUtility.Normalize(scanRootPath);
		}
		catch
		{
			return false;
		}

		while (!string.IsNullOrWhiteSpace(currentPath))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (TryMetadataEntryExists(Path.Combine(currentPath, ".git")))
			{
				repositoryRootPath = currentPath;
				return true;
			}

			var parentPath = Path.GetDirectoryName(currentPath);
			if (string.IsNullOrWhiteSpace(parentPath) || PathComparer.Default.Equals(parentPath, currentPath))
				break;
			currentPath = parentPath;
		}

		return false;
	}

	private static async Task<LoadedGitTrackedPathIndex?> LoadAndStoreSafelyAsync(
		GitIndexSignature signature)
	{
		try
		{
			var loaded = await LoadAsync(signature).ConfigureAwait(false);
			if (loaded is not null)
				Store(signature, loaded);
			return loaded;
		}
		catch
		{
			return null;
		}
	}

	private static void RemoveCompletedLoad(
		string loadKey,
		Lazy<Task<LoadedGitTrackedPathIndex?>> lazyLoad,
		Task<LoadedGitTrackedPathIndex?> loadTask)
	{
		_ = loadTask.ContinueWith(
			static (_, state) =>
			{
				var completedLoad = ((string Key, Lazy<Task<LoadedGitTrackedPathIndex?>> Load))state!;
				InFlightLoads.TryRemove(
					new KeyValuePair<string, Lazy<Task<LoadedGitTrackedPathIndex?>>>(
						completedLoad.Key,
						completedLoad.Load));
			},
			(loadKey, lazyLoad),
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
	}

	private static async Task<LoadedGitTrackedPathIndex?> LoadAsync(
		GitIndexSignature signature)
	{
		using var timeoutSource = new CancellationTokenSource(CommandTimeout);

		using var process = new Process
		{
			StartInfo = CreateStartInfo(signature.RepositoryRootPath)
		};
		try
		{
			if (!process.Start())
				return null;
			process.StandardInput.Close();
			Volatile.Write(ref _gitAvailability, 1);
		}
		catch (Win32Exception)
		{
			Volatile.Write(ref _gitAvailability, -1);
			return null;
		}

		using var cancellationRegistration = timeoutSource.Token.Register(
			static state =>
			{
				var runningProcess = (Process)state!;
				try
				{
					if (!runningProcess.HasExited)
						runningProcess.Kill(entireProcessTree: true);
				}
				catch
				{
					// Cancellation and timeout cleanup are best-effort.
				}
			},
			process);

		var trackedPathsTask = ReadNullDelimitedPathsAsync(
			process.StandardOutput,
			timeoutSource.Token);
		var errorDrainTask = DrainAsync(process.StandardError, timeoutSource.Token);
		await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
		var trackedPaths = await trackedPathsTask.ConfigureAwait(false);
		await errorDrainTask.ConfigureAwait(false);

		if (process.ExitCode != 0)
			return null;

		return new LoadedGitTrackedPathIndex(
			new GitTrackedPathIndex(
				signature.RepositoryRootPath,
				trackedPaths,
				signature.ComparisonSemantics),
			EstimateRetainedBytes(trackedPaths));
	}

	private static long EstimateRetainedBytes(IReadOnlyCollection<string> trackedPaths)
	{
		var estimatedBytes = 64L + ((long)trackedPaths.Count * IntPtr.Size);
		foreach (var path in trackedPaths)
		{
			// Object and alignment overhead varies by runtime; this intentionally rounds up.
			estimatedBytes += 32L + ((long)path.Length * sizeof(char));
		}

		return estimatedBytes;
	}

	internal static ProcessStartInfo CreateStartInfo(string repositoryRootPath)
	{
		var startInfo = GitProcessStartInfoFactory.Create(
			repositoryRootPath,
			[
				"-C", repositoryRootPath,
				"-c", "core.quotepath=false",
				"ls-files", "--cached", "--full-name", "-z", "--"
			]);
		startInfo.StandardOutputEncoding = new UTF8Encoding(
			encoderShouldEmitUTF8Identifier: false,
			throwOnInvalidBytes: false);
		startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
		return startInfo;
	}

	private static async Task<List<string>> ReadNullDelimitedPathsAsync(
		StreamReader reader,
		CancellationToken cancellationToken)
	{
		var paths = new List<string>(capacity: 1024);
		var buffer = ArrayPool<char>.Shared.Rent(4096);
		StringBuilder? spanningPath = null;
		try
		{
			while (true)
			{
				var read = await reader
					.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
					.ConfigureAwait(false);
				if (read == 0)
					break;

				var segmentStart = 0;
				for (var index = 0; index < read; index++)
				{
					if (buffer[index] != '\0')
						continue;

					var segmentLength = index - segmentStart;
					if (spanningPath is null)
					{
						if (segmentLength > 0)
							paths.Add(new string(buffer, segmentStart, segmentLength));
					}
					else
					{
						spanningPath.Append(buffer, segmentStart, segmentLength);
						if (spanningPath.Length > 0)
							paths.Add(spanningPath.ToString());
						spanningPath = null;
					}

					segmentStart = index + 1;
				}

				if (segmentStart < read)
				{
					spanningPath ??= new StringBuilder();
					spanningPath.Append(buffer, segmentStart, read - segmentStart);
				}
			}

			if (spanningPath is { Length: > 0 })
				paths.Add(spanningPath.ToString());
			return paths;
		}
		finally
		{
			ArrayPool<char>.Shared.Return(buffer);
		}
	}

	private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
	{
		var buffer = ArrayPool<char>.Shared.Rent(1024);
		try
		{
			while (await reader
				       .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
				       .ConfigureAwait(false) > 0)
			{
			}
		}
		finally
		{
			ArrayPool<char>.Shared.Return(buffer);
		}
	}

	private static bool TryCreateIndexSignature(
		string repositoryRootPath,
		string gitMetadataPath,
		out GitIndexSignature signature)
	{
		signature = default;
		try
		{
			var normalizedRootPath = PathUtility.Normalize(repositoryRootPath);
			var comparisonSemantics = GitConfigPathComparisonSemanticsResolver.Instance
				.Resolve(normalizedRootPath);
			if (!comparisonSemantics.IsAuthoritative)
				return false;
			if (!TryResolveGitDirectory(normalizedRootPath, gitMetadataPath, out var gitDirectoryPath))
				return false;

			var indexPath = Path.Combine(gitDirectoryPath, "index");
			var indexInfo = new FileInfo(indexPath);
			if (!indexInfo.Exists)
			{
				// A freshly initialized repository has no physical index until the first
				// staged entry. Git still defines it as a valid, readable empty index, and
				// `git ls-files` is the authority for that state. The absent signature is
				// intentionally distinct so a subsequently created index invalidates cache.
				signature = new GitIndexSignature(
					normalizedRootPath,
					PathUtility.Normalize(gitMetadataPath),
					PathUtility.Normalize(indexPath),
					comparisonSemantics,
					HasPhysicalIndex: false,
					LastWriteTicksUtc: 0,
					LengthBytes: 0,
					ContentFingerprint: 0);
				return true;
			}

			for (var attempt = 0; attempt < 2; attempt++)
			{
				indexInfo.Refresh();
				var lastWriteTicksUtc = indexInfo.LastWriteTimeUtc.Ticks;
				var lengthBytes = indexInfo.Length;
				if (!TryReadTailFingerprint(indexInfo.FullName, lengthBytes, out var contentFingerprint))
					return false;

				indexInfo.Refresh();
				if (indexInfo.LastWriteTimeUtc.Ticks != lastWriteTicksUtc ||
				    indexInfo.Length != lengthBytes)
				{
					continue;
				}

				signature = new GitIndexSignature(
					normalizedRootPath,
					PathUtility.Normalize(gitMetadataPath),
					PathUtility.Normalize(indexInfo.FullName),
					comparisonSemantics,
					HasPhysicalIndex: true,
					lastWriteTicksUtc,
					lengthBytes,
					contentFingerprint);
				return true;
			}

			return false;
		}
		catch
		{
			return false;
		}
	}

	internal static GitPathComparisonSemantics ResolvePathComparisonSemantics(string gitMetadataPath)
	{
		var repositoryRoot = Path.GetDirectoryName(PathUtility.Normalize(gitMetadataPath));
		return string.IsNullOrWhiteSpace(repositoryRoot)
			? GitPathComparisonSemantics.PlatformDefault
			: GitConfigPathComparisonSemanticsResolver.Instance.Resolve(repositoryRoot);
	}

	private static bool TryReadTailFingerprint(
		string indexPath,
		long lengthBytes,
		out ulong fingerprint)
	{
		fingerprint = 0;
		if (lengthBytes <= 0)
			return false;

		Span<byte> tail = stackalloc byte[64];
		var bytesToRead = (int)Math.Min(tail.Length, lengthBytes);
		using var handle = File.OpenHandle(
			indexPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete,
			FileOptions.RandomAccess);
		var bytesRead = RandomAccess.Read(
			handle,
			tail[..bytesToRead],
			lengthBytes - bytesToRead);
		if (bytesRead != bytesToRead)
			return false;

		const ulong offsetBasis = 14695981039346656037UL;
		const ulong prime = 1099511628211UL;
		var hash = offsetBasis;
		foreach (var value in tail[..bytesRead])
		{
			hash ^= value;
			hash *= prime;
		}

		fingerprint = hash;
		return true;
	}

	private static bool TryResolveGitDirectory(
		string repositoryRootPath,
		string gitMetadataPath,
		out string gitDirectoryPath)
	{
		gitDirectoryPath = string.Empty;
		var attributes = File.GetAttributes(gitMetadataPath);
		if (attributes.HasFlag(FileAttributes.ReparsePoint))
			return false;

		if (attributes.HasFlag(FileAttributes.Directory))
		{
			gitDirectoryPath = PathUtility.Normalize(gitMetadataPath);
			return true;
		}

		var gitFileInfo = new FileInfo(gitMetadataPath);
		if (!gitFileInfo.Exists || gitFileInfo.Length > GitFileMaximumLength)
			return false;

		using var stream = new FileStream(
			gitMetadataPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete,
			bufferSize: 1024,
			FileOptions.SequentialScan);
		using var reader = new StreamReader(
			stream,
			Encoding.UTF8,
			detectEncodingFromByteOrderMarks: true,
			bufferSize: 1024,
			leaveOpen: false);
		var firstLine = reader.ReadLine();
		const string prefix = "gitdir:";
		if (firstLine is null || !firstLine.StartsWith(prefix, StringComparison.Ordinal))
			return false;

		var target = firstLine[prefix.Length..].Trim();
		if (target.Length == 0)
			return false;

		var resolvedPath = Path.IsPathRooted(target)
			? target
			: Path.Combine(repositoryRootPath, target);
		gitDirectoryPath = PathUtility.Normalize(resolvedPath);
		return Directory.Exists(gitDirectoryPath);
	}

	private static bool TryMetadataEntryExists(string gitMetadataPath)
	{
		try
		{
			_ = File.GetAttributes(gitMetadataPath);
			return true;
		}
		catch (FileNotFoundException)
		{
			return false;
		}
		catch (DirectoryNotFoundException)
		{
			return false;
		}
		catch
		{
			// An inaccessible .git entry still establishes a repository boundary.
			return true;
		}
	}

	private static bool TryGetCached(
		GitIndexSignature signature,
		out GitTrackedPathIndex trackedPathIndex)
	{
		lock (CacheSync)
		{
			if (!Cache.TryGetValue(signature.RepositoryRootPath, out var node))
			{
				trackedPathIndex = null!;
				return false;
			}

			if (!node.Value.Signature.Equals(signature))
			{
				Remove(signature.RepositoryRootPath);
				trackedPathIndex = null!;
				return false;
			}

			CacheLru.Remove(node);
			CacheLru.AddFirst(node);
			trackedPathIndex = node.Value.Index;
			return true;
		}
	}

	private static void Store(GitIndexSignature signature, LoadedGitTrackedPathIndex loaded)
	{
		if (loaded.EstimatedRetainedBytes > MaximumSingleEntryBytes)
			return;

		lock (CacheSync)
		{
			Remove(signature.RepositoryRootPath);
			var entry = new CacheEntry(
				signature,
				loaded.Index,
				loaded.EstimatedRetainedBytes);
			Cache[signature.RepositoryRootPath] = CacheLru.AddFirst(entry);
			_cacheSizeBytes += entry.EstimatedRetainedBytes;

			while ((Cache.Count > CacheEntryLimit || _cacheSizeBytes > CacheByteLimit) &&
			       Cache.Count > 1 &&
			       CacheLru.Last is { } leastRecentlyUsed)
			{
				Remove(leastRecentlyUsed.Value.Signature.RepositoryRootPath);
			}
		}
	}

	private static void Remove(string repositoryRootPath)
	{
		if (!Cache.Remove(repositoryRootPath, out var node))
			return;

		_cacheSizeBytes -= node.Value.EstimatedRetainedBytes;
		CacheLru.Remove(node);
	}

	private readonly record struct GitIndexSignature(
		string RepositoryRootPath,
		string GitMetadataPath,
		string IndexPath,
		GitPathComparisonSemantics ComparisonSemantics,
		bool HasPhysicalIndex,
		long LastWriteTicksUtc,
		long LengthBytes,
		ulong ContentFingerprint)
	{
		public string CreateLoadKey() =>
			$"{RepositoryRootPath}\0{GitMetadataPath}\0{IndexPath}\0" +
			$"{ComparisonSemantics.IgnoreCase}\0{ComparisonSemantics.NormalizeUnicode}\0" +
			$"{ComparisonSemantics.IsAuthoritative}\0" +
			$"{HasPhysicalIndex}\0" +
			$"{LastWriteTicksUtc}\0{LengthBytes}\0{ContentFingerprint}";
	}

	private sealed record LoadedGitTrackedPathIndex(
		GitTrackedPathIndex Index,
		long EstimatedRetainedBytes);

	private sealed record CacheEntry(
		GitIndexSignature Signature,
		GitTrackedPathIndex Index,
		long EstimatedRetainedBytes);
}
