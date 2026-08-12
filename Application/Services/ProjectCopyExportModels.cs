namespace DevProjex.Application.Services;

public enum ProjectCopyExportFormat
{
	Folder = 0,
	Zip = 1
}

public enum ProjectCopyDestinationMode
{
	AutomaticName = 0,
	Exact = 1
}

public enum ProjectCopyConflictPolicy
{
	Fail = 0,
	ReplaceAtomically = 1
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
	UnexpectedFailure = 9,
	DestinationConflict = 10,
	SecretDetectionFailed = 11,
	SecretScanLimitExceeded = 12
}

public sealed record ProjectCopyExportRequest(
	string ProjectRootPath,
	string ProjectName,
	TreeNodeDescriptor TreeRoot,
	IReadOnlySet<string> SelectedPaths,
	string DestinationPath,
	ProjectCopyExportFormat Format,
	ProjectCopyDestinationMode DestinationMode = ProjectCopyDestinationMode.AutomaticName,
	ProjectCopyConflictPolicy ConflictPolicy = ProjectCopyConflictPolicy.Fail,
	bool RedactSecrets = false,
	bool CompressCode = false,
	bool StripComments = false,
	bool StripBlankLines = false,
	ProjectCopyNoticeText? NoticeText = null);

/// <summary>
/// Localized text for the notice a transformed copy carries in its root. Passed in rather than
/// resolved here so the Application layer keeps no opinion about the user's language.
/// </summary>
/// <param name="ExcludedUnscannable">
/// Header for the list of files left out because Hide Secrets could not read them. The paths
/// follow on their own lines, so the reader can tell an omission from a file that never existed.
/// </param>
public sealed record ProjectCopyNoticeText(
	string Redaction,
	string Compression,
	string ExcludedUnscannable = "");

public sealed record ProjectCopyExportResult(
	string DestinationPath,
	int CopiedFileCount,
	int CreatedDirectoryCount,
	long BytesWritten,
	int RedactedValueCount = 0);

public sealed record ProjectCopyExportProgress(
	int ProcessedEntryCount,
	int TotalEntryCount,
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
	Exception? innerException = null,
	string? pathContext = null)
	: Exception(message, innerException)
{
	public ProjectCopyExportError Error { get; } = error;
	public string? PathContext { get; } = pathContext;
}
