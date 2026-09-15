using System.Collections.Concurrent;
using System.ComponentModel;
using DevProjex.Application.Ranking;
using DevProjex.Infrastructure.Processes;

namespace DevProjex.Infrastructure.Git;

public sealed class ProjectGitHistoryReader : IProjectGitHistoryReader
{
	public const int CommitWindow = 200;
	private const int MaximumOutputCharacters = 8 * 1024 * 1024;
	private const int MaximumCachedRepositories = 16;
	private static readonly ConcurrentDictionary<HistoryCacheKey, RepositoryHistory> HistoryCache = new();
	private static readonly ConcurrentQueue<HistoryCacheKey> HistoryCacheOrder = new();

	public async Task<ProjectGitHistorySnapshot> ReadAsync(
		string sourceRoot,
		IReadOnlyList<string> candidateFiles,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
		ArgumentNullException.ThrowIfNull(candidateFiles);
		var root = Path.GetFullPath(sourceRoot);
		var repositoryRoot = FindOwningRepository(root);
		if (repositoryRoot is null)
			return Unavailable(ProjectGitHistoryUnavailableReason.NotRepository);

		GitRepositorySafetyInspection safety;
		try
		{
			safety = await GitRepositorySafetyInspector
				.InspectAsync(repositoryRoot, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (Win32Exception)
		{
			return Unavailable(ProjectGitHistoryUnavailableReason.GitUnavailable);
		}
		if (!safety.IsComplete)
			return Unavailable(ProjectGitHistoryUnavailableReason.ProcessFailed);
		if (safety.OldGitPromisorRepository)
			return Unavailable(ProjectGitHistoryUnavailableReason.OldGitPromisorRepository);

		var headResult = await RunLocalReadAsync(
			repositoryRoot,
			GitProcessOperation.ResolveCommit("HEAD"),
			maximumOutputCharacters: 128,
			cancellationToken).ConfigureAwait(false);
		if (!headResult.IsSuccess)
			return Unavailable(headResult.FailureReason, headResult.Detail);
		var head = headResult.Output.TrimEnd('\r', '\n');
		if (!IsObjectId(head))
			return Unavailable(ProjectGitHistoryUnavailableReason.InvalidOutput);

		var shallowResult = await RunLocalReadAsync(
			repositoryRoot,
			GitProcessOperation.ReadShallowRepositoryState(),
			maximumOutputCharacters: 64,
			cancellationToken).ConfigureAwait(false);
		if (!shallowResult.IsSuccess)
			return Unavailable(shallowResult.FailureReason, shallowResult.Detail);
		var shallowText = shallowResult.Output.TrimEnd('\r', '\n');
		if (!bool.TryParse(shallowText, out var isShallow))
			return Unavailable(ProjectGitHistoryUnavailableReason.InvalidOutput);

		var completeCacheKey = new HistoryCacheKey(repositoryRoot, head, CommitWindow, isShallow, IsComplete: true);
		if (!HistoryCache.TryGetValue(completeCacheKey, out var history))
		{
			var historyResult = await RunLocalReadAsync(
				repositoryRoot,
				GitProcessOperation.ReadHistoryWindow(),
				MaximumOutputCharacters,
				cancellationToken).ConfigureAwait(false);
			if (!historyResult.IsSuccess)
				return Unavailable(historyResult.FailureReason, historyResult.Detail);
			history = ParseRepositoryHistory(historyResult.Output, isShallow);
			if (history is null)
				return Unavailable(ProjectGitHistoryUnavailableReason.InvalidOutput);
			if (history.IsComplete)
				StoreHistory(completeCacheKey, history);
		}

		return SelectCandidates(repositoryRoot, candidateFiles, history);
	}

	internal static ProjectGitHistorySnapshot Parse(
		string repositoryRoot,
		IReadOnlyList<string> candidateFiles,
		string output,
		bool isShallow = false)
	{
		var history = ParseRepositoryHistory(output, isShallow);
		return history is null
			? Unavailable(ProjectGitHistoryUnavailableReason.InvalidOutput)
			: SelectCandidates(repositoryRoot, candidateFiles, history);
	}

	private static RepositoryHistory? ParseRepositoryHistory(string output, bool isShallow)
	{
		var counts = new Dictionary<string, MutableActivity>(StringComparer.Ordinal);
		var commitPosition = 0;
		HashSet<string>? changedInCommit = null;
		var firstPathField = false;
		foreach (var field in output.Split('\0', StringSplitOptions.None))
		{
			if (TryReadRecordHeader(field, out _))
			{
				ApplyCommit(changedInCommit, counts, commitPosition);
				commitPosition++;
				changedInCommit = new HashSet<string>(StringComparer.Ordinal);
				firstPathField = true;
				continue;
			}
			if (field.Length == 0)
				continue;
			if (changedInCommit is null)
				return null;
			// Git inserts one LF between the pretty-format header and the first -z path.
			// Remove that framing byte exactly once; every byte belonging to the path remains intact.
			var path = firstPathField && field[0] == '\n' ? field[1..] : field;
			firstPathField = false;
			if (path.Length > 0)
				changedInCommit.Add(path);
		}
		ApplyCommit(changedInCommit, counts, commitPosition);

		var activities = counts.ToDictionary(
			static pair => pair.Key,
			static pair => new ProjectGitFileActivity(
				pair.Value.CommitCount,
				pair.Value.MostRecentCommitPosition),
			StringComparer.Ordinal);
		return new RepositoryHistory(
			commitPosition,
			activities,
			isShallow,
			!isShallow || commitPosition >= CommitWindow);
	}

	private static ProjectGitHistorySnapshot SelectCandidates(
		string repositoryRoot,
		IReadOnlyList<string> candidateFiles,
		RepositoryHistory history)
	{
		var root = Path.GetFullPath(repositoryRoot);
		var boundaryIndex = new Dictionary<string, string?>(PathComparer.Default)
		{
			[root] = root
		};
		var files = new Dictionary<string, ProjectGitFileActivity>(PathComparer.Default);
		var unavailable = new Dictionary<string, ProjectGitHistoryUnavailableReason>(PathComparer.Default);
		foreach (var candidate in candidateFiles)
		{
			var fullPath = Path.GetFullPath(candidate);
			var directory = Path.GetDirectoryName(fullPath) ?? fullPath;
			var owner = FindOwningRepository(directory, boundaryIndex);
			if (owner is null || !PathComparer.Default.Equals(owner, root))
			{
				unavailable[fullPath] = ProjectGitHistoryUnavailableReason.NestedRepository;
				continue;
			}
			var relative = PortableRelative(root, fullPath);
			files[fullPath] = history.Files.TryGetValue(relative, out var activity)
				? activity
				: new ProjectGitFileActivity(0, 0);
		}

		return new ProjectGitHistorySnapshot(
			CommitWindow,
			history.CommitCount,
			files,
			unavailable)
		{
			IsShallow = history.IsShallow,
			IsComplete = history.IsComplete
		};
	}

	private static bool TryReadRecordHeader(string field, out string hash)
	{
		hash = string.Empty;
		if (field.Length is not (41 or 65) || field[0] != '\x1e')
			return false;
		var candidate = field.AsSpan(1);
		if (!IsObjectId(candidate))
			return false;
		hash = candidate.ToString();
		return true;
	}

	private static bool IsObjectId(string value) => IsObjectId(value.AsSpan());

	private static bool IsObjectId(ReadOnlySpan<char> value)
	{
		if (value.Length is not (40 or 64))
			return false;
		foreach (var character in value)
		{
			if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f') and not (>= 'A' and <= 'F'))
				return false;
		}
		return true;
	}

	private static void ApplyCommit(
		HashSet<string>? changedPaths,
		IDictionary<string, MutableActivity> counts,
		int commitPosition)
	{
		if (changedPaths is null)
			return;
		foreach (var path in changedPaths)
		{
			if (!counts.TryGetValue(path, out var activity))
			{
				activity = new MutableActivity();
				counts[path] = activity;
			}
			activity.CommitCount++;
			if (activity.MostRecentCommitPosition == 0)
				activity.MostRecentCommitPosition = commitPosition;
		}
	}

	private static async Task<LocalReadResult> RunLocalReadAsync(
		string repositoryRoot,
		GitProcessOperation operation,
		int maximumOutputCharacters,
		CancellationToken cancellationToken)
	{
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(operation.Deadline);
		using var process = new Process
		{
			StartInfo = GitProcessStartInfoFactory.Create(repositoryRoot, operation)
		};
		try
		{
			if (!process.Start())
				return LocalReadResult.Failed(ProjectGitHistoryUnavailableReason.ProcessFailed);
			process.StandardInput.Close();
		}
		catch (Win32Exception)
		{
			return LocalReadResult.Failed(ProjectGitHistoryUnavailableReason.GitUnavailable);
		}
		catch (InvalidOperationException exception)
		{
			return LocalReadResult.Failed(ProjectGitHistoryUnavailableReason.ProcessFailed, exception.Message);
		}

		var outputTask = GitProcessOutputReader.ReadAsync(
			process.StandardOutput,
			maximumOutputCharacters,
			deadline.Token);
		var errorTask = GitProcessOutputReader.ReadAsync(
			process.StandardError,
			GitProcessOutputReader.MaximumOutputCharacters,
			deadline.Token);
		try
		{
			await GitRepositoryService.WaitForExitOrTerminateAsync(process, deadline.Token)
				.ConfigureAwait(false);
			if (!await GitProcessOutputReader
				    .WaitForCompletionAfterExitAsync(process, outputTask, errorTask)
				    .ConfigureAwait(false))
			{
				return LocalReadResult.Failed(ProjectGitHistoryUnavailableReason.ProcessFailed);
			}
			var output = await outputTask.ConfigureAwait(false);
			var error = await errorTask.ConfigureAwait(false);
			if (output.ExceededLimit || error.ExceededLimit)
				return LocalReadResult.Failed(ProjectGitHistoryUnavailableReason.OutputLimitExceeded);
			return process.ExitCode == 0
				? LocalReadResult.Succeeded(output.Text)
				: LocalReadResult.Failed(
					ProjectGitHistoryUnavailableReason.ProcessFailed,
					FirstLine(error.Text));
		}
		catch (OperationCanceledException)
		{
			await GitProcessOutputReader
				.ObserveAfterTerminationAsync(process, outputTask, errorTask)
				.ConfigureAwait(false);
			if (cancellationToken.IsCancellationRequested)
				throw;
			return LocalReadResult.Failed(
				ProjectGitHistoryUnavailableReason.ProcessFailed,
				"Git history read exceeded its safety deadline.");
		}
	}

	private static void StoreHistory(HistoryCacheKey key, RepositoryHistory history)
	{
		if (!HistoryCache.TryAdd(key, history))
			return;
		HistoryCacheOrder.Enqueue(key);
		while (HistoryCache.Count > MaximumCachedRepositories && HistoryCacheOrder.TryDequeue(out var oldest))
			HistoryCache.TryRemove(oldest, out _);
	}

	private static ProjectGitHistorySnapshot Unavailable(
		ProjectGitHistoryUnavailableReason reason,
		string? detail = null) =>
		new(CommitWindow, 0,
			new Dictionary<string, ProjectGitFileActivity>(StringComparer.Ordinal),
			new Dictionary<string, ProjectGitHistoryUnavailableReason>(StringComparer.Ordinal),
			reason,
			detail);

	private static string? FindOwningRepository(string startPath) =>
		FindOwningRepository(startPath, new Dictionary<string, string?>(PathComparer.Default));

	private static string? FindOwningRepository(
		string startPath,
		IDictionary<string, string?> boundaryIndex)
	{
		var visited = new List<string>();
		string? owner = null;
		try
		{
			var current = new DirectoryInfo(Path.GetFullPath(startPath));
			while (current is not null)
			{
				if (boundaryIndex.TryGetValue(current.FullName, out owner))
					break;
				visited.Add(current.FullName);
				var marker = Path.Combine(current.FullName, ".git");
				if (Directory.Exists(marker) || File.Exists(marker))
				{
					owner = current.FullName;
					break;
				}
				current = current.Parent;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			owner = null;
		}
		foreach (var directory in visited)
			boundaryIndex[directory] = owner;
		return owner;
	}

	private static string PortableRelative(string root, string path) =>
		Path.GetRelativePath(root, path).Replace('\\', '/');

	private static string? FirstLine(string value) =>
		value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

	private sealed class MutableActivity
	{
		public int CommitCount { get; set; }
		public int MostRecentCommitPosition { get; set; }
	}

	private sealed record RepositoryHistory(
		int CommitCount,
		IReadOnlyDictionary<string, ProjectGitFileActivity> Files,
		bool IsShallow,
		bool IsComplete);

	private readonly record struct HistoryCacheKey(
		string RepositoryRoot,
		string Head,
		int Window,
		bool IsShallow,
		bool IsComplete);

	private readonly record struct LocalReadResult(
		bool IsSuccess,
		string Output,
		ProjectGitHistoryUnavailableReason FailureReason,
		string? Detail)
	{
		public static LocalReadResult Succeeded(string output) =>
			new(true, output, ProjectGitHistoryUnavailableReason.None, null);

		public static LocalReadResult Failed(
			ProjectGitHistoryUnavailableReason reason,
			string? detail = null) =>
			new(false, string.Empty, reason, detail);
	}
}
