using DevProjex.Application.Secrets;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalParameterRowsBuilder(
	Func<string, string> localize,
	Func<string, string> fitLabel,
	Func<string, string> fitInformationLabel,
	Func<IgnoreOptionId, SecretScanState, int?, int?, string> formatRedactionLabel)
{
	public IReadOnlyList<TerminalParameterRow> BuildContent(
		ProjectContextPlan plan,
		SecretRedactionSnapshot? snapshot)
	{
		ArgumentNullException.ThrowIfNull(plan);
		return ProjectPresentationCatalog.ContentTransformations.Select(descriptor =>
			new TerminalParameterRow(
				$"content:{descriptor.Token}",
				TerminalParameterRowKind.ContentTransformation,
				fitLabel(FormatContentTransformationLabel(descriptor, plan, snapshot)),
				IsContentTransformationEnabled(plan.Selection, descriptor.LegacyOptionId),
				ContentTransformation: descriptor.LegacyOptionId)).ToArray();
	}

	public IReadOnlyList<TerminalParameterRow> BuildExclusions(ProjectContextPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);
		var exclusions = (plan.Selection.Exclusions ?? []).ToHashSet();
		var rows = new List<TerminalParameterRow>
		{
			new(
				"exclusions:all",
				TerminalParameterRowKind.ToggleAllExclusions,
				fitLabel(localize("Settings.All")),
				plan.GitReadiness.Mode != GitFilteringMode.None &&
				ProjectPresentationCatalog.Exclusions.All(descriptor =>
					exclusions.Contains(descriptor.RequireId())))
		};
		rows.AddRange(ProjectPresentationCatalog.GitFiltering
			.Where(static descriptor => descriptor.Id != GitFilteringMode.None)
			.Select(descriptor => new TerminalParameterRow(
				$"git:{descriptor.Token}",
				TerminalParameterRowKind.GitMode,
				fitLabel(localize(descriptor.LabelKey)),
				plan.GitReadiness.Mode == descriptor.Id,
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

	public IReadOnlyList<TerminalParameterRow> BuildExtensions(ProjectContextPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);
		var selectedExtensions = plan.SelectedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var rows = new List<TerminalParameterRow>
		{
			new(
				"extensions:all",
				TerminalParameterRowKind.ToggleAllExtensions,
				fitLabel(localize("Settings.All")),
				plan.AvailableExtensions.Count == selectedExtensions.Count &&
				plan.AvailableExtensions.All(selectedExtensions.Contains))
		};
		rows.AddRange(plan.AvailableExtensions.Select(extension =>
			new TerminalParameterRow(
				$"extension:{extension}",
				TerminalParameterRowKind.Extension,
				fitLabel(extension),
				selectedExtensions.Contains(extension),
				Value: extension)));
		rows.AddRange((plan.Selection.Extensions ?? [])
			.Where(extension => !plan.AvailableExtensions.Contains(
				extension,
				StringComparer.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Order(StringComparer.OrdinalIgnoreCase)
			.Select(extension => new TerminalParameterRow(
				$"extension-unavailable:{extension}",
				TerminalParameterRowKind.Information,
				fitInformationLabel(
					$"{localize("Terminal.Tui.Recent.Unavailable")}: {extension}"))));
		return rows;
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
		ProjectContextPlan plan,
		SecretRedactionSnapshot? snapshot)
	{
		if (snapshot is null ||
			!IsContentTransformationEnabled(plan.Selection, descriptor.LegacyOptionId))
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
}
