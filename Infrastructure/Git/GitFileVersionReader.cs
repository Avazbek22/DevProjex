using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using DevProjex.Application.Context;
using DevProjex.Application.Diff;
using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Infrastructure.Git;

/// <summary>
/// Reads committed and staged file versions through the registered LocalRead operations only:
/// a revision is resolved to a commit id, entries are listed with literal pathspecs taken from
/// the selection, and object contents are streamed by hex id. Nothing here compares against the
/// working tree, so no clean, smudge, textconv or external-diff program can run.
/// </summary>
public sealed class GitFileVersionReader : IGitFileVersionReader
{
	public const long MaximumBlobBytes = 16L * 1024 * 1024;
	public const long MaximumTotalBlobBytes = 64L * 1024 * 1024;
	private const int MaximumListingBytes = 64 * 1024 * 1024;
	private const int MaximumObjectHeaderBytes = 256;
	private const string RegularFileMode = "100644";
	private const string ExecutableFileMode = "100755";

	public async Task<GitFileVersionSet> ReadAsync(
		GitFileVersionRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectRoot);
		ArgumentNullException.ThrowIfNull(request.Files);
		if (request.Mode is not (GitFilteringMode.Changes or GitFilteringMode.Staged or GitFilteringMode.Diff))
			throw new ArgumentOutOfRangeException(nameof(request), request.Mode, "A changes, staged or diff scope is required.");
		if (request.Mode == GitFilteringMode.Diff && !GitScopeSelection.IsValidDiffRange(request.DiffRange))
			throw new ArgumentException("A diff scope requires a valid range.", nameof(request));

		var results = new Dictionary<string, GitFileVersions>(StringComparer.Ordinal);
		var budget = new BlobBudget(MaximumTotalBlobBytes);
		try
		{
			foreach (var repository in GroupByRepository(request.Files, results, cancellationToken))
			{
				var failure = await ReadRepositoryAsync(
						repository.Key,
						repository.Value,
						request,
						budget,
						results,
						cancellationToken)
					.ConfigureAwait(false);
				if (failure is not null)
					return failure;
			}
		}
		catch (Win32Exception)
		{
			return GitFileVersionSet.Unavailable("Git is not available on PATH.");
		}
		catch (InvalidOperationException exception)
		{
			return GitFileVersionSet.Unavailable(exception.Message);
		}
		return new GitFileVersionSet(results);
	}

	public async Task<GitRevisionAvailability> ProbeRangeAsync(
		string projectRoot,
		string diffRange,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		if (!GitScopeSelection.IsValidDiffRange(diffRange) ||
			!GitTrackedPathIndexCache.TryFindNearestRepositoryBoundary(projectRoot, cancellationToken, out var repositoryRoot))
		{
			return GitRevisionAvailability.Unknown;
		}
		try
		{
			var (left, right) = GitScopeSelection.SplitDiffRange(diffRange);
			var missing = false;
			foreach (var reference in new[] { left, right })
			{
				var resolved = await ResolveCommitAsync(repositoryRoot, reference, cancellationToken).ConfigureAwait(false);
				if (resolved.Failure is { Failure: GitFileVersionFailure.Unavailable })
					return GitRevisionAvailability.Unknown;
				missing |= resolved.ObjectId is null;
			}
			if (!missing)
				return GitRevisionAvailability.Unknown;
			var shallow = await RunListingAsync(
					repositoryRoot,
					GitProcessOperation.ReadShallowRepositoryState(),
					cancellationToken)
				.ConfigureAwait(false);
			return shallow.Succeeded && shallow.Text.Trim() == "true"
				? GitRevisionAvailability.MissingInShallowClone
				: GitRevisionAvailability.Missing;
		}
		catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
		{
			return GitRevisionAvailability.Unknown;
		}
	}

	private static SortedDictionary<string, List<RepositoryFile>> GroupByRepository(
		IReadOnlyList<string> files,
		Dictionary<string, GitFileVersions> results,
		CancellationToken cancellationToken)
	{
		var repositories = new SortedDictionary<string, List<RepositoryFile>>(StringComparer.Ordinal);
		foreach (var file in files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var directory = Path.GetDirectoryName(file);
			if (string.IsNullOrEmpty(directory) ||
				!GitTrackedPathIndexCache.TryFindNearestRepositoryBoundary(directory, cancellationToken, out var repositoryRoot))
			{
				results[file] = new GitFileVersions(null, null, Unsupported: true);
				continue;
			}
			// Only Windows separates directories with '\'; elsewhere it is part of a file name, which
			// the safe-path check refuses, so such a file is reported unsupported instead of being
			// read as the '/'-separated path of another file.
			var relativePath = Path.GetRelativePath(repositoryRoot, file);
			var gitPath = OperatingSystem.IsWindows() ? relativePath.Replace('\\', '/') : relativePath;
			if (!GitProcessOperation.IsSafeRepositoryPath(gitPath) || Path.IsPathRooted(gitPath))
			{
				results[file] = new GitFileVersions(null, null, Unsupported: true);
				continue;
			}
			if (!repositories.TryGetValue(repositoryRoot, out var members))
			{
				members = [];
				repositories.Add(repositoryRoot, members);
			}
			members.Add(new RepositoryFile(file, gitPath));
		}
		return repositories;
	}

	private static async Task<GitFileVersionSet?> ReadRepositoryAsync(
		string repositoryRoot,
		IReadOnlyList<RepositoryFile> files,
		GitFileVersionRequest request,
		BlobBudget budget,
		Dictionary<string, GitFileVersions> results,
		CancellationToken cancellationToken)
	{
		var safety = await GitRepositorySafetyInspector.InspectAsync(repositoryRoot, cancellationToken)
			.ConfigureAwait(false);
		if (!safety.IsComplete)
			return GitFileVersionSet.Unavailable("Git safety configuration could not be inspected.");
		if (safety.OldGitPromisorRepository)
			return GitFileVersionSet.Unavailable("This partial clone requires Git 2.45 or newer for a no-network local read.");

		var (oldReference, newReference) = request.Mode == GitFilteringMode.Diff
			? GitScopeSelection.SplitDiffRange(request.DiffRange!)
			: ("HEAD", (string?)null);
		var oldCommit = await ResolveCommitAsync(repositoryRoot, oldReference, cancellationToken).ConfigureAwait(false);
		if (oldCommit.Failure is { } oldFailure)
			return oldFailure;
		if (oldCommit.ObjectId is null && request.Mode == GitFilteringMode.Diff)
			return GitFileVersionSet.UnknownRevision("Git could not resolve the old revision of the range.");
		string? newCommit = null;
		if (newReference is not null)
		{
			var resolved = await ResolveCommitAsync(repositoryRoot, newReference, cancellationToken).ConfigureAwait(false);
			if (resolved.Failure is { } newFailure)
				return newFailure;
			newCommit = resolved.ObjectId;
			if (newCommit is null)
				return GitFileVersionSet.UnknownRevision("Git could not resolve the new revision of the range.");
		}

		var gitPaths = files.Select(static file => file.GitPath).ToArray();
		// An unborn branch has no commit: every old side is absent.
		var oldEntries = oldCommit.ObjectId is null
			? new Dictionary<string, StoredEntry>(StringComparer.Ordinal)
			: await ReadTreeEntriesAsync(repositoryRoot, oldCommit.ObjectId, gitPaths, cancellationToken).ConfigureAwait(false);
		if (oldEntries is null)
			return GitFileVersionSet.Unavailable("Git returned an invalid tree listing.");

		Dictionary<string, StoredEntry>? newEntries = null;
		HashSet<string> conflicted = new(StringComparer.Ordinal);
		IReadOnlyDictionary<string, string> renames = new Dictionary<string, string>(StringComparer.Ordinal);
		if (request.Mode == GitFilteringMode.Staged)
		{
			newEntries = await ReadIndexEntriesAsync(repositoryRoot, gitPaths, conflicted, cancellationToken).ConfigureAwait(false);
			if (newEntries is null)
				return GitFileVersionSet.Unavailable("Git returned an invalid index listing.");
			renames = await ReadRenamesAsync(repositoryRoot, GitProcessOperation.ReadStagedChanges(), cancellationToken)
				.ConfigureAwait(false) ?? renames;
		}
		else if (request.Mode == GitFilteringMode.Diff)
		{
			newEntries = await ReadTreeEntriesAsync(repositoryRoot, newCommit!, gitPaths, cancellationToken).ConfigureAwait(false);
			if (newEntries is null)
				return GitFileVersionSet.Unavailable("Git returned an invalid tree listing.");
			renames = await ReadRenamesAsync(
					repositoryRoot,
					GitProcessOperation.ReadRefDiff(oldCommit.ObjectId + ".." + newCommit),
					cancellationToken)
				.ConfigureAwait(false) ?? renames;
		}

		var requestedByGitPath = new Dictionary<string, RepositoryFile>(files.Count, StringComparer.Ordinal);
		foreach (var file in files)
			requestedByGitPath.TryAdd(file.GitPath, file);
		var plans = new List<FilePlan>(files.Count);
		foreach (var file in files)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string? renamedFrom = null;
			var oldPath = file.GitPath;
			if (!oldEntries.ContainsKey(file.GitPath) &&
				renames.TryGetValue(file.GitPath, out var source) &&
				requestedByGitPath.TryGetValue(source, out var sourceFile))
			{
				oldPath = source;
				renamedFrom = sourceFile.FullPath;
			}
			plans.Add(new FilePlan(
				file,
				oldEntries.GetValueOrDefault(oldPath),
				newEntries?.GetValueOrDefault(file.GitPath),
				renamedFrom,
				conflicted.Contains(file.GitPath)));
		}

		var read = await ReadContentsAsync(repositoryRoot, plans, budget, cancellationToken).ConfigureAwait(false);
		if (read is null)
			return GitFileVersionSet.Unavailable("Git object contents could not be read.");
		var (contents, missingObjects) = read.Value;
		foreach (var plan in plans)
		{
			if (plan.Conflicted || IsMissing(plan.Old, missingObjects) || IsMissing(plan.New, missingObjects))
			{
				// The repository no longer has the object this entry names: the loose object file is
				// gone even though the tree or index still records it. Only this file is affected; the
				// rest of the selection is still read and shown.
				results[plan.File.FullPath] = new GitFileVersions(null, null, Unsupported: true);
				continue;
			}
			results[plan.File.FullPath] = new GitFileVersions(
				ToStoredFile(plan.Old, contents),
				ToStoredFile(plan.New, contents),
				plan.RenamedFrom);
		}
		return null;
	}

	private static bool IsMissing(StoredEntry? entry, IReadOnlySet<string> missingObjects) =>
		entry is { IsRegularFile: true } && missingObjects.Contains(entry.ObjectId);

	private static GitStoredFile? ToStoredFile(StoredEntry? entry, IReadOnlyDictionary<string, ObjectContent> contents)
	{
		if (entry is null)
			return null;
		if (!entry.IsRegularFile)
			return new GitStoredFile(GitStoredFileKind.NotRegularFile, 0, null);
		if (!contents.TryGetValue(entry.ObjectId, out var content) || content.Bytes is null)
			return new GitStoredFile(GitStoredFileKind.TooLarge, content?.Size ?? 0, null);
		return new GitStoredFile(GitStoredFileKind.Regular, content.Size, content.Bytes);
	}

	private static async Task<CommitResolution> ResolveCommitAsync(
		string repositoryRoot,
		string reference,
		CancellationToken cancellationToken)
	{
		GitProcessOperation operation;
		try
		{
			operation = GitProcessOperation.ResolveCommit(reference);
		}
		catch (ArgumentException)
		{
			return new CommitResolution(null, GitFileVersionSet.UnknownRevision("The revision is invalid."));
		}
		var result = await RunListingAsync(repositoryRoot, operation, cancellationToken).ConfigureAwait(false);
		if (!result.Started)
			return new CommitResolution(null, GitFileVersionSet.Unavailable(result.FailureReason!));
		var output = result.Text.TrimEnd('\r', '\n');
		if (result.ExitCode == 0 && GitProcessOperation.IsObjectId(output))
			return new CommitResolution(output, null);
		// --verify --quiet exits 1 with no output for a name that does not resolve, which for HEAD
		// means an unborn branch.
		return result.ExitCode == 1 && output.Length == 0
			? new CommitResolution(null, null)
			: new CommitResolution(null, GitFileVersionSet.Unavailable("Git could not resolve a revision."));
	}

	private static async Task<Dictionary<string, StoredEntry>?> ReadTreeEntriesAsync(
		string repositoryRoot,
		string commit,
		IReadOnlyList<string> gitPaths,
		CancellationToken cancellationToken)
	{
		var entries = new Dictionary<string, StoredEntry>(StringComparer.Ordinal);
		foreach (var chunk in ChunkPaths(gitPaths))
		{
			var result = await RunListingAsync(
					repositoryRoot,
					GitProcessOperation.ReadTreeEntries(commit, chunk),
					cancellationToken)
				.ConfigureAwait(false);
			if (!result.Succeeded)
				return null;
			var requested = chunk.ToHashSet(StringComparer.Ordinal);
			foreach (var record in SplitRecords(result.Text))
			{
				// <mode> SP <type> SP <object> TAB <path>
				var tab = record.IndexOf('\t');
				if (tab < 0)
					return null;
				var fields = record[..tab].Split(' ');
				var path = record[(tab + 1)..];
				if (fields.Length != 3 || !GitProcessOperation.IsObjectId(fields[2]))
					return null;
				if (requested.Contains(path))
				{
					entries[path] = new StoredEntry(
						fields[2],
						fields[1] == "blob" && fields[0] is RegularFileMode or ExecutableFileMode);
				}
			}
		}
		return entries;
	}

	private static async Task<Dictionary<string, StoredEntry>?> ReadIndexEntriesAsync(
		string repositoryRoot,
		IReadOnlyList<string> gitPaths,
		ISet<string> conflicted,
		CancellationToken cancellationToken)
	{
		var entries = new Dictionary<string, StoredEntry>(StringComparer.Ordinal);
		foreach (var chunk in ChunkPaths(gitPaths))
		{
			var result = await RunListingAsync(
					repositoryRoot,
					GitProcessOperation.ReadIndexEntries(chunk),
					cancellationToken)
				.ConfigureAwait(false);
			if (!result.Succeeded)
				return null;
			var requested = chunk.ToHashSet(StringComparer.Ordinal);
			foreach (var record in SplitRecords(result.Text))
			{
				// <mode> SP <object> SP <stage> TAB <path>
				var tab = record.IndexOf('\t');
				if (tab < 0)
					return null;
				var fields = record[..tab].Split(' ');
				var path = record[(tab + 1)..];
				if (fields.Length != 3 || !GitProcessOperation.IsObjectId(fields[1]))
					return null;
				if (!requested.Contains(path))
					continue;
				if (fields[2] != "0")
				{
					conflicted.Add(path);
					continue;
				}
				entries[path] = new StoredEntry(fields[1], fields[0] is RegularFileMode or ExecutableFileMode);
			}
		}
		return entries;
	}

	/// <summary>Maps each rename destination to its source; a listing that cannot be read yields none.</summary>
	private static async Task<IReadOnlyDictionary<string, string>?> ReadRenamesAsync(
		string repositoryRoot,
		GitProcessOperation operation,
		CancellationToken cancellationToken)
	{
		var result = await RunListingAsync(repositoryRoot, operation, cancellationToken).ConfigureAwait(false);
		if (!result.Succeeded)
			return null;
		var values = result.Text.Split('\0');
		var renames = new Dictionary<string, string>(StringComparer.Ordinal);
		for (var index = 0; index < values.Length;)
		{
			var status = values[index++];
			if (status.Length == 0)
				continue;
			if (index >= values.Length)
				return null;
			var first = values[index++];
			if (status[0] is not ('R' or 'C'))
				continue;
			if (index >= values.Length)
				return null;
			var destination = values[index++];
			if (status[0] == 'R')
				renames[destination] = first;
		}
		return renames;
	}

	private static async Task<(Dictionary<string, ObjectContent> Contents, HashSet<string> Missing)?> ReadContentsAsync(
		string repositoryRoot,
		IReadOnlyList<FilePlan> plans,
		BlobBudget budget,
		CancellationToken cancellationToken)
	{
		var wanted = new List<string>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var plan in plans)
		{
			if (plan.Conflicted)
				continue;
			foreach (var entry in new[] { plan.Old, plan.New })
			{
				if (entry is { IsRegularFile: true } && seen.Add(entry.ObjectId))
					wanted.Add(entry.ObjectId);
			}
		}
		var contents = new Dictionary<string, ObjectContent>(StringComparer.Ordinal);
		if (wanted.Count == 0)
			return (contents, new HashSet<string>(StringComparer.Ordinal));

		var headers = await ReadObjectSizesAsync(repositoryRoot, wanted, cancellationToken).ConfigureAwait(false);
		if (headers is null)
			return null;
		var (sizes, missing) = headers.Value;
		var readable = new List<string>(wanted.Count);
		foreach (var objectId in wanted)
		{
			if (missing.Contains(objectId))
				continue;
			var size = sizes[objectId];
			if (size <= MaximumBlobBytes && budget.TryReserve(size))
				readable.Add(objectId);
			else
				contents[objectId] = new ObjectContent(size, null);
		}
		if (readable.Count == 0)
			return (contents, missing);
		var result = await ReadObjectBytesAsync(repositoryRoot, readable, sizes, contents, cancellationToken)
			.ConfigureAwait(false);
		return result is null ? null : (result, missing);
	}

	/// <summary>
	/// Reports each object's size, or that it is missing from the object store: the tree or index
	/// still names it, but its loose object file is gone. A missing object fails only the file(s)
	/// that name it, not the rest of the batch.
	/// </summary>
	private static async Task<(Dictionary<string, long> Sizes, HashSet<string> Missing)?> ReadObjectSizesAsync(
		string repositoryRoot,
		IReadOnlyList<string> objectIds,
		CancellationToken cancellationToken)
	{
		var result = await RunAsync(
				repositoryRoot,
				GitProcessOperation.ReadObjectHeaders(),
				objectIds,
				(stream, token) => ReadBoundedTextAsync(stream, MaximumListingBytes, token),
				cancellationToken)
			.ConfigureAwait(false);
		if (!result.Succeeded || result.Value is null)
			return null;
		var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
		var missing = new HashSet<string>(StringComparer.Ordinal);
		foreach (var line in result.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			// <object> SP <type> SP <size>; a missing object is "<object> missing".
			var fields = line.Split(' ');
			if (fields.Length == 2 && fields[1] == "missing" && GitProcessOperation.IsObjectId(fields[0]))
			{
				missing.Add(fields[0]);
				continue;
			}
			if (fields.Length != 3 || fields[1] != "blob" ||
				!long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
			{
				return null;
			}
			sizes[fields[0]] = size;
		}
		return objectIds.All(objectId => sizes.ContainsKey(objectId) || missing.Contains(objectId))
			? (sizes, missing)
			: null;
	}

	private static async Task<Dictionary<string, ObjectContent>?> ReadObjectBytesAsync(
		string repositoryRoot,
		IReadOnlyList<string> objectIds,
		IReadOnlyDictionary<string, long> sizes,
		Dictionary<string, ObjectContent> contents,
		CancellationToken cancellationToken)
	{
		var result = await RunAsync(
				repositoryRoot,
				GitProcessOperation.ReadObjectContents(),
				objectIds,
				async (stream, token) =>
				{
					var reader = new ObjectStreamReader(stream);
					foreach (var objectId in objectIds)
					{
						var header = await reader.ReadLineAsync(MaximumObjectHeaderBytes, token).ConfigureAwait(false);
						var fields = header?.Split(' ');
						if (fields is not { Length: 3 } || fields[0] != objectId || fields[1] != "blob" ||
							!long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
							size != sizes[objectId])
						{
							return false;
						}
						var bytes = new byte[size];
						await reader.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
						if (!await reader.ReadNewlineAsync(token).ConfigureAwait(false))
							return false;
						contents[objectId] = new ObjectContent(size, bytes);
					}
					return true;
				},
				cancellationToken)
			.ConfigureAwait(false);
		return result is { Succeeded: true, Value: true } ? contents : null;
	}

	private static IEnumerable<IReadOnlyList<string>> ChunkPaths(IReadOnlyList<string> gitPaths)
	{
		var chunk = new List<string>();
		var characters = 0;
		foreach (var path in gitPaths)
		{
			if (chunk.Count > 0 && characters + path.Length + 1 > GitProcessOperation.MaximumPathArgumentCharacters)
			{
				yield return chunk;
				chunk = [];
				characters = 0;
			}
			chunk.Add(path);
			characters += path.Length + 1;
		}
		if (chunk.Count > 0)
			yield return chunk;
	}

	private static IEnumerable<string> SplitRecords(string text) =>
		text.Split('\0', StringSplitOptions.RemoveEmptyEntries);

	private static async Task<ProcessResult<string>> RunListingAsync(
		string repositoryRoot,
		GitProcessOperation operation,
		CancellationToken cancellationToken)
	{
		var result = await RunAsync(
				repositoryRoot,
				operation,
				input: null,
				(stream, token) => ReadBoundedTextAsync(stream, MaximumListingBytes, token),
				cancellationToken)
			.ConfigureAwait(false);
		return result with { Value = result.Value ?? string.Empty };
	}

	private static async Task<string> ReadBoundedTextAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
	{
		using var buffer = new MemoryStream();
		var chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
		try
		{
			while (true)
			{
				var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
				if (read == 0)
					break;
				if (buffer.Length + read > maximumBytes)
					throw new IOException("Git output exceeded the supported limit.");
				buffer.Write(chunk, 0, read);
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(chunk);
		}
		return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
			.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
	}

	/// <summary>
	/// Runs one registered operation. Object ids are written to standard input while the output is
	/// read, so neither pipe can fill and stall the other; the operation deadline bounds both.
	/// </summary>
	private static async Task<ProcessResult<T>> RunAsync<T>(
		string repositoryRoot,
		GitProcessOperation operation,
		IReadOnlyList<string>? input,
		Func<Stream, CancellationToken, Task<T>> readOutput,
		CancellationToken cancellationToken)
	{
		if (input is not null)
		{
			foreach (var objectId in input)
				GitProcessOperation.ValidateObjectId(objectId);
		}
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(operation.Deadline);
		using var process = new Process
		{
			StartInfo = GitProcessStartInfoFactory.Create(repositoryRoot, operation)
		};
		try
		{
			if (!process.Start())
				return ProcessResult<T>.NotStarted("Git could not be started.");
		}
		catch (Win32Exception)
		{
			return ProcessResult<T>.NotStarted("Git is not available on PATH.");
		}

		var inputTask = WriteInputAsync(process, input, deadline.Token);
		var outputTask = readOutput(process.StandardOutput.BaseStream, deadline.Token);
		var errorTask = GitProcessOutputReader.ReadAsync(
			process.StandardError,
			GitProcessOutputReader.MaximumOutputCharacters,
			deadline.Token);
		try
		{
			var value = await outputTask.ConfigureAwait(false);
			await inputTask.ConfigureAwait(false);
			await GitRepositoryService.WaitForExitOrTerminateAsync(process, deadline.Token).ConfigureAwait(false);
			if (!await GitProcessOutputReader
					.WaitForCompletionAfterExitAsync(process, errorTask)
					.ConfigureAwait(false))
			{
				return ProcessResult<T>.NotStarted("Git output could not be read.");
			}
			return new ProcessResult<T>(true, process.ExitCode, value, null);
		}
		catch (Exception exception) when (exception is OperationCanceledException or IOException)
		{
			TryTerminate(process);
			await GitProcessOutputReader
				.ObserveAfterTerminationAsync(process, inputTask, outputTask, errorTask)
				.ConfigureAwait(false);
			if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
				throw;
			return ProcessResult<T>.NotStarted(exception is OperationCanceledException
				? "Git object reads exceeded their safety deadline."
				: "Git output could not be read.");
		}
	}

	private static async Task WriteInputAsync(Process process, IReadOnlyList<string>? input, CancellationToken cancellationToken)
	{
		try
		{
			if (input is null)
				return;
			var stream = process.StandardInput.BaseStream;
			foreach (var objectId in input)
			{
				await stream.WriteAsync(Encoding.ASCII.GetBytes(objectId + "\n"), cancellationToken).ConfigureAwait(false);
			}
			await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			try
			{
				process.StandardInput.Close();
			}
			catch (IOException)
			{
				// The process exited before reading all input; the output check reports it.
			}
		}
	}

	private static void TryTerminate(Process process)
	{
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or Win32Exception)
		{
		}
	}

	private readonly record struct RepositoryFile(string FullPath, string GitPath);

	private sealed record StoredEntry(string ObjectId, bool IsRegularFile);

	private sealed record FilePlan(
		RepositoryFile File,
		StoredEntry? Old,
		StoredEntry? New,
		string? RenamedFrom,
		bool Conflicted);

	private sealed record ObjectContent(long Size, byte[]? Bytes);

	private readonly record struct CommitResolution(string? ObjectId, GitFileVersionSet? Failure);

	private sealed record ProcessResult<T>(bool Started, int ExitCode, T? Value, string? FailureReason)
	{
		public bool Succeeded => Started && ExitCode == 0;

		public string Text => Value as string ?? string.Empty;

		public static ProcessResult<T> NotStarted(string reason) => new(false, -1, default, reason);
	}

	private sealed class BlobBudget(long maximumBytes)
	{
		private long remaining = maximumBytes;

		public bool TryReserve(long bytes)
		{
			if (bytes > remaining)
				return false;
			remaining -= bytes;
			return true;
		}
	}

	/// <summary>Reads the framed output of <c>cat-file --batch</c> without decoding object bytes.</summary>
	private sealed class ObjectStreamReader(Stream stream)
	{
		private readonly byte[] buffer = new byte[64 * 1024];
		private int offset;
		private int count;

		public async ValueTask<string?> ReadLineAsync(int maximumBytes, CancellationToken cancellationToken)
		{
			var line = new List<byte>(96);
			while (true)
			{
				if (offset == count && !await FillAsync(cancellationToken).ConfigureAwait(false))
					return null;
				var value = buffer[offset++];
				if (value == (byte)'\n')
					return Encoding.ASCII.GetString(line.ToArray());
				if (line.Count == maximumBytes)
					return null;
				line.Add(value);
			}
		}

		public async ValueTask ReadExactlyAsync(byte[] destination, CancellationToken cancellationToken)
		{
			var written = 0;
			while (written < destination.Length)
			{
				if (offset == count && !await FillAsync(cancellationToken).ConfigureAwait(false))
					throw new IOException("Git object output ended early.");
				var available = Math.Min(count - offset, destination.Length - written);
				Buffer.BlockCopy(buffer, offset, destination, written, available);
				offset += available;
				written += available;
			}
		}

		public async ValueTask<bool> ReadNewlineAsync(CancellationToken cancellationToken)
		{
			if (offset == count && !await FillAsync(cancellationToken).ConfigureAwait(false))
				return false;
			return buffer[offset++] == (byte)'\n';
		}

		private async ValueTask<bool> FillAsync(CancellationToken cancellationToken)
		{
			count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
			offset = 0;
			return count > 0;
		}
	}
}
