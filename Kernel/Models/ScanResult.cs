namespace DevProjex.Kernel.Models;

public sealed record ScanResult<T>(
	T Value,
	bool RootAccessDenied,
	bool HadAccessDenied,
	bool HadScanFailure = false);
