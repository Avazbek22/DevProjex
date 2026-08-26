namespace DevProjex.Kernel.Models;

public sealed record ProjectProfileSaveRequest(
	string LocalProjectPath,
	ProjectSelectionProfile Profile,
	DateTimeOffset UpdatedUtc);

public sealed record ProjectProfileBatchSaveResult(
	IReadOnlyList<string> SavedProjectPaths);
