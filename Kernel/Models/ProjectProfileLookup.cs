namespace DevProjex.Kernel.Models;

public enum ProjectProfileLookupStatus
{
	Found = 0,
	Missing = 1,
	TemporarilyUnavailable = 2,
	InvalidStorage = 3,
	InvalidProjectPath = 4
}

public sealed record ProjectProfileLookupResult(
	ProjectProfileLookupStatus Status,
	ProjectSelectionProfile? Profile);
