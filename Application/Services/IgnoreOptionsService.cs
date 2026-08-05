using DevProjex.Application.Secrets;

namespace DevProjex.Application.Services;

public sealed class IgnoreOptionsService(LocalizationService localization)
{
	public string FormatHideSecretsLabel(SecretScanState state, int? redactionCount)
	{
		var label = localization["Settings.Ignore.HideSecrets"];
		return state == SecretScanState.Completed && redactionCount > 0
			? $"{label} ({redactionCount})"
			: label;
	}

	public string FormatHideSecretsStatus(
		SecretScanState state,
		int? matchedCount,
		int? redactionCount) => state switch
	{
		SecretScanState.Scanning => localization["Settings.Secrets.Status.Scanning"],
		SecretScanState.Failed => localization["Settings.Secrets.Status.Failed"],
		SecretScanState.Completed when matchedCount == 0 =>
			localization["Settings.Secrets.Status.NoMatches"],
		SecretScanState.Completed when matchedCount > 0 && redactionCount == 0 =>
			localization.Format("Settings.Secrets.Status.AllKept", matchedCount),
		SecretScanState.Completed when matchedCount > 0 && redactionCount is not null =>
			localization.Format("Settings.Secrets.Status.Applied", matchedCount, redactionCount),
		_ => string.Empty
	};

	public IReadOnlyList<IgnoreOptionDescriptor> GetOptions(IgnoreOptionsAvailability availability)
	{
		var options = new List<IgnoreOptionDescriptor>();
		// Content-level protection belongs with the primary controllers, before Git modes.
		// Unlike path filters, Hide Secrets is always offered because availability cannot
		// be known without reading the selected content.
		AppendPrimaryExclusionOptions(options, availability);
		AppendContentTransformationOptions(options, availability);

		foreach (var descriptor in ProjectPresentationCatalog.GitFiltering)
		{
			if (descriptor.LegacyOptionId is not { } optionId)
				continue;
			var (included, isSelected) = descriptor.Id switch
			{
				GitFilteringMode.RespectGitIgnore =>
					(availability.IncludeGitIgnore, true),
				GitFilteringMode.TrackedFilesOnly =>
					(availability.IncludeTrackedGitFilesOnly, false),
				_ => (false, false)
			};
			if (included)
			{
				options.Add(new IgnoreOptionDescriptor(
					optionId,
					localization[descriptor.LabelKey],
					isSelected));
			}
		}

		AppendPathExclusionOptions(options, availability);
		return options;
	}

	private void AppendPrimaryExclusionOptions(
		List<IgnoreOptionDescriptor> options,
		IgnoreOptionsAvailability availability)
	{
		foreach (var descriptor in ProjectPresentationCatalog.Exclusions)
		{
			if (descriptor.Id != ProjectExclusion.SmartIgnore)
				continue;

			var included = availability.IncludeSmartIgnore;
			if (!included)
				continue;

			options.Add(new IgnoreOptionDescriptor(
				descriptor.LegacyOptionId,
				localization[descriptor.LabelKey],
				true));
		}
	}

	private void AppendPathExclusionOptions(
		List<IgnoreOptionDescriptor> options,
		IgnoreOptionsAvailability availability)
	{
		foreach (var descriptor in ProjectPresentationCatalog.Exclusions)
		{
			if (descriptor.Id == ProjectExclusion.SmartIgnore)
				continue;

			var (included, count) = descriptor.Id switch
			{
				ProjectExclusion.HiddenFolders =>
					(availability.IncludeHiddenFolders, availability.HiddenFoldersCount),
				ProjectExclusion.HiddenFiles =>
					(availability.IncludeHiddenFiles, availability.HiddenFilesCount),
				ProjectExclusion.DotFolders =>
					(availability.IncludeDotFolders, availability.DotFoldersCount),
				ProjectExclusion.DotFiles =>
					(availability.IncludeDotFiles, availability.DotFilesCount),
				ProjectExclusion.EmptyFolders =>
					(availability.IncludeEmptyFolders, availability.EmptyFoldersCount),
				ProjectExclusion.EmptyFiles =>
					(availability.IncludeEmptyFiles, availability.EmptyFilesCount),
				ProjectExclusion.ExtensionlessFiles =>
					(availability.IncludeExtensionlessFiles, availability.ExtensionlessFilesCount),
				_ => throw new ArgumentOutOfRangeException()
			};
			if (!included)
				continue;
			options.Add(new IgnoreOptionDescriptor(
				descriptor.LegacyOptionId,
				FormatLabelWithCount(
					localization[descriptor.LabelKey],
					count,
					availability.ShowAdvancedCounts),
				true));
		}
	}

	private void AppendContentTransformationOptions(
		List<IgnoreOptionDescriptor> options,
		IgnoreOptionsAvailability availability)
	{
		foreach (var descriptor in ProjectPresentationCatalog.ContentTransformations)
		{
			var label = availability.SecretRedactionsCount is { } redactionCount
				? FormatHideSecretsLabel(SecretScanState.Completed, redactionCount)
				: localization[descriptor.LabelKey];
			options.Add(new IgnoreOptionDescriptor(descriptor.LegacyOptionId, label, false));
		}
	}

	public IReadOnlyList<IgnoreOptionDescriptor> GetOptions()
	{
		return GetOptions(new IgnoreOptionsAvailability(
			IncludeGitIgnore: false,
			IncludeSmartIgnore: false));
	}

	public IReadOnlyList<IgnoreOptionDescriptor> GetOptions(bool includeGitIgnore)
	{
		return GetOptions(new IgnoreOptionsAvailability(
			IncludeGitIgnore: includeGitIgnore,
			IncludeSmartIgnore: false));
	}

	private static string FormatLabelWithCount(string baseLabel, int count, bool showAdvancedCounts)
	{
		return showAdvancedCounts && count > 0
			? $"{baseLabel} ({count})"
			: baseLabel;
	}
}
