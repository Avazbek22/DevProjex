namespace DevProjex.Infrastructure.FileSystem;

/// <summary>
/// Lightweight directory snapshot produced directly by filesystem enumeration.
/// We keep the metadata we repeatedly need in ignore/tree scans so the hot path
/// does not fall back to extra File.GetAttributes calls per entry.
/// </summary>
internal readonly record struct FileSystemDirectoryEntry(
	string Name,
	string FullPath,
	bool IsHidden);
