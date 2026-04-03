namespace DevProjex.Infrastructure.FileSystem;

/// <summary>
/// Unified filesystem entry used by tree building. The tree path needs to sort
/// directories before files, so keeping both kinds in one lightweight record
/// lets us avoid FileSystemInfo allocations while preserving deterministic order.
/// </summary>
internal readonly record struct FileSystemTreeEntry(
	string Name,
	string FullPath,
	bool IsDirectory,
	bool IsHidden,
	long Length);
