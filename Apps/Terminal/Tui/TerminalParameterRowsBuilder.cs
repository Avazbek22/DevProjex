using DevProjex.Application.Secrets;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalParameterRowsBuilder(
	Func<string, string> localize,
	Func<string, string> fitLabel,
	Func<IgnoreOptionId, SecretScanState, int?, int?, string> formatRedactionLabel,
	bool useUnicodeRadioMarker = true)
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
		ProjectSelectionSpec? selectionOverride = null,
		bool gitCliAvailable = true)
	{
		ArgumentNullException.ThrowIfNull(plan);
		var selection = selectionOverride ?? plan.Selection;
		var exclusions = (selection.Exclusions ?? []).ToHashSet();
		var rows = new List<TerminalParameterRow>();
		var activeMode = selection.GitMode ?? plan.GitReadiness.Mode;
		var hasRepositoryBoundary = HasRepositoryBoundary(plan);
		var hasRepository = gitCliAvailable && hasRepositoryBoundary;
		if (IsGitFilteringApplicable(plan, hasRepositoryBoundary))
		{
			rows.AddRange(ProjectPresentationCatalog.GitFiltering
				.Select(descriptor => new TerminalParameterRow(
					$"git:{descriptor.Token}",
					TerminalParameterRowKind.GitMode,
					fitLabel(localize(descriptor.LabelKey)),
					activeMode == descriptor.Id,
					IsEnabled: descriptor.Id is not (GitFilteringMode.TrackedFilesOnly or
						GitFilteringMode.Staged or GitFilteringMode.Changes) || hasRepository,
					UseUnicodeRadioMarker: useUnicodeRadioMarker,
					GitMode: descriptor.Id)));
			if (activeMode == GitFilteringMode.Diff &&
			    !string.IsNullOrWhiteSpace(selection.GitDiffRange))
			{
				rows.Add(new TerminalParameterRow(
					"git:diff",
					TerminalParameterRowKind.GitMode,
					fitLabel($"diff: {selection.GitDiffRange}"),
					true,
					IsEnabled: hasRepository,
					UseUnicodeRadioMarker: useUnicodeRadioMarker,
					GitMode: GitFilteringMode.Diff,
					Value: selection.GitDiffRange));
			}
		}
		rows.AddRange(ProjectPresentationCatalog.Exclusions
			.Where(descriptor => IsPathExclusionAvailable(descriptor, plan))
			.Select(descriptor =>
			new TerminalParameterRow(
				$"exclusion:{descriptor.Token}",
				TerminalParameterRowKind.Exclusion,
				fitLabel(FormatPathExclusionLabel(descriptor, plan)),
				exclusions.Contains(descriptor.RequireId()),
				Exclusion: descriptor.RequireId())));
		return rows;
	}

	internal static bool IsGitFilteringApplicable(ProjectContextPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);
		return IsGitFilteringApplicable(plan, HasRepositoryBoundary(plan));
	}

	public TerminalParameterRow BuildExclusionAggregate(
		ProjectContextPlan plan,
		ProjectSelectionSpec? selectionOverride = null)
	{
		ArgumentNullException.ThrowIfNull(plan);
		var selection = selectionOverride ?? plan.Selection;
		var exclusions = (selection.Exclusions ?? []).ToHashSet();
		var availableExclusions = ProjectPresentationCatalog.Exclusions
			.Where(descriptor => IsPathExclusionAvailable(descriptor, plan))
			.ToArray();
		var count = availableExclusions.Length;
		return new TerminalParameterRow(
			"exclusions:all",
			TerminalParameterRowKind.ToggleAllExclusions,
			FormatAggregateLabel(count),
			count > 0 &&
			availableExclusions.All(descriptor =>
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
			plan.AvailableExtensions.Count > 0 &&
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

	private string FormatPathExclusionLabel(
		ProjectExclusionDescriptor descriptor,
		ProjectContextPlan plan)
	{
		var label = localize(descriptor.LabelKey);
		var impactCount = GetPathExclusionImpactCount(descriptor.RequireId(), plan.IgnoreOptionCounts);
		return plan.HasIgnoreOptionCounts && impactCount is > 0
			? $"{label} ({impactCount})"
			: label;
	}

	private static int? GetPathExclusionImpactCount(
		ProjectExclusion exclusion,
		in IgnoreOptionCounts counts) =>
		exclusion switch
		{
			ProjectExclusion.HiddenFolders => counts.HiddenFolders,
			ProjectExclusion.HiddenFiles => counts.HiddenFiles,
			ProjectExclusion.DotFolders => counts.DotFolders,
			ProjectExclusion.DotFiles => counts.DotFiles,
			ProjectExclusion.EmptyFolders => counts.EmptyFolders,
			ProjectExclusion.EmptyFiles => counts.EmptyFiles,
			ProjectExclusion.ExtensionlessFiles => counts.ExtensionlessFiles,
			ProjectExclusion.SmartIgnore => null,
			_ => throw new ArgumentOutOfRangeException(nameof(exclusion), exclusion, null)
		};

	private static bool IsPathExclusionAvailable(
		ProjectExclusionDescriptor descriptor,
		ProjectContextPlan plan)
	{
		if (!plan.HasIgnoreOptionCounts)
			return true;

		return descriptor.Id == ProjectExclusion.SmartIgnore
			? plan.IgnoreControllerImpactCounts.SmartIgnore > 0
			: GetPathExclusionImpactCount(descriptor.RequireId(), plan.IgnoreOptionCounts) > 0;
	}

	private static bool IsGitFilteringApplicable(
		ProjectContextPlan plan,
		bool hasRepositoryBoundary) =>
		hasRepositoryBoundary ||
		plan.HasIgnoreOptionCounts && plan.IgnoreControllerImpactCounts.GitIgnore > 0;

	private static bool HasRepositoryBoundary(ProjectContextPlan plan) =>
		plan.GitReadiness.HasRepositoryBoundary ||
		GitRepositoryBoundaryProbe.ExistsAtOrAbove(plan.SourceRoot);
}
