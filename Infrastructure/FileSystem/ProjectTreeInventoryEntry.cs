namespace DevProjex.Infrastructure.FileSystem;

/// <summary>
/// Compact tree-inventory entry. The scanner owns filesystem IO; the tree builder
/// only projects these entries into visible nodes, so filtering can be tested without
/// mixing it with directory enumeration details.
/// </summary>
internal struct ProjectTreeInventoryEntry(
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
