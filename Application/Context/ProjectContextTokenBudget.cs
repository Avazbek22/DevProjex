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
	private readonly List<ProjectContextTokenBudgetSkippedFile> _largestSkippedFiles =
		new(MaximumReportedSkippedFiles);
	private long _remainingEstimatedTokens;
	private int _includedFileCount;
	private int _skippedFileCount;
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
		_skippedFileCount++;
		RetainLargestSkippedFile(new ProjectContextTokenBudgetSkippedFile(path, estimatedTokens));
		return false;
	}

	public ProjectContextTokenBudgetReport CreateReport()
	{
		var largestSkippedFiles = _largestSkippedFiles.ToArray();
		return new ProjectContextTokenBudgetReport(
			_maximumEstimatedTokens,
			_includedFileCount,
			_skippedFileCount,
			_includedEstimatedTokens,
			_skippedEstimatedTokens,
			largestSkippedFiles,
			_skippedFileCount - largestSkippedFiles.Length);
	}

	private void RetainLargestSkippedFile(ProjectContextTokenBudgetSkippedFile candidate)
	{
		var insertionIndex = _largestSkippedFiles.BinarySearch(candidate, SkippedFileComparer.Instance);
		if (insertionIndex < 0)
			insertionIndex = ~insertionIndex;
		if (insertionIndex >= MaximumReportedSkippedFiles)
			return;

		_largestSkippedFiles.Insert(insertionIndex, candidate);
		if (_largestSkippedFiles.Count > MaximumReportedSkippedFiles)
			_largestSkippedFiles.RemoveAt(MaximumReportedSkippedFiles);
	}

	private sealed class SkippedFileComparer : IComparer<ProjectContextTokenBudgetSkippedFile>
	{
		public static SkippedFileComparer Instance { get; } = new();

		public int Compare(
			ProjectContextTokenBudgetSkippedFile? left,
			ProjectContextTokenBudgetSkippedFile? right)
		{
			if (ReferenceEquals(left, right))
				return 0;
			if (left is null)
				return 1;
			if (right is null)
				return -1;

			var tokenOrder = right.EstimatedTokens.CompareTo(left.EstimatedTokens);
			return tokenOrder != 0
				? tokenOrder
				: PathComparer.Default.Compare(left.Path, right.Path);
		}
	}
}
