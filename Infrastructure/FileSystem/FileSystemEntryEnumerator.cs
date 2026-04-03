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
		var enumerable = new FileSystemEnumerable<FileSystemDirectoryEntry>(
			path,
			static (ref FileSystemEntry entry) => new FileSystemDirectoryEntry(
				entry.FileName.ToString(),
				entry.ToSpecifiedFullPath(),
				entry.IsHidden),
			SingleLevelOptions);
		enumerable.ShouldIncludePredicate = static (ref FileSystemEntry entry) => entry.IsDirectory;
		return enumerable;
	}

	public static IEnumerable<FileSystemFileEntry> EnumerateFiles(string path)
	{
		var enumerable = new FileSystemEnumerable<FileSystemFileEntry>(
			path,
			static (ref FileSystemEntry entry) => new FileSystemFileEntry(
				entry.FileName.ToString(),
				entry.ToSpecifiedFullPath(),
				entry.IsHidden,
				entry.Length),
			SingleLevelOptions);
		enumerable.ShouldIncludePredicate = static (ref FileSystemEntry entry) => !entry.IsDirectory;
		return enumerable;
	}

	public static IEnumerable<FileSystemTreeEntry> EnumerateEntries(string path)
	{
		var enumerable = new FileSystemEnumerable<FileSystemTreeEntry>(
			path,
			static (ref FileSystemEntry entry) => new FileSystemTreeEntry(
				entry.FileName.ToString(),
				entry.ToSpecifiedFullPath(),
				entry.IsDirectory,
				entry.IsHidden,
				entry.IsDirectory ? 0 : entry.Length),
			SingleLevelOptions);
		enumerable.ShouldIncludePredicate = static (ref FileSystemEntry _) => true;
		return enumerable;
	}
}
