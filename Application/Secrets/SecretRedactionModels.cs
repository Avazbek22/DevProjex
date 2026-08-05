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
	SecretPreviewSpanState State);

public sealed record SecretTextRedactionResult(
	string Text,
	IReadOnlyList<SecretPreviewSpan> Spans,
	int DetectedCount,
	int RedactedCount);

public sealed record SecretRedactionSnapshot(
	string SelectionKey,
	int DetectedCount,
	int RedactedCount);

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
