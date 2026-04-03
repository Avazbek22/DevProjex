namespace DevProjex.Kernel;

public interface IFileSystemScannerEffectiveIgnoreCountsProvider
{
	ScanResult<IgnoreOptionCounts> GetEffectiveIgnoreOptionCounts(
		string rootPath,
		IReadOnlySet<string> allowedExtensions,
		IgnoreRules rules,
		CancellationToken cancellationToken = default);

	ScanResult<IgnoreOptionCounts> GetEffectiveRootFileIgnoreOptionCounts(
		string rootPath,
		IReadOnlySet<string> allowedExtensions,
		IgnoreRules rules,
		CancellationToken cancellationToken = default);
}
