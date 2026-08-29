using DevProjex.Application.Compression;

namespace DevProjex.Application.Context;

public sealed record ProjectContextTokenBudgetSkippedFile(
	string Path,
	long EstimatedTokens);

public sealed record ProjectContextTokenBudgetReport(
	long MaximumEstimatedTokens,
	int IncludedFileCount,
	int SkippedFileCount,
	long IncludedEstimatedTokens,
	long SkippedEstimatedTokens,
	IReadOnlyList<ProjectContextTokenBudgetSkippedFile> LargestSkippedFiles,
	int AdditionalSkippedFileCount);

internal sealed class ProjectContextTokenBudgetAccumulator
{
	internal const int MaximumReportedSkippedFiles = 25;
	private readonly long _maximumEstimatedTokens;
	private readonly List<ProjectContextTokenBudgetSkippedFile> _skippedFiles = [];
	private long _remainingEstimatedTokens;
	private int _includedFileCount;
	private long _includedEstimatedTokens;
	private long _skippedEstimatedTokens;

	public ProjectContextTokenBudgetAccumulator(long maximumEstimatedTokens)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(maximumEstimatedTokens, 1);
		_maximumEstimatedTokens = maximumEstimatedTokens;
		_remainingEstimatedTokens = maximumEstimatedTokens;
	}

	public bool TryInclude(string path, int transformedCharacterCount)
	{
		ArgumentNullException.ThrowIfNull(path);
		var estimatedTokens = CodeCompressionSnapshot.EstimateTokens(
			Math.Max(0, transformedCharacterCount));
		if (estimatedTokens <= _remainingEstimatedTokens)
		{
			_remainingEstimatedTokens -= estimatedTokens;
			_includedFileCount++;
			_includedEstimatedTokens += estimatedTokens;
			return true;
		}

		_skippedEstimatedTokens += estimatedTokens;
		_skippedFiles.Add(new ProjectContextTokenBudgetSkippedFile(path, estimatedTokens));
		return false;
	}

	public ProjectContextTokenBudgetReport CreateReport()
	{
		var largestSkippedFiles = _skippedFiles
			.OrderByDescending(static file => file.EstimatedTokens)
			.ThenBy(static file => file.Path, PathComparer.Default)
			.Take(MaximumReportedSkippedFiles)
			.ToArray();
		return new ProjectContextTokenBudgetReport(
			_maximumEstimatedTokens,
			_includedFileCount,
			_skippedFiles.Count,
			_includedEstimatedTokens,
			_skippedEstimatedTokens,
			largestSkippedFiles,
			_skippedFiles.Count - largestSkippedFiles.Length);
	}
}
