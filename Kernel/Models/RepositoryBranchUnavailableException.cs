namespace DevProjex.Kernel.Models;

public enum RepositoryBranchUnavailableReason
{
	NotFound = 0,
	WorktreeUnsupported = 1,
	RevisionMismatch = 2
}

public sealed class RepositoryBranchUnavailableException : IOException
{
	public RepositoryBranchUnavailableException(
		string branch,
		RepositoryBranchUnavailableReason reason)
		: base(CreateMessage(branch, reason))
	{
		Branch = branch;
		Reason = reason;
	}

	public string Branch { get; }
	public RepositoryBranchUnavailableReason Reason { get; }

	private static string CreateMessage(string branch, RepositoryBranchUnavailableReason reason) => reason switch
	{
		RepositoryBranchUnavailableReason.NotFound => $"Git branch '{branch}' was not found.",
		RepositoryBranchUnavailableReason.WorktreeUnsupported =>
			$"Git branch switching is unavailable for '{branch}'.",
		RepositoryBranchUnavailableReason.RevisionMismatch =>
			$"Git branch '{branch}' resolved to an unexpected revision.",
		_ => $"Git branch '{branch}' is unavailable."
	};
}
