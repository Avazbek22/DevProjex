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
		string path,
		ContentTransformationContext? transformationContext,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);

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
}
