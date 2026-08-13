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

	/// <summary>
	/// Returns whether automatic detection needs the file content. Implementations may use only
	/// path-level policy here; source IO remains owned by the application pipeline.
	/// </summary>
	bool ShouldInspectPath(string repositoryRelativePath) => true;

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

	IReadOnlyList<DetectedSecret> Detect(
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(budget);
		var findings = Detect(repositoryRelativePath, content, cancellationToken);
		budget.RegisterFindings(findings.Count, cancellationToken);
		return findings;
	}
}

public interface ISecretDetectionScope
{
	string GetRulesIdentity(string fullPath, string repositoryRelativePath);

	bool ShouldInspectPath(string fullPath, string repositoryRelativePath) => true;

	IReadOnlyList<DetectedSecret> Detect(
		string fullPath,
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		CancellationToken cancellationToken = default);

	IReadOnlyList<DetectedSecret> Detect(
		string fullPath,
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(budget);
		var findings = Detect(fullPath, repositoryRelativePath, content, cancellationToken);
		budget.RegisterFindings(findings.Count, cancellationToken);
		return findings;
	}
}

internal sealed class UnscopedSecretDetectionScope(ISecretDetector detector) : ISecretDetectionScope
{
	public string GetRulesIdentity(string fullPath, string repositoryRelativePath) =>
		detector.RulesIdentity;

	public bool ShouldInspectPath(string fullPath, string repositoryRelativePath) =>
		detector.ShouldInspectPath(repositoryRelativePath);

	public IReadOnlyList<DetectedSecret> Detect(
		string fullPath,
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		CancellationToken cancellationToken = default) =>
		detector.Detect(repositoryRelativePath, content, cancellationToken);

	public IReadOnlyList<DetectedSecret> Detect(
		string fullPath,
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken = default) =>
		detector.Detect(repositoryRelativePath, content, budget, cancellationToken);
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
	string? PersistentMarkHash = null,
	string? SessionMarkId = null);

[Flags]
public enum SecretFindingSource
{
	Detector = 1,
	PersistentMark = 2,
	SessionMark = 4
}

public class SecretDetectionException(
	string message,
	Exception? innerException = null)
	: Exception(message, innerException);
