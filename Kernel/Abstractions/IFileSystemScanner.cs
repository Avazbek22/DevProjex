namespace DevProjex.Kernel.Abstractions;

public interface IFileSystemScanner
{
	bool CanReadRoot(string rootPath);
	ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default);
	ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default);
	ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default);
}
