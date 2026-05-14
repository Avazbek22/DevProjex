using System.IO.Enumeration;

namespace DevProjex.Infrastructure.FileSystem;

internal static class FileSystemEntryEnumerator
{
	private static readonly EnumerationOptions SingleLevelOptions = new()
	{
		RecurseSubdirectories = false,
		ReturnSpecialDirectories = false,
		AttributesToSkip = 0,
		IgnoreInaccessible = false
	};

	public static IEnumerable<FileSystemDirectoryEntry> EnumerateDirectories(string path)
	{
		return EnumerateDirectories(path, relativeDirectory: string.Empty);
	}

	public static IEnumerable<FileSystemDirectoryEntry> EnumerateDirectories(string path, string relativeDirectory)
	{
		var enumerable = new FileSystemEnumerable<FileSystemDirectoryEntry>(
			path,
			(ref FileSystemEntry entry) =>
			{
				var name = entry.FileName.ToString();
				return new FileSystemDirectoryEntry(
					name,
					entry.ToSpecifiedFullPath(),
					CombineRelativePath(relativeDirectory, name),
					entry.IsHidden);
			},
			SingleLevelOptions);
		enumerable.ShouldIncludePredicate = static (ref FileSystemEntry entry) =>
			entry.IsDirectory && !IsReparsePoint(ref entry);
		return enumerable;
	}

	public static IEnumerable<FileSystemFileEntry> EnumerateFiles(string path)
	{
		return EnumerateFiles(path, relativeDirectory: string.Empty);
	}

	public static IEnumerable<FileSystemFileEntry> EnumerateFiles(string path, string relativeDirectory)
	{
		var enumerable = new FileSystemEnumerable<FileSystemFileEntry>(
			path,
			(ref FileSystemEntry entry) =>
			{
				var name = entry.FileName.ToString();
				return new FileSystemFileEntry(
					name,
					entry.ToSpecifiedFullPath(),
					CombineRelativePath(relativeDirectory, name),
					entry.IsHidden,
					entry.Length);
			},
			SingleLevelOptions);
		enumerable.ShouldIncludePredicate = static (ref FileSystemEntry entry) =>
			!entry.IsDirectory && !IsReparsePoint(ref entry);
		return enumerable;
	}

	public static IEnumerable<FileSystemTreeEntry> EnumerateEntries(string path)
	{
		return EnumerateEntries(path, relativeDirectory: string.Empty);
	}

	public static IEnumerable<FileSystemTreeEntry> EnumerateEntries(string path, string relativeDirectory)
	{
		var enumerable = new FileSystemEnumerable<FileSystemTreeEntry>(
			path,
			(ref FileSystemEntry entry) =>
			{
				var name = entry.FileName.ToString();
				return new FileSystemTreeEntry(
					name,
					entry.ToSpecifiedFullPath(),
					CombineRelativePath(relativeDirectory, name),
					entry.IsDirectory,
					entry.IsHidden,
					entry.IsDirectory ? 0 : entry.Length);
			},
			SingleLevelOptions);
		enumerable.ShouldIncludePredicate = static (ref FileSystemEntry entry) => !IsReparsePoint(ref entry);
		return enumerable;
	}

	private static string CombineRelativePath(string relativeDirectory, string name)
	{
		return string.IsNullOrEmpty(relativeDirectory)
			? name
			: $"{relativeDirectory}/{name}";
	}

	private static bool IsReparsePoint(ref FileSystemEntry entry)
	{
		return (entry.Attributes & FileAttributes.ReparsePoint) != 0;
	}
}
