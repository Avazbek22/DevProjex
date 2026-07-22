namespace DevProjex.Application.Services;

public enum ProjectCopyExportFormat
{
	Folder = 0,
	Zip = 1
}

public enum ProjectCopyExportError
{
	InvalidRequest = 0,
	DestinationInsideSource = 1,
	UnsafeSourcePath = 2,
	SymbolicLinkNotSupported = 3,
	DestinationUnavailable = 4,
	SourceUnavailable = 5,
	AccessDenied = 6,
	IoFailure = 7,
	UnsafeDestinationPath = 8,
	UnexpectedFailure = 9
}

public sealed record ProjectCopyExportRequest(
	string ProjectRootPath,
	string ProjectName,
	TreeNodeDescriptor TreeRoot,
	IReadOnlySet<string> SelectedPaths,
	string DestinationPath,
	ProjectCopyExportFormat Format);

public sealed record ProjectCopyExportResult(
	string DestinationPath,
	int CopiedFileCount,
	int CreatedDirectoryCount,
	long BytesWritten);

public sealed record ProjectCopyExportProgress(
	int ProcessedFileCount,
	int TotalFileCount,
	long BytesWritten,
	double Percentage);

public sealed record ProjectCopyExportPlanEntry(
	string SourcePath,
	string RelativePath,
	bool IsDirectory);

public sealed record ProjectCopyExportPlan(
	string ProjectRootPath,
	string ProjectName,
	IReadOnlyList<ProjectCopyExportPlanEntry> Entries)
{
	public int FileCount => Entries.Count(static entry => !entry.IsDirectory);
	public int DirectoryCount => Entries.Count(static entry => entry.IsDirectory);
}

public sealed class ProjectCopyExportException(
	ProjectCopyExportError error,
	string message,
	Exception? innerException = null)
	: Exception(message, innerException)
{
	public ProjectCopyExportError Error { get; } = error;
}
