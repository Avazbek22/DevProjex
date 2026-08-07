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
	string? SessionMarkId = null);

public sealed record SecretTextRedactionResult(
	string Text,
	IReadOnlyList<SecretPreviewSpan> Spans,
	int DetectedCount,
	int RedactedCount);

/// <param name="UnscannablePath">
/// One selected text file the scanner was not allowed to read, or null. Documents omit such a file
/// and report a count for everything else; a project copy reproduces bytes and refuses instead, so
/// it is the surface that has to look at this.
/// </param>
public sealed record SecretRedactionSnapshot(
	string SelectionKey,
	int DetectedCount,
	int RedactedCount,
	IReadOnlyDictionary<string, int>? MarkedSecretCounts = null,
	string? UnscannablePath = null);

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
	: Exception($"The text file exceeds the Hide Secrets scan limit: {path}")
{
	public string Path { get; } = path;
	public long SizeBytes { get; } = sizeBytes;
	public long MaximumSizeBytes { get; } = maximumSizeBytes;
}
