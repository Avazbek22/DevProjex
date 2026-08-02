namespace DevProjex.Kernel.Models;

/// <summary>
/// Structural Git facts observed by the canonical workspace scan.
/// </summary>
public readonly record struct GitWorkspaceEvidence(bool HasRepositoryBoundary)
{
	public static readonly GitWorkspaceEvidence Empty = default;

	public GitWorkspaceEvidence Add(in GitWorkspaceEvidence other) =>
		new(HasRepositoryBoundary || other.HasRepositoryBoundary);
}
