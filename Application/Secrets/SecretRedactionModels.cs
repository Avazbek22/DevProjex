using DevProjex.Application.Compression;

namespace DevProjex.Application.Secrets;

public enum SecretPreviewSpanState
{
	Redacted = 0,
	KeptAsIs = 1
}

public sealed record SecretPreviewSpan(
	string OccurrenceId,
	string RuleId,
	int Start,
	int Length,
	SecretPreviewSpanState State,
	int SourceLength = 0,
	SecretFindingSource Source = SecretFindingSource.Detector,
	string? PersistentMarkHash = null,
	string? SessionMarkId = null,
	PersistentSecretMarkId? PersistentMarkId = null,
	string RelativePath = "");

public sealed record SecretTextRedactionResult(
	string Text,
	IReadOnlyList<SecretPreviewSpan> Spans,
	int DetectedCount,
	int RedactedCount,
	ContentTransformMap CoordinateMap);

public sealed record UnscannableFile(
	string Path,
	FileContentClassification Classification);

/// <param name="UnscannablePath">
/// One selected text file the scanner was not allowed to read, or null. Documents omit such a file
/// and report a count for everything else; a project copy leaves it out and names it. The dry run
/// reads this to say the same thing before anything is written.
/// </param>
/// <param name="SkippedFileCount">
/// Selected text files withheld because bounded inspection could not decode or fully read them.
/// </param>
/// <param name="FailedFileCount">
/// Selected files discovery could not inspect because reading, decoding, or detection failed.
/// </param>
public sealed record SecretRedactionSnapshot(
	string SelectionKey,
	int DetectedCount,
	int RedactedCount,
	IReadOnlyDictionary<string, int>? MarkedSecretCounts = null,
	string? UnscannablePath = null,
	int SkippedFileCount = 0,
	int FailedFileCount = 0,
	int PrivateDataDetectedCount = 0,
	int PrivateDataRedactedCount = 0)
{
	public IReadOnlyList<UnscannableFile> UnscannableFiles { get; init; } = [];
	public int SecretDetectedCount => checked(DetectedCount - PrivateDataDetectedCount);
	public int SecretRedactedCount => checked(RedactedCount - PrivateDataRedactedCount);
	public int IncompleteFileCount => checked(SkippedFileCount + FailedFileCount);
	public bool IsComplete => IncompleteFileCount == 0;
	public bool HasFailures => FailedFileCount > 0;
	public bool HasLimitedCoverage => SkippedFileCount > 0;
}

public enum SecretDiscoveryCacheMode
{
	/// <summary>Reopens selected files and verifies their content fingerprints.</summary>
	RevalidateContent = 0,
	/// <summary>
	/// Reuses previously verified findings while file metadata and all rule identities remain equal.
	/// Intended for selection-only UI changes; output generation must use content revalidation.
	/// </summary>
	ReuseValidatedContent = 1
}

public readonly record struct ManualSecretMarkRemovalResult(
	bool PersistentMarkRemoved,
	bool SessionMarkRemoved)
{
	public bool Removed => PersistentMarkRemoved || SessionMarkRemoved;
}

public sealed class SecretScanLimitExceededException(
	string path,
	long sizeBytes,
	long maximumSizeBytes)
	: Exception($"The text file exceeds the content-redaction scan limit: {path}")
{
	public string Path { get; } = path;
	public long SizeBytes { get; } = sizeBytes;
	public long MaximumSizeBytes { get; } = maximumSizeBytes;
}
