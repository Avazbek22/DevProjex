using System.CommandLine.Completions;
using System.Security;

namespace DevProjex.Terminal.CommandLine;

internal enum FileSystemCompletionKind
{
	Directories,
	FilesAndDirectories
}

internal static class FileSystemCompletionSource
{
	private const int MaximumCandidateCount = 200;
	private static readonly AsyncLocal<string?> ScopedBaseDirectory = new();

	public static IDisposable UseBaseDirectory(string? baseDirectory)
	{
		var previous = ScopedBaseDirectory.Value;
		ScopedBaseDirectory.Value = baseDirectory;
		return new BaseDirectoryScope(previous);
	}

	public static IEnumerable<string> Complete(
		CompletionContext context,
		FileSystemCompletionKind kind,
		string? baseDirectory = null)
	{
		ArgumentNullException.ThrowIfNull(context);
		return Complete(context.WordToComplete ?? string.Empty, kind, baseDirectory);
	}

	public static IEnumerable<string> Complete(
		string word,
		FileSystemCompletionKind kind,
		string? baseDirectory = null,
		string? userHomeDirectory = null)
	{
		ArgumentNullException.ThrowIfNull(word);
		if (!TryResolveSearch(word, baseDirectory, userHomeDirectory, out var search))
			return [];

		try
		{
			var comparison = OperatingSystem.IsWindows()
				? StringComparison.OrdinalIgnoreCase
				: StringComparison.Ordinal;
			var candidates = Directory
				.EnumerateFileSystemEntries(search.DirectoryPath)
				.Select(path => new
				{
					Path = path,
					Name = Path.GetFileName(path)
				})
				.Where(entry =>
					entry.Name.StartsWith(search.NamePrefix, comparison))
				.Select(entry => new CompletionCandidate(
					entry.Name,
					Directory.Exists(entry.Path)))
				.Where(entry =>
					kind == FileSystemCompletionKind.FilesAndDirectories ||
					entry.IsDirectory);
			return SelectBestCandidates(candidates)
				.Select(entry =>
					search.DisplayPrefix +
					entry.Name +
					(entry.IsDirectory ? search.Separator : string.Empty))
				.ToArray();
		}
		catch (Exception exception) when (exception is
			UnauthorizedAccessException or
			DirectoryNotFoundException or
			IOException or
			ArgumentException or
			NotSupportedException or
			SecurityException)
		{
			return [];
		}
	}

	internal static IReadOnlyList<CompletionCandidate> SelectBestCandidates(
		IEnumerable<CompletionCandidate> candidates,
		int maximumCandidateCount = MaximumCandidateCount)
	{
		ArgumentNullException.ThrowIfNull(candidates);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCandidateCount);

		var retained = new SortedSet<RankedCompletionCandidate>(RankedCompletionCandidateComparer.Instance);
		long sequence = 0;
		foreach (var candidate in candidates)
		{
			retained.Add(new RankedCompletionCandidate(candidate, sequence++));
			if (retained.Count > maximumCandidateCount)
				retained.Remove(retained.Max);
		}

		return retained.Select(static item => item.Candidate).ToArray();
	}

	private static bool TryResolveSearch(
		string word,
		string? baseDirectory,
		string? userHomeDirectory,
		out CompletionSearch search)
	{
		var separatorIndex = Math.Max(
			word.LastIndexOf(Path.DirectorySeparatorChar),
			word.LastIndexOf(Path.AltDirectorySeparatorChar));
		var displayPrefix = separatorIndex >= 0
			? word[..(separatorIndex + 1)]
			: string.Empty;
		var namePrefix = separatorIndex >= 0
			? word[(separatorIndex + 1)..]
			: word;
		var directoryText = displayPrefix.Length == 0
			? "."
			: displayPrefix;
		try
		{
			var effectiveBaseDirectory = ResolveBaseDirectory(baseDirectory);
			var directoryPath = ResolveDirectoryPath(
				directoryText,
				effectiveBaseDirectory,
				userHomeDirectory);
			if (directoryPath is null)
			{
				search = default;
				return false;
			}
			var separator = displayPrefix.EndsWith(Path.AltDirectorySeparatorChar)
				? Path.AltDirectorySeparatorChar
				: Path.DirectorySeparatorChar;
			search = new CompletionSearch(
				directoryPath,
				displayPrefix,
				namePrefix,
				separator);
			return true;
		}
		catch (Exception exception) when (exception is
			ArgumentException or
			NotSupportedException or
			PathTooLongException or
			SecurityException)
		{
			search = default;
			return false;
		}
	}

	public static string ResolveProjectDirectory(CompletionContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		var project = context.ParseResult.CommandResult.Command.Arguments
			.FirstOrDefault(static argument => argument.Name == "PROJECT");
		var token = project is null
			? null
			: context.ParseResult.GetResult(project)?.Tokens.FirstOrDefault()?.Value;
		try
		{
			var baseDirectory = Path.GetFullPath(ResolveBaseDirectory(null));
			return string.IsNullOrWhiteSpace(token)
				? baseDirectory
				: Path.GetFullPath(token, baseDirectory);
		}
		catch (Exception exception) when (exception is
			ArgumentException or
			NotSupportedException or
			PathTooLongException or
			SecurityException)
		{
			return string.Empty;
		}
	}

	private static string ResolveBaseDirectory(string? baseDirectory) =>
		string.IsNullOrWhiteSpace(baseDirectory)
			? ScopedBaseDirectory.Value ?? Directory.GetCurrentDirectory()
			: baseDirectory;

	private readonly record struct CompletionSearch(
		string DirectoryPath,
		string DisplayPrefix,
		string NamePrefix,
		char Separator);

	internal readonly record struct CompletionCandidate(string Name, bool IsDirectory);

	private readonly record struct RankedCompletionCandidate(
		CompletionCandidate Candidate,
		long Sequence);

	private sealed class RankedCompletionCandidateComparer : IComparer<RankedCompletionCandidate>
	{
		public static RankedCompletionCandidateComparer Instance { get; } = new();

		public int Compare(RankedCompletionCandidate left, RankedCompletionCandidate right)
		{
			var result = right.Candidate.IsDirectory.CompareTo(left.Candidate.IsDirectory);
			if (result != 0)
				return result;

			result = StringComparer.Ordinal.Compare(left.Candidate.Name, right.Candidate.Name);
			return result != 0 ? result : left.Sequence.CompareTo(right.Sequence);
		}
	}

	private static string? ResolveDirectoryPath(
		string directoryText,
		string baseDirectory,
		string? userHomeDirectory)
	{
		if (directoryText.Length < 2 ||
		    directoryText[0] != '~' ||
		    !IsDirectorySeparator(directoryText[1]))
		{
			return Path.GetFullPath(
				directoryText,
				Path.GetFullPath(baseDirectory));
		}

		var homeDirectory = string.IsNullOrWhiteSpace(userHomeDirectory)
			? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
			: userHomeDirectory;
		if (string.IsNullOrWhiteSpace(homeDirectory))
			return null;

		var resolvedHomeDirectory = Path.GetFullPath(homeDirectory);
		var relativeDirectory = directoryText[2..];
		return relativeDirectory.Length == 0
			? resolvedHomeDirectory
			: Path.GetFullPath(relativeDirectory, resolvedHomeDirectory);
	}

	private static bool IsDirectorySeparator(char character) =>
		character == Path.DirectorySeparatorChar ||
		character == Path.AltDirectorySeparatorChar;

	private sealed class BaseDirectoryScope(string? previous) : IDisposable
	{
		private bool _disposed;

		public void Dispose()
		{
			if (_disposed)
				return;
			ScopedBaseDirectory.Value = previous;
			_disposed = true;
		}
	}
}
