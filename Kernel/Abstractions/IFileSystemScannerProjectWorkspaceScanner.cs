namespace DevProjex.Kernel.Abstractions;

/// <summary>
/// Provides one canonical project scan product for both live option state and optional
/// tree projection. Older granular scanner interfaces remain adapters around this shape.
/// </summary>
public interface IFileSystemScannerProjectWorkspaceScanner
{
	ScanResult<ProjectWorkspaceScanSnapshot> ScanProjectWorkspace(
		ProjectWorkspaceScanRequest request,
		CancellationToken cancellationToken = default);
}
