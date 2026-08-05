namespace DevProjex.Application.Secrets;

/// <summary>
/// Detects credential values in already decoded text. Implementations must not read files:
/// classification and source IO remain owned by <see cref="IFileContentAnalyzer"/>.
/// </summary>
public interface ISecretDetector
{
	IReadOnlyList<DetectedSecret> Detect(
		string repositoryRelativePath,
		string content,
		CancellationToken cancellationToken = default);
}

/// <summary>
/// A match identifies only the value that may be replaced, never the surrounding assignment.
/// The value is intentionally short-lived and must not be persisted or logged.
/// </summary>
public sealed record DetectedSecret(
	string RuleId,
	int Start,
	int Length,
	string Value,
	int RuleOrder);

public sealed class SecretDetectionException(
	string message,
	Exception? innerException = null)
	: Exception(message, innerException);
