namespace DevProjex.Infrastructure.FileSystem;

/// <summary>
/// Lightweight file snapshot produced directly by filesystem enumeration.
/// Hidden flag and file length come from the enumeration payload, which keeps
/// large scans cheaper than re-querying attributes for every file.
/// </summary>
internal readonly record struct FileSystemFileEntry(
	string Name,
	string FullPath,
	bool IsHidden,
	long Length);
