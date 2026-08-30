namespace DevProjex.Kernel.Contracts;

public sealed record ScanOptionsResult(
	IReadOnlyList<string> Extensions,
	IReadOnlyList<string> RootFolders,
	bool RootAccessDenied,
	bool HadAccessDenied,
	bool HadScanFailure = false,
	GitWorkspaceEvidence GitEvidence = default);
