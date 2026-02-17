using System.Collections.Generic;
using System.IO;
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

		ScanResult<HashSet<string>>? extensions = null;
		ScanResult<List<string>>? rootFolders = null;

		Parallel.Invoke(
			new ParallelOptions
			{
				MaxDegreeOfParallelism = 2,
				CancellationToken = cancellationToken
			},
			() => extensions = _scanner.GetExtensions(request.RootPath, request.IgnoreRules, cancellationToken),
			() => rootFolders = _scanner.GetRootFolderNames(request.RootPath, request.IgnoreRules, cancellationToken));

		if (extensions is null || rootFolders is null)
			throw new InvalidOperationException("Scan results were not produced.");

		// Convert to List and sort in-place - avoids LINQ intermediate allocations
		var extensionsList = new List<string>(extensions.Value);
		extensionsList.Sort(StringComparer.OrdinalIgnoreCase);

		var rootFoldersList = new List<string>(rootFolders.Value);
		rootFoldersList.Sort(StringComparer.OrdinalIgnoreCase);

		return new ScanOptionsResult(
			Extensions: extensionsList,
			RootFolders: rootFoldersList,
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

		var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var mergeLock = new object();
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

			Parallel.ForEach(
				rootFolders,
				parallelOptions,
				() => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				(folder, _, localExtensions) =>
				{
					cancellationToken.ThrowIfCancellationRequested();

					var folderPath = Path.Combine(rootPath, folder);
					var result = _scanner.GetExtensions(folderPath, ignoreRules, cancellationToken);

					foreach (var ext in result.Value)
						localExtensions.Add(ext);

					if (result.RootAccessDenied) Interlocked.Exchange(ref rootAccessDenied, 1);
					if (result.HadAccessDenied) Interlocked.Exchange(ref hadAccessDenied, 1);

					return localExtensions;
				},
				localExtensions =>
				{
					if (localExtensions.Count == 0)
						return;

					lock (mergeLock)
					{
						extensions.UnionWith(localExtensions);
					}
				});
		}

		return new ScanResult<HashSet<string>>(extensions, rootAccessDenied == 1, hadAccessDenied == 1);
	}

	public bool CanReadRoot(string rootPath) => _scanner.CanReadRoot(rootPath);
}
