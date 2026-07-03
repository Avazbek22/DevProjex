namespace DevProjex.Kernel.Models;

/// <summary>
/// Compact filesystem inventory entry. The filesystem scanner owns IO; higher-level
/// project-load code can reuse the captured metadata without re-enumerating folders.
/// </summary>
public struct ProjectTreeInventoryEntry(
	string name,
	string fullPath,
	string relativePath,
	int parentIndex,
	bool isDirectory,
	bool isHidden,
	long length)
{
	public string Name { get; } = name;
	public string FullPath { get; } = fullPath;
	public string RelativePath { get; } = relativePath;
	public int ParentIndex { get; } = parentIndex;
	public bool IsDirectory { get; } = isDirectory;
	public bool IsHidden { get; } = isHidden;
	public long Length { get; } = length;
	public int FirstChildIndex { get; set; } = -1;
	public int ChildCount { get; set; }
	public bool IsAccessDenied { get; set; }
}
