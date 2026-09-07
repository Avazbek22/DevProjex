using DevProjex.Application.Compression;

namespace DevProjex.Application.Context;

public sealed record ProjectContextTokenBudgetSkippedFile(
	string Path,
	long EstimatedTokens,
	int? Priority = null,
	long? RemainingEstimatedTokens = null);

public sealed record ProjectContextTokenBudgetReport(
	long MaximumEstimatedTokens,
	int IncludedFileCount,
	int SkippedFileCount,
	long IncludedEstimatedTokens,
	long SkippedEstimatedTokens,
	IReadOnlyList<ProjectContextTokenBudgetSkippedFile> LargestSkippedFiles,
	int AdditionalSkippedFileCount,
	IReadOnlyList<ProjectContextTokenBudgetSkippedFile>? RankedSkippedFiles = null);

internal sealed class ProjectContextTokenBudgetAccumulator
{
	internal const int MaximumReportedSkippedFiles = 25;
	internal const int MaximumReportedRankedSkippedFiles = 10;
	private readonly long _maximumEstimatedTokens;
	private List<ProjectContextTokenBudgetSkippedFile>? _largestSkippedFiles;
	private List<ProjectContextTokenBudgetSkippedFile>? _rankedSkippedFiles;
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

	public bool TryInclude(string path, int transformedCharacterCount, int? priority = null)
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
		RetainLargestSkippedFile(path, estimatedTokens, priority, _remainingEstimatedTokens);
		if (priority is not null)
			RetainRankedSkippedFile(path, estimatedTokens, priority.Value, _remainingEstimatedTokens);
		return false;
	}

	public ProjectContextTokenBudgetReport CreateReport()
	{
		var largestSkippedFiles = _largestSkippedFiles?.ToArray() ?? [];
		return new ProjectContextTokenBudgetReport(
			_maximumEstimatedTokens,
			_includedFileCount,
			_skippedFileCount,
			_includedEstimatedTokens,
			_skippedEstimatedTokens,
			largestSkippedFiles,
			_skippedFileCount - largestSkippedFiles.Length,
			_rankedSkippedFiles?.ToArray() ?? []);
	}

	private void RetainRankedSkippedFile(
		string path,
		long estimatedTokens,
		int priority,
		long remainingEstimatedTokens)
	{
		var ranked = _rankedSkippedFiles ??=
			new List<ProjectContextTokenBudgetSkippedFile>(MaximumReportedRankedSkippedFiles);
		var index = ranked.FindIndex(item =>
			item.Priority > priority ||
			item.Priority == priority && ProjectTreePathIdentity.CanonicalComparer.Compare(item.Path, path) > 0);
		if (index < 0)
			index = ranked.Count;
		if (index >= MaximumReportedRankedSkippedFiles)
			return;
		ranked.Insert(index, new ProjectContextTokenBudgetSkippedFile(
			path,
			estimatedTokens,
			priority,
			remainingEstimatedTokens));
		if (ranked.Count > MaximumReportedRankedSkippedFiles)
			ranked.RemoveAt(MaximumReportedRankedSkippedFiles);
	}

	private void RetainLargestSkippedFile(
		string path,
		long estimatedTokens,
		int? priority,
		long? remainingEstimatedTokens)
	{
		var largestSkippedFiles = _largestSkippedFiles ??=
			new List<ProjectContextTokenBudgetSkippedFile>(MaximumReportedSkippedFiles);
		var insertionIndex = FindInsertionIndex(largestSkippedFiles, path, estimatedTokens);
		if (insertionIndex >= MaximumReportedSkippedFiles)
			return;

		largestSkippedFiles.Insert(
			insertionIndex,
			new ProjectContextTokenBudgetSkippedFile(path, estimatedTokens, priority, remainingEstimatedTokens));
		if (largestSkippedFiles.Count > MaximumReportedSkippedFiles)
			largestSkippedFiles.RemoveAt(MaximumReportedSkippedFiles);
	}

	private static int FindInsertionIndex(
		IReadOnlyList<ProjectContextTokenBudgetSkippedFile> items,
		string path,
		long estimatedTokens)
	{
		var low = 0;
		var high = items.Count;
		while (low < high)
		{
			var middle = low + (high - low) / 2;
			var item = items[middle];
			var comparison = item.EstimatedTokens != estimatedTokens
				? estimatedTokens.CompareTo(item.EstimatedTokens)
				: ProjectTreePathIdentity.CanonicalComparer.Compare(item.Path, path);
			if (comparison < 0)
				low = middle + 1;
			else
				high = middle;
		}
		return low;
	}
}
