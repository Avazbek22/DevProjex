using DevProjex.Application.Compression;
using DevProjex.Application.Secrets;

namespace DevProjex.Application.Services;

public readonly record struct TransformedFileContentResult(
	FileContentClassification Classification,
	string? Content)
{
	public bool HasText => Classification == FileContentClassification.Text && Content is not null;
}

public sealed class TransformedFileContentReader(
	IFileContentAnalyzer contentAnalyzer,
	SecretRedactionOutputPreparer outputPreparer)
{
	public async Task<TransformedFileContentResult> ReadAsync(
		string projectRoot,
		string path,
		ContentTransformationContext? transformationContext,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ArgumentException.ThrowIfNullOrWhiteSpace(path);

		if (!HasMatchingProjectRoot(projectRoot, transformationContext))
			return new TransformedFileContentResult(FileContentClassification.Unreadable, null);

		var sourceClassification = ValidateSourcePath(projectRoot, path);
		if (sourceClassification is { } unavailable)
			return new TransformedFileContentResult(unavailable, null);

		if (transformationContext is null)
		{
			var content = await contentAnalyzer
				.TryReadAsTextAsync(path, cancellationToken)
				.ConfigureAwait(false);
			return content is null
				? new TransformedFileContentResult(FileContentClassification.Binary, null)
				: new TransformedFileContentResult(FileContentClassification.Text, content.Content);
		}

		await using var prepared = await outputPreparer
			.PrepareAsync(transformationContext, [path], cancellationToken)
			.ConfigureAwait(false);
		var preparedFile = prepared.GetFile(path);
		if (!preparedFile.IsText || preparedFile.IsUnscannable)
			return new TransformedFileContentResult(preparedFile.Classification, null);

		var preparedContent = await outputPreparer
			.CreatePreparedAnalyzer(prepared)
			.TryReadAsTextAsync(path, cancellationToken)
			.ConfigureAwait(false);
		return preparedContent is null
			? new TransformedFileContentResult(FileContentClassification.Binary, null)
			: new TransformedFileContentResult(FileContentClassification.Text, preparedContent.Content);
	}

	private static bool HasMatchingProjectRoot(
		string projectRoot,
		ContentTransformationContext? transformationContext)
	{
		try
		{
			var normalizedRoot = PathUtility.Normalize(projectRoot);
			return (transformationContext?.Compression is not { } compression ||
			        PathComparer.Default.Equals(normalizedRoot, PathUtility.Normalize(compression.ProjectRoot))) &&
			       (transformationContext?.Redaction is not { } redaction ||
			        PathComparer.Default.Equals(normalizedRoot, PathUtility.Normalize(redaction.ProjectRoot)));
		}
		catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
		{
			return false;
		}
	}

	private static FileContentClassification? ValidateSourcePath(string projectRoot, string path)
	{
		try
		{
			var normalizedRoot = PathUtility.Normalize(projectRoot);
			var normalizedPath = PathUtility.Normalize(path);
			var relativePath = Path.GetRelativePath(normalizedRoot, normalizedPath);
			if (IsOutsideRoot(relativePath))
				return FileContentClassification.Unreadable;

			var currentPath = normalizedRoot;
			if (IsReparsePoint(currentPath))
				return FileContentClassification.Unreadable;

			foreach (var segment in relativePath.Split(
			         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
			         StringSplitOptions.RemoveEmptyEntries))
			{
				currentPath = Path.Combine(currentPath, segment);
				if (IsReparsePoint(currentPath))
					return FileContentClassification.Unreadable;
			}

			return null;
		}
		catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
		{
			return FileContentClassification.Missing;
		}
		catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
		{
			return FileContentClassification.AccessDenied;
		}
		catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
		{
			return FileContentClassification.Unreadable;
		}
	}

	private static bool IsOutsideRoot(string relativePath) =>
		PathUtility.IsRelativePathOutsideRoot(relativePath);

	private static bool IsReparsePoint(string path) =>
		(File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
