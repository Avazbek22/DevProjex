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

		var operation = GitProcessOperation.ReadHistoryWindow();
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(operation.Deadline);
		using var process = new Process
		{
			StartInfo = GitProcessStartInfoFactory.Create(repositoryRoot, operation)
		};
		try
		{
			if (!process.Start())
				return Unavailable(ProjectGitHistoryUnavailableReason.ProcessFailed);
			process.StandardInput.Close();
		}
		catch (Win32Exception)
		{
			return Unavailable(ProjectGitHistoryUnavailableReason.GitUnavailable);
		}
		catch (InvalidOperationException exception)
		{
			return Unavailable(ProjectGitHistoryUnavailableReason.ProcessFailed, exception.Message);
		}

		var outputTask = GitProcessOutputReader.ReadAsync(
			process.StandardOutput,
			MaximumOutputCharacters,
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
				return Unavailable(ProjectGitHistoryUnavailableReason.ProcessFailed);
			}
			var output = await outputTask.ConfigureAwait(false);
			var error = await errorTask.ConfigureAwait(false);
			if (output.ExceededLimit || error.ExceededLimit)
				return Unavailable(ProjectGitHistoryUnavailableReason.OutputLimitExceeded);
			if (process.ExitCode != 0)
				return Unavailable(ProjectGitHistoryUnavailableReason.ProcessFailed, FirstLine(error.Text));
			return Parse(repositoryRoot, candidateFiles, output.Text);
		}
		catch (OperationCanceledException)
		{
			await GitProcessOutputReader
				.ObserveAfterTerminationAsync(process, outputTask, errorTask)
				.ConfigureAwait(false);
			if (cancellationToken.IsCancellationRequested)
				throw;
			return Unavailable(ProjectGitHistoryUnavailableReason.ProcessFailed, "Git history read exceeded its safety deadline.");
		}
	}

	internal static ProjectGitHistorySnapshot Parse(
		string repositoryRoot,
		IReadOnlyList<string> candidateFiles,
		string output)
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
		foreach (var record in output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
		{
			var fields = record.Split('\0', StringSplitOptions.RemoveEmptyEntries);
			if (fields.Length == 0)
				continue;
			var hash = fields[0].Trim();
			if (hash.Length < 7 || !hash.All(Uri.IsHexDigit))
				return Unavailable(ProjectGitHistoryUnavailableReason.InvalidOutput);
			commitPosition++;
			var changedInCommit = new HashSet<string>(StringComparer.Ordinal);
			foreach (var field in fields.Skip(1))
			{
				var path = field.Trim('\r', '\n');
				if (path.Length > 0 && counts.ContainsKey(path))
					changedInCommit.Add(path);
			}
			foreach (var path in changedInCommit)
			{
				var activity = counts[path];
				activity.CommitCount++;
				if (activity.MostRecentCommitPosition == 0)
					activity.MostRecentCommitPosition = commitPosition;
			}
		}

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
			unavailable);
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
}
