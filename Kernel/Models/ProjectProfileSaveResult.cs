namespace DevProjex.Kernel.Models;

public enum ProjectProfileSaveStatus
{
	Saved = 0,
	Failed = 1,
	Truncated = 2,
	Conflict = 3
}

public readonly record struct ProjectProfileSaveResult
{
	public ProjectProfileSaveResult(bool Succeeded, bool WasTruncated)
	{
		this.Succeeded = Succeeded;
		this.WasTruncated = WasTruncated;
		Status = Succeeded
			? ProjectProfileSaveStatus.Saved
			: WasTruncated
				? ProjectProfileSaveStatus.Truncated
				: ProjectProfileSaveStatus.Failed;
	}

	public ProjectProfileSaveResult(ProjectProfileSaveStatus status)
	{
		Status = status;
		Succeeded = status == ProjectProfileSaveStatus.Saved;
		WasTruncated = status == ProjectProfileSaveStatus.Truncated;
	}

	public bool Succeeded { get; }
	public bool WasTruncated { get; }
	public ProjectProfileSaveStatus Status { get; }
}
