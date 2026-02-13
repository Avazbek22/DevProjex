using System.Collections.Concurrent;
using System.IO;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel.Models;

namespace DevProjex.Infrastructure.FileSystem;

public sealed class FileSystemScanner : IFileSystemScanner
{
	// Optimal parallelism for modern multi-core CPUs with NVMe SSDs
	private static readonly int MaxParallelism = Math.Max(4, Environment.ProcessorCount);

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

	public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
			return new ScanResult<HashSet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false);

		// Thread-safe collections for parallel scanning
		var extensions = new ConcurrentBag<string>();
		var directories = new ConcurrentBag<string>();
		var rootAccessDenied = 0;
		var hadAccessDenied = 0;

		// First pass: collect all directories (single-threaded to avoid race conditions on initial scan)
		var pending = new Stack<string>();
		pending.Push(rootPath);
		bool isFirst = true;

		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var dir = pending.Pop();
			directories.Add(dir);

			string[] subDirs;
			try
			{
				subDirs = Directory.GetDirectories(dir);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (UnauthorizedAccessException)
			{
				Interlocked.Exchange(ref hadAccessDenied, 1);
				if (isFirst) Interlocked.Exchange(ref rootAccessDenied, 1);
				continue;
			}
			catch
			{
				continue;
			}

			foreach (var sd in subDirs)
			{
				cancellationToken.ThrowIfCancellationRequested();

				var dirName = Path.GetFileName(sd);
				if (ShouldSkipDirectoryByName(dirName, sd, rules))
					continue;

				pending.Push(sd);
			}

			isFirst = false;
		}

		// Second pass: scan files in all directories in parallel
		var parallelOptions = new ParallelOptions
		{
			MaxDegreeOfParallelism = MaxParallelism,
			CancellationToken = cancellationToken
		};

		Parallel.ForEach(directories, parallelOptions, dir =>
		{
			parallelOptions.CancellationToken.ThrowIfCancellationRequested();

			string[] files;
			try
			{
				files = Directory.GetFiles(dir);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (UnauthorizedAccessException)
			{
				Interlocked.Exchange(ref hadAccessDenied, 1);
				return;
			}
			catch
			{
				return;
			}

			foreach (var file in files)
			{
				parallelOptions.CancellationToken.ThrowIfCancellationRequested();

				var name = Path.GetFileName(file);

				if (ShouldSkipFileByName(name, file, rules))
					continue;

				var ext = Path.GetExtension(name);
				if (!string.IsNullOrWhiteSpace(ext))
					extensions.Add(ext);
			}
		});

		// Convert to HashSet for deduplication
		var uniqueExtensions = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
		return new ScanResult<HashSet<string>>(uniqueExtensions, rootAccessDenied == 1, hadAccessDenied == 1);
	}

	public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

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
		catch (OperationCanceledException)
		{
			throw;
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
			cancellationToken.ThrowIfCancellationRequested();

			var name = Path.GetFileName(file);
			if (ShouldSkipFileByName(name, file, rules))
				continue;

			var ext = Path.GetExtension(name);
			if (!string.IsNullOrWhiteSpace(ext))
				exts.Add(ext);
		}

		return new ScanResult<HashSet<string>>(exts, rootAccessDenied, hadAccessDenied);
	}

	public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var names = new List<string>();

		if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
			return new ScanResult<List<string>>(names, false, false);

		string[] dirs;
		try
		{
			dirs = Directory.GetDirectories(rootPath);
		}
		catch (OperationCanceledException)
		{
			throw;
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
			cancellationToken.ThrowIfCancellationRequested();

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
