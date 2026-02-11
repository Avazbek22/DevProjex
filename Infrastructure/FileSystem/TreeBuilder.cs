using System.IO;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel.Models;

namespace DevProjex.Infrastructure.FileSystem;

public sealed class TreeBuilder : ITreeBuilder
{
	public TreeBuildResult Build(string rootPath, TreeFilterOptions options)
	{
		var state = new BuildState();

		var rootInfo = new DirectoryInfo(rootPath);
		var root = new FileSystemNode(
			name: rootInfo.Name,
			fullPath: rootPath,
			isDirectory: true,
			isAccessDenied: false,
			children: new List<FileSystemNode>());

		BuildChildren(
			parent: root,
			path: rootPath,
			options: options,
			isRoot: true,
			state: state);

		return new TreeBuildResult(root, state.RootAccessDenied, state.HadAccessDenied);
	}

	private static void BuildChildren(
		FileSystemNode parent,
		string path,
		TreeFilterOptions options,
		bool isRoot,
		BuildState state)
	{
		FileSystemInfo[] entries;
		try
		{
			entries = new DirectoryInfo(path)
				.GetFileSystemInfos()
				.OrderBy(fi => !IsDirectory(fi))
				.ThenBy(fi => fi.Name, StringComparer.OrdinalIgnoreCase)
				.ToArray();
		}
		catch (UnauthorizedAccessException)
		{
			state.HadAccessDenied = true;
			if (isRoot) state.RootAccessDenied = true;
			parent.IsAccessDenied = true;
			return;
		}
		catch
		{
			return;
		}

		var children = (List<FileSystemNode>)parent.Children;
		var hasNameFilter = !string.IsNullOrWhiteSpace(options.NameFilter);

		foreach (var entry in entries)
		{
			var name = entry.Name;
			bool isDir = IsDirectory(entry);

			if (isDir && isRoot && !options.AllowedRootFolders.Contains(name))
				continue;

			var ignore = options.IgnoreRules;

			if (isDir)
			{
				if (ShouldSkipDirectory(entry, ignore))
					continue;

				var dirNode = new FileSystemNode(
					name: name,
					fullPath: entry.FullName,
					isDirectory: true,
					isAccessDenied: false,
					children: new List<FileSystemNode>());

				BuildChildren(dirNode, entry.FullName, options, isRoot: false, state);

				// If name filter is active, only include directories that have matching children or match themselves
				if (hasNameFilter)
				{
					bool hasMatchingChildren = dirNode.Children.Count > 0;
					bool matchesName = name.Contains(options.NameFilter!, StringComparison.OrdinalIgnoreCase);

					if (hasMatchingChildren || matchesName)
						children.Add(dirNode);
				}
				else
				{
					children.Add(dirNode);
				}
			}
			else
			{
				if (ShouldSkipFile(entry, ignore))
					continue;

				if (options.AllowedExtensions.Count == 0)
					continue;

				var ext = Path.GetExtension(name);
				if (!options.AllowedExtensions.Contains(ext))
					continue;

				// Apply name filter for files
				if (hasNameFilter && !name.Contains(options.NameFilter!, StringComparison.OrdinalIgnoreCase))
					continue;

				children.Add(new FileSystemNode(
					name: name,
					fullPath: entry.FullName,
					isDirectory: false,
					isAccessDenied: false,
					children: FileSystemNode.EmptyChildren));
			}
		}
	}

	private static bool IsDirectory(FileSystemInfo entry)
	{
		try
		{
			return entry.Attributes.HasFlag(FileAttributes.Directory);
		}
		catch (IOException)
		{
			// Reserved Windows device names (nul, con, prn, etc.) throw IOException
			// Treat them as files (non-directories)
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static bool ShouldSkipDirectory(FileSystemInfo entry, IgnoreRules rules)
	{
		if (rules.UseGitIgnore && rules.GitIgnoreMatcher.IsIgnored(entry.FullName, isDirectory: true, entry.Name))
		{
			if (!rules.GitIgnoreMatcher.ShouldTraverseIgnoredDirectory(entry.FullName, entry.Name))
				return true;
		}

		if (rules.SmartIgnoredFolders.Contains(entry.Name))
			return true;

		if (rules.IgnoreBinFolders && entry.Name.Equals("bin", StringComparison.OrdinalIgnoreCase))
			return true;

		if (rules.IgnoreObjFolders && entry.Name.Equals("obj", StringComparison.OrdinalIgnoreCase))
			return true;

		if (rules.IgnoreDotFolders && entry.Name.StartsWith(".", StringComparison.Ordinal))
			return true;

		if (rules.IgnoreHiddenFolders)
		{
			try
			{
				if (entry.Attributes.HasFlag(FileAttributes.Hidden))
					return true;
			}
			catch (IOException)
			{
				// Reserved Windows device names (nul, con, prn, etc.) throw IOException
				return true;
			}
			catch (UnauthorizedAccessException)
			{
				return true;
			}
		}

		return false;
	}

	private static bool ShouldSkipFile(FileSystemInfo entry, IgnoreRules rules)
	{
		if (rules.UseGitIgnore && rules.GitIgnoreMatcher.IsIgnored(entry.FullName, isDirectory: false, entry.Name))
			return true;

		if (rules.SmartIgnoredFiles.Contains(entry.Name))
			return true;

		if (rules.IgnoreDotFiles && entry.Name.StartsWith(".", StringComparison.Ordinal))
			return true;

		if (rules.IgnoreHiddenFiles)
		{
			try
			{
				if (entry.Attributes.HasFlag(FileAttributes.Hidden))
					return true;
			}
			catch (IOException)
			{
				// Reserved Windows device names (nul, con, prn, etc.) throw IOException
				return true;
			}
			catch (UnauthorizedAccessException)
			{
				return true;
			}
		}

		return false;
	}

	private sealed class BuildState
	{
		public bool RootAccessDenied { get; set; }
		public bool HadAccessDenied { get; set; }
	}
}
