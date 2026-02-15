using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel.Contracts;
using DevProjex.Kernel.Models;

namespace DevProjex.Application.UseCases;

public sealed class ScanOptionsUseCase
{
	private readonly IFileSystemScanner _scanner;

	// Optimal parallelism for modern multi-core CPUs (targeting developers with NVMe SSDs)
	private static readonly int MaxParallelism = Math.Max(4, Environment.ProcessorCount);

	public ScanOptionsUseCase(IFileSystemScanner scanner)
	{
		_scanner = scanner;
	}

	public ScanOptionsResult Execute(ScanOptionsRequest request, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		// Run extensions and root folders scans in parallel for better performance
		var extensionsTask = Task.Run(() => _scanner.GetExtensions(request.RootPath, request.IgnoreRules, cancellationToken), cancellationToken);
		var rootFoldersTask = Task.Run(() => _scanner.GetRootFolderNames(request.RootPath, request.IgnoreRules, cancellationToken), cancellationToken);

		Task.WaitAll([extensionsTask, rootFoldersTask], cancellationToken);

		var extensions = extensionsTask.Result;
		var rootFolders = rootFoldersTask.Result;

		return new ScanOptionsResult(
			Extensions: extensions.Value.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(),
			RootFolders: rootFolders.Value.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(),
			RootAccessDenied: extensions.RootAccessDenied || rootFolders.RootAccessDenied,
			HadAccessDenied: extensions.HadAccessDenied || rootFolders.HadAccessDenied);
	}

	public ScanResult<HashSet<string>> GetExtensionsForRootFolders(
		string rootPath,
		IReadOnlyCollection<string> rootFolders,
		IgnoreRules ignoreRules,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		// Thread-safe collection for parallel aggregation
		var extensions = new ConcurrentBag<string>();
		var rootAccessDenied = 0;
		var hadAccessDenied = 0;

		// Always scan root-level files, even when no subfolders are selected.
		// This ensures folders containing only files (no subdirectories) work correctly.
		var rootFiles = _scanner.GetRootFileExtensions(rootPath, ignoreRules, cancellationToken);
		foreach (var ext in rootFiles.Value)
			extensions.Add(ext);

		if (rootFiles.RootAccessDenied) Interlocked.Exchange(ref rootAccessDenied, 1);
		if (rootFiles.HadAccessDenied) Interlocked.Exchange(ref hadAccessDenied, 1);

		// Scan extensions from selected subfolders in parallel
		if (rootFolders.Count > 0)
		{
			var parallelOptions = new ParallelOptions
			{
				MaxDegreeOfParallelism = MaxParallelism,
				CancellationToken = cancellationToken
			};

			Parallel.ForEach(rootFolders, parallelOptions, folder =>
			{
				cancellationToken.ThrowIfCancellationRequested();

				var folderPath = Path.Combine(rootPath, folder);
				var result = _scanner.GetExtensions(folderPath, ignoreRules, cancellationToken);

				foreach (var ext in result.Value)
					extensions.Add(ext);

				if (result.RootAccessDenied) Interlocked.Exchange(ref rootAccessDenied, 1);
				if (result.HadAccessDenied) Interlocked.Exchange(ref hadAccessDenied, 1);
			});
		}

		// Convert to HashSet for deduplication
		var uniqueExtensions = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
		return new ScanResult<HashSet<string>>(uniqueExtensions, rootAccessDenied == 1, hadAccessDenied == 1);
	}

	public bool CanReadRoot(string rootPath) => _scanner.CanReadRoot(rootPath);
}
