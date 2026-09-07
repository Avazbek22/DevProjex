using System.ComponentModel;
using DevProjex.Application.Ranking;
using DevProjex.Infrastructure.Processes;

namespace DevProjex.Infrastructure.Git;

public sealed class ProjectGitHistoryReader : IProjectGitHistoryReader
{
	public const int CommitWindow = 200;
	private const int MaximumOutputCharacters = 8 * 1024 * 1024;

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

		var historyResult = await RunLocalReadAsync(
			repositoryRoot,
			GitProcessOperation.ReadHistoryWindow(),
			MaximumOutputCharacters,
			cancellationToken).ConfigureAwait(false);
		if (!historyResult.IsSuccess)
			return Unavailable(historyResult.FailureReason, historyResult.Detail);
		return Parse(repositoryRoot, candidateFiles, historyResult.Output, isShallow);
	}

	internal static ProjectGitHistorySnapshot Parse(
		string repositoryRoot,
		IReadOnlyList<string> candidateFiles,
		string output,
		bool isShallow = false)
	{
		var root = Path.GetFullPath(repositoryRoot);
		var canonicalCandidates = new Dictionary<string, string>(StringComparer.Ordinal);
		var unavailable = new Dictionary<string, ProjectGitHistoryUnavailableReason>(StringComparer.Ordinal);
		foreach (var candidate in candidateFiles)
		{
			var fullPath = Path.GetFullPath(candidate);
			var relative = PortableRelative(root, fullPath);
			var owner = FindOwningRepository(Path.GetDirectoryName(fullPath) ?? fullPath);
			if (owner is null || !PathComparer.Default.Equals(owner, root))
			{
				unavailable[fullPath] = ProjectGitHistoryUnavailableReason.NestedRepository;
				continue;
			}
			canonicalCandidates[relative] = fullPath;
		}

		var counts = canonicalCandidates.Keys.ToDictionary(
			static path => path,
			static _ => new MutableActivity(),
			StringComparer.Ordinal);
		var commitPosition = 0;
		HashSet<string>? changedInCommit = null;
		foreach (var field in output.Split('\0', StringSplitOptions.None))
		{
			if (TryReadRecordHeader(field, out _))
			{
				ApplyCommit(changedInCommit, counts, commitPosition);
				commitPosition++;
				changedInCommit = new HashSet<string>(StringComparer.Ordinal);
				continue;
			}
			if (field.Length == 0)
				continue;
			if (changedInCommit is null)
				return Unavailable(ProjectGitHistoryUnavailableReason.InvalidOutput);
			if (counts.ContainsKey(field))
				changedInCommit.Add(field);
		}
		ApplyCommit(changedInCommit, counts, commitPosition);

		var files = counts.ToDictionary(
			pair => canonicalCandidates[pair.Key],
			static pair => new ProjectGitFileActivity(
				pair.Value.CommitCount,
				pair.Value.MostRecentCommitPosition),
			PathComparer.Default);
		return new ProjectGitHistorySnapshot(
			CommitWindow,
			commitPosition,
			files,
			unavailable)
		{
			IsShallow = isShallow,
			IsComplete = !isShallow || commitPosition >= CommitWindow
		};
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

	private static bool TryReadRecordHeader(string field, out string hash)
	{
		hash = string.Empty;
		if (field.Length is not (41 or 65) || field[0] != '\x1e')
			return false;
		var candidate = field.AsSpan(1);
		foreach (var character in candidate)
		{
			if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f') and not (>= 'A' and <= 'F'))
				return false;
		}
		hash = candidate.ToString();
		return true;
	}

	private static void ApplyCommit(
		HashSet<string>? changedPaths,
		IReadOnlyDictionary<string, MutableActivity> counts,
		int commitPosition)
	{
		if (changedPaths is null)
			return;
		foreach (var path in changedPaths)
		{
			var activity = counts[path];
			activity.CommitCount++;
			if (activity.MostRecentCommitPosition == 0)
				activity.MostRecentCommitPosition = commitPosition;
		}
	}

	private static ProjectGitHistorySnapshot Unavailable(
		ProjectGitHistoryUnavailableReason reason,
		string? detail = null) =>
		new(CommitWindow, 0,
			new Dictionary<string, ProjectGitFileActivity>(StringComparer.Ordinal),
			new Dictionary<string, ProjectGitHistoryUnavailableReason>(StringComparer.Ordinal),
			reason,
			detail);

	private static string? FindOwningRepository(string startPath)
	{
		try
		{
			var current = new DirectoryInfo(Path.GetFullPath(startPath));
			while (current is not null)
			{
				var marker = Path.Combine(current.FullName, ".git");
				if (Directory.Exists(marker) || File.Exists(marker))
					return current.FullName;
				current = current.Parent;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
		}
		return null;
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
