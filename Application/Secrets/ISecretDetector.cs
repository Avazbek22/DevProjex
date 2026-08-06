namespace DevProjex.Application.Secrets;

/// <summary>
/// Detects credential values in already decoded text. Implementations must not read files:
/// classification and source IO remain owned by <see cref="IFileContentAnalyzer"/>.
/// </summary>
public interface ISecretDetector
{
	/// <summary>
	/// Identifies the exact rule set used to produce findings. Cache entries must never cross
	/// this boundary because equal file bytes can produce different findings after a rule update.
	/// </summary>
	string RulesIdentity => GetType().FullName ?? nameof(ISecretDetector);

	/// <summary>
	/// Performs detector-only initialization before source buffers enter the pipeline. The
	/// default is intentionally free so lightweight and test detectors pay no startup cost.
	/// </summary>
	void WarmUp(CancellationToken cancellationToken = default)
	{
	}

	ISecretDetectionScope CreateScope(string projectRoot) =>
		new UnscopedSecretDetectionScope(this);

	IReadOnlyList<DetectedSecret> Detect(
		string repositoryRelativePath,
		string content,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Detects directly over an operation-owned buffer. Production detectors should override
	/// this overload so count-only scans do not manufacture a full-file string per source file.
	/// </summary>
	IReadOnlyList<DetectedSecret> Detect(
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		CancellationToken cancellationToken = default) =>
		Detect(repositoryRelativePath, content.ToString(), cancellationToken);
}

public interface ISecretDetectionScope
{
	string GetRulesIdentity(string fullPath, string repositoryRelativePath);

	IReadOnlyList<DetectedSecret> Detect(
		string fullPath,
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		CancellationToken cancellationToken = default);
}

internal sealed class UnscopedSecretDetectionScope(ISecretDetector detector) : ISecretDetectionScope
{
	public string GetRulesIdentity(string fullPath, string repositoryRelativePath) =>
		detector.RulesIdentity;

	public IReadOnlyList<DetectedSecret> Detect(
		string fullPath,
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		CancellationToken cancellationToken = default) =>
		detector.Detect(repositoryRelativePath, content, cancellationToken);
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
	int RuleOrder,
	SecretFindingSource Source = SecretFindingSource.Detector,
	string? PersistentMarkHash = null);

[Flags]
public enum SecretFindingSource
{
	Detector = 1,
	PersistentMark = 2,
	SessionMark = 4
}

public sealed class SecretDetectionException(
	string message,
	Exception? innerException = null)
	: Exception(message, innerException);
