using DevProjex.Application.Secrets;

namespace DevProjex.Application.Compression;

/// <summary>
/// The enabled content transformations for one output operation, as ONE ordered pipeline rather
/// than two independent optional parameters.
///
/// Order is the whole point and it is fixed here so no call site can get it wrong: syntax edits
/// (body compression and/or comment removal) first, secret redaction second. Secrets must run on
/// the text that actually leaves the
/// application - a value inside a body that was removed never ships and must not be counted, while
/// a value in a constant, an attribute or a default argument does ship and must be hidden.
///
/// A null context is the fast path: nothing is loaded and output is byte-for-byte unchanged.
/// </summary>
public sealed record ContentTransformationContext(
	CodeCompressionContext? Compression,
	SecretRedactionContext? Redaction)
{
	public static ContentTransformationContext? For(
		CodeCompressionContext? compression,
		SecretRedactionContext? redaction) =>
		compression is null && redaction is null ? null : new ContentTransformationContext(compression, redaction);

	/// <summary>Convenience for the many call sites that only ever had redaction.</summary>
	public static implicit operator ContentTransformationContext?(SecretRedactionContext? redaction) =>
		redaction is null ? null : new ContentTransformationContext(null, redaction);

	public bool HasCompression => Compression is not null;

	public bool HasRedaction => Redaction is not null;

	public ContentTransformationScope BeginOutput(IReadOnlyList<string> orderedFilePaths)
	{
		var projectRoot = Compression?.ProjectRoot ?? Redaction?.ProjectRoot ??
			throw new InvalidOperationException("A transformation context has no project root.");
		return BeginOutput(ContentSelectionSnapshot.Create(projectRoot, orderedFilePaths));
	}

	public ContentTransformationScope BeginOutput(
		IReadOnlyList<string> orderedFilePaths,
		CancellationToken cancellationToken)
	{
		var projectRoot = Compression?.ProjectRoot ?? Redaction?.ProjectRoot ??
			throw new InvalidOperationException("A transformation context has no project root.");
		return BeginOutput(ContentSelectionSnapshot.CreateWithCancellation(
			projectRoot,
			orderedFilePaths,
			cancellationToken));
	}

	public ContentTransformationScope BeginOutput(ContentSelectionSnapshot selection) =>
		new(
			Compression?.BeginOutput(selection),
			// The redaction cache is keyed on the text that was scanned, and compression decides what
			// that text is. Without this, toggling the checkbox would reuse offsets from the other one.
			Redaction?.BeginOutput(selection, Compression?.TransformIdentity ?? string.Empty));
}

/// <summary>
/// Applies the pipeline to one file. Callers get the final text plus the map back to the original,
/// so anything recorded against source coordinates can still be located afterwards.
/// </summary>
public sealed class ContentTransformationScope(
	CodeCompressionScope? compression,
	SecretRedactionScope? redaction) : IDisposable
{
	public CodeCompressionScope? Compression => compression;

	public SecretRedactionScope? Redaction => redaction;

	/// <summary>
	/// Compresses the file if compression is enabled, and returns the text the redaction stage must
	/// be given. Redaction stays with the caller because its two consumers need different shapes -
	/// a materialized string for the preview, a streamed plan for the clipboard.
	/// </summary>
	public CodeCompressionResult Compress(
		string fullPath,
		string relativePath,
		string content,
		CancellationToken cancellationToken) =>
		compression is null
			? new CodeCompressionResult(content, ContentTransformMap.Identity)
			: compression.Transform(fullPath, relativePath, content, cancellationToken);

	public CodeCompressionResult Compress(
		string fullPath,
		string relativePath,
		string content,
		ContentFingerprint fingerprint,
		CancellationToken cancellationToken) =>
		compression is null
			? new CodeCompressionResult(content, ContentTransformMap.Identity)
			: compression.Transform(fullPath, relativePath, content, fingerprint, cancellationToken);

	public void Dispose()
	{
		compression?.Dispose();
	}
}
