using System.IO.Enumeration;
using DevProjex.Application.Diagnostics;

namespace DevProjex.Infrastructure.FileSystem;

internal static class FileSystemEntryEnumerator
{
	private static readonly StringComparison FileNameComparison = PathComparer.Comparison;

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
		IgnorePipelineDiagnostics.RecordDirectoryEnumeration();
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

	public static DirectoryEnumerationBatch ReadDirectoriesAndGitIgnore(
		string path,
		string relativeDirectory,
		CancellationToken cancellationToken,
		bool captureFiles = false)
	{
		IgnorePipelineDiagnostics.RecordCombinedEntryEnumeration();
		var enumerable = new FileSystemEnumerable<DirectoryDiscoveryEntry>(
			path,
			(ref FileSystemEntry entry) =>
			{
				var name = entry.FileName.ToString();
				return new DirectoryDiscoveryEntry(
					name,
					entry.ToSpecifiedFullPath(),
					entry.IsDirectory,
					entry.IsHidden,
					entry.IsDirectory ? 0 : entry.Length);
			},
			SingleLevelOptions);
		enumerable.ShouldIncludePredicate = (ref FileSystemEntry entry) =>
			!IsReparsePoint(ref entry) &&
			IsSupportedGitMetadataEntry(ref entry, path) &&
			(entry.IsDirectory ||
			 captureFiles ||
			 entry.FileName.Equals(".gitignore", FileNameComparison) ||
			 entry.FileName.Equals(".git", FileNameComparison));

		List<FileSystemDirectoryEntry>? directories = null;
		List<FileSystemFileEntry>? files = null;
		string? gitIgnorePath = null;
		string? gitIgnoreAliasPath = null;
		string? gitMetadataPath = null;
		string? gitMetadataAliasPath = null;
		foreach (var entry in enumerable)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (entry.Name.Equals(".git", StringComparison.Ordinal) &&
			    GitRepositoryBoundaryProbe.ExistsAt(path))
			{
				gitMetadataPath ??= Path.Combine(path, ".git");
			}
			else if (gitMetadataPath is null &&
			         gitMetadataAliasPath is null &&
			         OperatingSystem.IsWindows() &&
			         entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) &&
			         IsWindowsCompatibleControlAlias(
				         entry.Name,
				         ".git",
				         GitRepositoryBoundaryProbe.ExistsAt(path)))
			{
				gitMetadataAliasPath = Path.Combine(path, ".git");
			}

			if (!entry.IsDirectory)
			{
				if (entry.Name.Equals(".gitignore", StringComparison.Ordinal))
					gitIgnorePath ??= entry.FullPath;
				else if (gitIgnorePath is null &&
				         gitIgnoreAliasPath is null &&
				         OperatingSystem.IsWindows() &&
				         entry.Name.Equals(".gitignore", StringComparison.OrdinalIgnoreCase) &&
				         IsWindowsCompatibleControlAlias(
					         entry.Name,
					         ".gitignore",
					         File.Exists(Path.Combine(path, ".gitignore"))))
				{
					gitIgnoreAliasPath = entry.FullPath;
				}
				if (captureFiles)
				{
					(files ??= []).Add(new FileSystemFileEntry(
						entry.Name,
						entry.FullPath,
						CombineRelativePath(relativeDirectory, entry.Name),
						entry.IsHidden,
						entry.Length));
				}
				continue;
			}

			(directories ??= []).Add(new FileSystemDirectoryEntry(
				entry.Name,
				entry.FullPath,
				CombineRelativePath(relativeDirectory, entry.Name),
				entry.IsHidden));
		}

		return new DirectoryEnumerationBatch(
			directories ?? [],
			files ?? [],
			gitIgnorePath ?? gitIgnoreAliasPath,
			gitMetadataPath ?? gitMetadataAliasPath);
	}

	public static IEnumerable<FileSystemFileEntry> EnumerateFiles(string path)
	{
		return EnumerateFiles(path, relativeDirectory: string.Empty);
	}

	public static IEnumerable<FileSystemFileEntry> EnumerateFiles(string path, string relativeDirectory)
	{
		IgnorePipelineDiagnostics.RecordFileEnumeration();
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
		enumerable.ShouldIncludePredicate = (ref FileSystemEntry entry) =>
			!entry.IsDirectory &&
			!IsReparsePoint(ref entry) &&
			IsSupportedGitMetadataEntry(ref entry, path);
		return enumerable;
	}

	public static IEnumerable<FileSystemTreeEntry> EnumerateEntries(string path)
	{
		return EnumerateEntries(path, relativeDirectory: string.Empty);
	}

	public static IEnumerable<FileSystemTreeEntry> EnumerateEntries(string path, string relativeDirectory)
	{
		IgnorePipelineDiagnostics.RecordCombinedEntryEnumeration();
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
		enumerable.ShouldIncludePredicate = (ref FileSystemEntry entry) =>
			!IsReparsePoint(ref entry) &&
			IsSupportedGitMetadataEntry(ref entry, path);
		return enumerable;
	}

	private static bool IsSupportedGitMetadataEntry(ref FileSystemEntry entry, string parentPath) =>
		!entry.FileName.Equals(".git", FileNameComparison) ||
		GitRepositoryBoundaryProbe.ExistsAt(parentPath);

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

	internal static bool IsWindowsCompatibleControlAlias(
		string observedName,
		string expectedName,
		bool expectedPathResolves) =>
		OperatingSystem.IsWindows() &&
		expectedPathResolves &&
		!ProjectTreePathIdentity.CanonicalComparer.Equals(observedName, expectedName) &&
		StringComparer.OrdinalIgnoreCase.Equals(observedName, expectedName);

	private readonly record struct DirectoryDiscoveryEntry(
		string Name,
		string FullPath,
		bool IsDirectory,
		bool IsHidden,
		long Length);
}

internal readonly record struct DirectoryEnumerationBatch(
	IReadOnlyList<FileSystemDirectoryEntry> Directories,
	IReadOnlyList<FileSystemFileEntry> Files,
	string? GitIgnorePath,
	string? GitMetadataPath);
