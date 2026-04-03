namespace DevProjex.Kernel.Abstractions;

public interface IFileSystemScannerEffectiveEmptyFolderCounter
{
	ScanResult<int> GetEffectiveEmptyFolderCount(
		string rootPath,
		IReadOnlySet<string> allowedExtensions,
		IgnoreRules rules,
		CancellationToken cancellationToken = default);
}
