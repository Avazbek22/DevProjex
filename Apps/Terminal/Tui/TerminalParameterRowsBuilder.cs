using DevProjex.Application.Secrets;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalParameterRowsBuilder(
	Func<string, string> localize,
	Func<string, string> fitLabel,
	Func<IgnoreOptionId, SecretScanState, int?, int?, string> formatRedactionLabel)
{
	public IReadOnlyList<TerminalParameterRow> BuildContent(
		ProjectContextPlan plan,
		SecretRedactionSnapshot? snapshot,
		ProjectSelectionSpec? selectionOverride = null)
	{
		ArgumentNullException.ThrowIfNull(plan);
		var selection = selectionOverride ?? plan.Selection;
		return ProjectPresentationCatalog.ContentTransformations.Select(descriptor =>
			new TerminalParameterRow(
				$"content:{descriptor.Token}",
				TerminalParameterRowKind.ContentTransformation,
				fitLabel(FormatContentTransformationLabel(descriptor, selection, snapshot)),
				IsContentTransformationEnabled(selection, descriptor.LegacyOptionId),
				ContentTransformation: descriptor.LegacyOptionId)).ToArray();
	}

	public TerminalParameterRow BuildContentAggregate(ProjectSelectionSpec selection)
	{
		ArgumentNullException.ThrowIfNull(selection);
		var count = ProjectPresentationCatalog.ContentTransformations.Count;
		return new TerminalParameterRow(
			"content:all",
			TerminalParameterRowKind.ToggleAllContent,
			FormatAggregateLabel(count),
			count > 0 && ProjectPresentationCatalog.ContentTransformations.All(descriptor =>
				IsContentTransformationEnabled(selection, descriptor.LegacyOptionId)));
	}

	public IReadOnlyList<TerminalParameterRow> BuildExclusions(
		ProjectContextPlan plan,
		ProjectSelectionSpec? selectionOverride = null)
	{
		ArgumentNullException.ThrowIfNull(plan);
		var selection = selectionOverride ?? plan.Selection;
		var exclusions = (selection.Exclusions ?? []).ToHashSet();
		var rows = new List<TerminalParameterRow>();
		rows.AddRange(ProjectPresentationCatalog.GitFiltering
			.Where(static descriptor => descriptor.Id != GitFilteringMode.None)
			.Select(descriptor => new TerminalParameterRow(
				$"git:{descriptor.Token}",
				TerminalParameterRowKind.GitMode,
				fitLabel(localize(descriptor.LabelKey)),
				(selection.GitMode ?? plan.GitReadiness.Mode) == descriptor.Id,
				GitMode: descriptor.Id)));
		rows.AddRange(ProjectPresentationCatalog.Exclusions.Select(descriptor =>
			new TerminalParameterRow(
				$"exclusion:{descriptor.Token}",
				TerminalParameterRowKind.Exclusion,
				fitLabel(localize(descriptor.LabelKey)),
				exclusions.Contains(descriptor.RequireId()),
				Exclusion: descriptor.RequireId())));
		return rows;
	}

	public TerminalParameterRow BuildExclusionAggregate(
		ProjectContextPlan plan,
		ProjectSelectionSpec? selectionOverride = null)
	{
		ArgumentNullException.ThrowIfNull(plan);
		var selection = selectionOverride ?? plan.Selection;
		var exclusions = (selection.Exclusions ?? []).ToHashSet();
		var count = ProjectPresentationCatalog.GitFiltering.Count - 1 +
		            ProjectPresentationCatalog.Exclusions.Count;
		return new TerminalParameterRow(
			"exclusions:all",
			TerminalParameterRowKind.ToggleAllExclusions,
			FormatAggregateLabel(count),
			(selection.GitMode ?? plan.GitReadiness.Mode) != GitFilteringMode.None &&
			ProjectPresentationCatalog.Exclusions.All(descriptor =>
				exclusions.Contains(descriptor.RequireId())));
	}

	public IReadOnlyList<TerminalParameterRow> BuildExtensions(
		ProjectContextPlan plan,
		IReadOnlyCollection<string>? selectedExtensionsOverride = null)
	{
		ArgumentNullException.ThrowIfNull(plan);
		var selectedExtensions = (selectedExtensionsOverride ?? plan.SelectedExtensions)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		return plan.AvailableExtensions.Select(extension =>
			new TerminalParameterRow(
				$"extension:{extension}",
				TerminalParameterRowKind.Extension,
				fitLabel(extension),
				selectedExtensions.Contains(extension),
				Value: extension)).ToArray();
	}

	public TerminalParameterRow BuildExtensionAggregate(
		ProjectContextPlan plan,
		IReadOnlyCollection<string>? selectedExtensionsOverride = null)
	{
		ArgumentNullException.ThrowIfNull(plan);
		var selectedExtensions = (selectedExtensionsOverride ?? plan.SelectedExtensions)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		return new TerminalParameterRow(
			"extensions:all",
			TerminalParameterRowKind.ToggleAllExtensions,
			FormatAggregateLabel(plan.AvailableExtensions.Count),
			plan.AvailableExtensions.All(selectedExtensions.Contains));
	}

	internal static bool IsContentTransformationEnabled(
		ProjectSelectionSpec selection,
		IgnoreOptionId optionId) =>
		optionId switch
		{
			IgnoreOptionId.HideSecrets => selection.HideSecrets == true,
			IgnoreOptionId.HidePrivateData => selection.HidePrivateData == true,
			IgnoreOptionId.CompressCode => selection.CompressCode == true,
			IgnoreOptionId.StripComments => selection.StripComments == true,
			IgnoreOptionId.StripBlankLines => selection.StripBlankLines == true,
			_ => throw new ArgumentOutOfRangeException(nameof(optionId), optionId, null)
		};

	private string FormatContentTransformationLabel(
		ProjectExclusionDescriptor descriptor,
		ProjectSelectionSpec selection,
		SecretRedactionSnapshot? snapshot)
	{
		if (snapshot is null ||
			!IsContentTransformationEnabled(selection, descriptor.LegacyOptionId))
		{
			return localize(descriptor.LabelKey);
		}
		var state = snapshot.IsComplete ? SecretScanState.Completed : SecretScanState.Limited;
		return descriptor.LegacyOptionId switch
		{
			IgnoreOptionId.HideSecrets => formatRedactionLabel(
				IgnoreOptionId.HideSecrets,
				state,
				snapshot.SecretDetectedCount,
				snapshot.SecretRedactedCount),
			IgnoreOptionId.HidePrivateData => formatRedactionLabel(
				IgnoreOptionId.HidePrivateData,
				state,
				snapshot.PrivateDataDetectedCount,
				snapshot.PrivateDataRedactedCount),
			_ => localize(descriptor.LabelKey)
		};
	}

	private string FormatAggregateLabel(int count) => count == 0
		? localize("Settings.All")
		: $"{localize("Settings.All")} ({count:N0})";
}
