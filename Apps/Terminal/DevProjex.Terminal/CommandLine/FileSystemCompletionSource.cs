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
		string? baseDirectory = null)
	{
		ArgumentNullException.ThrowIfNull(word);
		if (!TryResolveSearch(word, baseDirectory, out var search))
			return [];

		try
		{
			var comparison = OperatingSystem.IsWindows()
				? StringComparison.OrdinalIgnoreCase
				: StringComparison.Ordinal;
			return Directory
				.EnumerateFileSystemEntries(search.DirectoryPath)
				.Select(path => new
				{
					Path = path,
					Name = Path.GetFileName(path),
					IsDirectory = Directory.Exists(path)
				})
				.Where(entry =>
					entry.Name.StartsWith(search.NamePrefix, comparison) &&
					(kind == FileSystemCompletionKind.FilesAndDirectories ||
					 entry.IsDirectory))
				.OrderByDescending(static entry => entry.IsDirectory)
				.ThenBy(static entry => entry.Name, StringComparer.Ordinal)
				.Take(MaximumCandidateCount)
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

	private static bool TryResolveSearch(
		string word,
		string? baseDirectory,
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
			var directoryPath = Path.GetFullPath(
				directoryText,
				string.IsNullOrWhiteSpace(baseDirectory)
					? Directory.GetCurrentDirectory()
					: Path.GetFullPath(baseDirectory));
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
			return string.IsNullOrWhiteSpace(token)
				? Directory.GetCurrentDirectory()
				: Path.GetFullPath(token);
		}
		catch (Exception exception) when (exception is
			ArgumentException or
			NotSupportedException or
			PathTooLongException or
			SecurityException)
		{
			return Directory.GetCurrentDirectory();
		}
	}

	private readonly record struct CompletionSearch(
		string DirectoryPath,
		string DisplayPrefix,
		string NamePrefix,
		char Separator);
}
