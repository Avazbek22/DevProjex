using System.Collections.Generic;
using System.IO;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel.Contracts;
using DevProjex.Kernel.Models;

namespace DevProjex.Application.UseCases;

public sealed class ScanOptionsUseCase
{
	private readonly IFileSystemScanner _scanner;

	public ScanOptionsUseCase(IFileSystemScanner scanner)
	{
		_scanner = scanner;
	}

	public ScanOptionsResult Execute(ScanOptionsRequest request)
	{
		var extensions = _scanner.GetExtensions(request.RootPath, request.IgnoreRules);
		var rootFolders = _scanner.GetRootFolderNames(request.RootPath, request.IgnoreRules);

		return new ScanOptionsResult(
			Extensions: extensions.Value.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(),
			RootFolders: rootFolders.Value.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(),
			RootAccessDenied: extensions.RootAccessDenied || rootFolders.RootAccessDenied,
			HadAccessDenied: extensions.HadAccessDenied || rootFolders.HadAccessDenied);
	}

	public ScanResult<HashSet<string>> GetExtensionsForRootFolders(
		string rootPath,
		IReadOnlyCollection<string> rootFolders,
		IgnoreRules ignoreRules)
	{
		var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		// Always scan root-level files, even when no subfolders are selected.
		// This ensures folders containing only files (no subdirectories) work correctly.
		var rootFiles = _scanner.GetRootFileExtensions(rootPath, ignoreRules);
		foreach (var ext in rootFiles.Value)
			extensions.Add(ext);

		bool rootAccessDenied = rootFiles.RootAccessDenied;
		bool hadAccessDenied = rootFiles.HadAccessDenied;

		// Scan extensions from selected subfolders
		foreach (var folder in rootFolders)
		{
			var folderPath = Path.Combine(rootPath, folder);
			var result = _scanner.GetExtensions(folderPath, ignoreRules);

			foreach (var ext in result.Value)
				extensions.Add(ext);

			rootAccessDenied |= result.RootAccessDenied;
			hadAccessDenied |= result.HadAccessDenied;
		}

		return new ScanResult<HashSet<string>>(extensions, rootAccessDenied, hadAccessDenied);
	}

	public bool CanReadRoot(string rootPath) => _scanner.CanReadRoot(rootPath);
}
