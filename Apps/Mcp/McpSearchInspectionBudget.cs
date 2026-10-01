namespace DevProjex.Mcp;

/// <summary>
/// Chooses the selected sources a search inspects under its cumulative text budget. The budget
/// bounds the text a search decodes, so only a source that would be decoded pays for it: a source
/// whose metadata or leading bytes already prove it is binary, or that the pipeline will not decode
/// at all, is admitted free and reported by the pipeline as skipped or unscannable. A source that
/// does not fit what is left is skipped rather than ending admission, so a later source that fits is
/// still searched, and any skip makes the boundary report the inspection-bytes limit.
/// </summary>
internal static class McpSearchInspectionBudget
{
	// Only a selection larger than the budget pays for classification, and only a source at least
	// this large pays for opening it: that is where a binary asset without a known extension would
	// otherwise spend the budget. A smaller source is charged by its size.
	internal const long ContentProbeMinimumBytes = 64 * 1024;

	/// <param name="resolveSize">The source size in bytes, or null when it cannot be bounded.</param>
	/// <param name="isDecodedAsText">
	/// Whether inspection would decode the source as text, given its size and whether its leading
	/// bytes may be read to decide.
	/// </param>
	public static McpSearchInspectionAdmission Admit(
		IReadOnlyList<string> selectedFiles,
		Func<string, long?> resolveSize,
		Func<string, long, bool, bool> isDecodedAsText,
		long maximumBytes,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(selectedFiles);
		ArgumentNullException.ThrowIfNull(resolveSize);
		ArgumentNullException.ThrowIfNull(isDecodedAsText);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

		var sizes = new long?[selectedFiles.Count];
		var selectionFits = true;
		long selectedBytes = 0;
		for (var index = 0; index < selectedFiles.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var size = resolveSize(selectedFiles[index]);
			sizes[index] = size is >= 0 ? size : null;
			if (sizes[index] is not { } known || known > maximumBytes - selectedBytes)
			{
				selectionFits = false;
				continue;
			}
			selectedBytes += known;
		}
		// The common case costs no classification at all: everything fits even when counted whole.
		if (selectionFits)
			return new McpSearchInspectionAdmission(selectedFiles, BudgetReached: false);

		var admitted = new List<string>(selectedFiles.Count);
		var budgetReached = false;
		long chargedBytes = 0;
		for (var index = 0; index < selectedFiles.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var path = selectedFiles[index];
			if (sizes[index] is not { } size)
			{
				budgetReached = true;
				continue;
			}
			var charge = size > 0 && isDecodedAsText(path, size, size >= ContentProbeMinimumBytes)
				? size
				: 0;
			if (charge > maximumBytes - chargedBytes)
			{
				budgetReached = true;
				continue;
			}
			admitted.Add(path);
			chargedBytes += charge;
		}
		return new McpSearchInspectionAdmission(admitted, budgetReached);
	}
}

/// <param name="Files">The admitted sources in selection order.</param>
/// <param name="BudgetReached">True when at least one selected source was skipped for the budget.</param>
internal sealed record McpSearchInspectionAdmission(IReadOnlyList<string> Files, bool BudgetReached);
