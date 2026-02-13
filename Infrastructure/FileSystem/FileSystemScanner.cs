using System.IO;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel.Models;

namespace DevProjex.Infrastructure.FileSystem;

public sealed class FileSystemScanner : IFileSystemScanner
{
	public bool CanReadRoot(string rootPath)
	{
		try
		{
			_ = Directory.EnumerateFileSystemEntries(rootPath).GetEnumerator().MoveNext();
			return true;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
		catch
		{
			return true;
		}
	}

	public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules)
	{
		var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		bool rootAccessDenied = false;
		bool hadAccessDenied = false;

		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
			return new ScanResult<HashSet<string>>(exts, false, false);

		var pending = new Stack<string>();
		pending.Push(rootPath);

		bool isFirst = true;

		while (pending.Count > 0)
		{
			var dir = pending.Pop();

			string[] subDirs;
			try
			{
				subDirs = Directory.GetDirectories(dir);
			}
			catch (UnauthorizedAccessException)
			{
				hadAccessDenied = true;
				if (isFirst) rootAccessDenied = true;
				continue;
			}
			catch
			{
				continue;
			}

			foreach (var sd in subDirs)
			{
				var dirName = Path.GetFileName(sd);
				if (ShouldSkipDirectoryByName(dirName, sd, rules))
					continue;

				pending.Push(sd);
			}

			string[] files;
			try
			{
				files = Directory.GetFiles(dir);
			}
			catch (UnauthorizedAccessException)
			{
				hadAccessDenied = true;
				if (isFirst) rootAccessDenied = true;
				continue;
			}
			catch
			{
				continue;
			}

			foreach (var file in files)
			{
				var name = Path.GetFileName(file);

				if (ShouldSkipFileByName(name, file, rules))
					continue;

				var ext = Path.GetExtension(name);
				if (!string.IsNullOrWhiteSpace(ext))
					exts.Add(ext);
			}

			isFirst = false;
		}

		return new ScanResult<HashSet<string>>(exts, rootAccessDenied, hadAccessDenied);
	}

	public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules)
	{
		var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
			return new ScanResult<HashSet<string>>(exts, false, false);

		bool rootAccessDenied = false;
		bool hadAccessDenied = false;

		string[] files;
		try
		{
			files = Directory.GetFiles(rootPath);
		}
		catch (UnauthorizedAccessException)
		{
			return new ScanResult<HashSet<string>>(exts, true, true);
		}
		catch
		{
			return new ScanResult<HashSet<string>>(exts, false, false);
		}

		foreach (var file in files)
		{
			var name = Path.GetFileName(file);
			if (ShouldSkipFileByName(name, file, rules))
				continue;

			var ext = Path.GetExtension(name);
			if (!string.IsNullOrWhiteSpace(ext))
				exts.Add(ext);
		}

		return new ScanResult<HashSet<string>>(exts, rootAccessDenied, hadAccessDenied);
	}

	public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules)
	{
		var names = new List<string>();

		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
			return new ScanResult<List<string>>(names, false, false);

		string[] dirs;
		try
		{
			dirs = Directory.GetDirectories(rootPath);
		}
		catch (UnauthorizedAccessException)
		{
			return new ScanResult<List<string>>(names, true, true);
		}
		catch
		{
			return new ScanResult<List<string>>(names, false, false);
		}

		foreach (var dir in dirs)
		{
			var dirName = Path.GetFileName(dir);
			if (ShouldSkipDirectoryByName(dirName, dir, rules))
				continue;

			names.Add(dirName);
		}

		names.Sort(StringComparer.OrdinalIgnoreCase);
		return new ScanResult<List<string>>(names, false, false);
	}

	/// <summary>
	/// Optimized version that avoids DirectoryInfo allocation when possible.
	/// Only creates DirectoryInfo when checking Hidden attribute.
	/// </summary>
	private static bool ShouldSkipDirectoryByName(string name, string fullPath, IgnoreRules rules)
	{
		var matcher = rules.ResolveGitIgnoreMatcher(fullPath);
		if (!ReferenceEquals(matcher, GitIgnoreMatcher.Empty) &&
		    matcher.IsIgnored(fullPath, isDirectory: true, name))
		{
			if (!matcher.ShouldTraverseIgnoredDirectory(fullPath, name))
				return true;
		}

		if (rules.ShouldApplySmartIgnore(fullPath) && rules.SmartIgnoredFolders.Contains(name))
			return true;

		if (rules.IgnoreDotFolders && name.StartsWith(".", StringComparison.Ordinal))
			return true;

		if (rules.IgnoreHiddenFolders)
		{
			try
			{
				if (File.GetAttributes(fullPath).HasFlag(FileAttributes.Hidden))
					return true;
			}
			catch (IOException)
			{
				return true;
			}
			catch (UnauthorizedAccessException)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Optimized version that avoids FileInfo allocation when possible.
	/// Only checks attributes when necessary.
	/// </summary>
	private static bool ShouldSkipFileByName(string name, string fullPath, IgnoreRules rules)
	{
		var matcher = rules.ResolveGitIgnoreMatcher(fullPath);
		if (!ReferenceEquals(matcher, GitIgnoreMatcher.Empty) &&
		    matcher.IsIgnored(fullPath, isDirectory: false, name))
			return true;

		if (rules.ShouldApplySmartIgnore(fullPath) && rules.SmartIgnoredFiles.Contains(name))
			return true;

		if (rules.IgnoreDotFiles && name.StartsWith(".", StringComparison.Ordinal))
			return true;

		if (rules.IgnoreHiddenFiles)
		{
			try
			{
				if (File.GetAttributes(fullPath).HasFlag(FileAttributes.Hidden))
					return true;
			}
			catch (IOException)
			{
				return true;
			}
			catch (UnauthorizedAccessException)
			{
				return true;
			}
		}

		return false;
	}
}
