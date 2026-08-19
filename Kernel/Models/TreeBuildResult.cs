namespace DevProjex.Kernel.Models;

public sealed record TreeBuildResult(
	FileSystemNode Root,
	bool RootAccessDenied,
	bool HadAccessDenied,
	bool HadScanFailure = false);

public sealed class FileSystemNode(
	string name,
	string fullPath,
	bool isDirectory,
	bool isAccessDenied,
	IReadOnlyList<FileSystemNode> children)
{
	/// <summary>
	/// Shared empty list for file nodes (files have no children).
	/// Avoids allocating a new List for each of potentially 100k+ files.
	/// </summary>
	public static readonly IReadOnlyList<FileSystemNode> EmptyChildren = [];

	public string Name { get; } = name;
	public string FullPath { get; } = fullPath;
	public bool IsDirectory { get; } = isDirectory;
	public bool IsAccessDenied { get; set; } = isAccessDenied;
	public IReadOnlyList<FileSystemNode> Children { get; } = children;
}
