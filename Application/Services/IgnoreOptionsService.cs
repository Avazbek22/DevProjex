using DevProjex.Application.Secrets;

namespace DevProjex.Application.Services;

public sealed class IgnoreOptionsService(LocalizationService localization)
{
	public string FormatHideSecretsLabel(
		SecretScanState state,
		int? matchedCount,
		int? redactionCount) =>
		FormatContentRedactionLabel(
			IgnoreOptionId.HideSecrets,
			state,
			matchedCount,
			redactionCount);

	public string FormatContentRedactionLabel(
		IgnoreOptionId optionId,
		SecretScanState state,
		int? matchedCount,
		int? redactionCount)
	{
		var labelKey = optionId switch
		{
			IgnoreOptionId.HideSecrets => "Settings.Ignore.HideSecrets",
			IgnoreOptionId.HidePrivateData => "Settings.Ignore.HidePrivateData",
			_ => throw new ArgumentOutOfRangeException(nameof(optionId), optionId, null)
		};
		var label = localization[labelKey];
		// A clean scan keeps the plain label: the row's status indicator already reports no matches,
		// and "(0/0)" next to it would read as a counter for something that is not there.
		if (state is not (SecretScanState.Completed or SecretScanState.Limited) ||
		    matchedCount is not > 0 ||
		    redactionCount is null)
		{
			return label;
		}

		// Matched and hidden counts differ only while overrides or a partial scan are in play;
		// the usual all-hidden result collapses to one number so long locale labels keep fitting.
		return matchedCount == redactionCount
			? $"{label} ({matchedCount})"
			: $"{label} ({matchedCount}/{redactionCount})";
	}

	public IReadOnlyList<IgnoreOptionDescriptor> GetOptions(IgnoreOptionsAvailability availability)
	{
		var options = new List<IgnoreOptionDescriptor>();
		// Content-level transformations belong with the primary controllers, before Git modes.
		// Unlike path filters, redaction is always offered because availability cannot be known
		// without reading the selected content.
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

	public string FormatCompressCodeLabel(
		int? compressedFiles,
		int? uncompressedFiles,
		bool unavailable = false)
	{
		var label = localization["Settings.Ignore.CompressCode"];
		return unavailable
			? localization.Format("Settings.Ignore.CompressCodeUnavailable", label)
			: label;
	}

	public string FormatStripCommentsLabel(int? strippedFiles, int? unchangedFiles)
		=> localization["Settings.Ignore.StripComments"];

	public string FormatStripBlankLinesLabel(int? strippedFiles, int? unchangedFiles)
		=> localization["Settings.Ignore.StripBlankLines"];

	private void AppendContentTransformationOptions(
		List<IgnoreOptionDescriptor> options,
		IgnoreOptionsAvailability availability)
	{
		// Dispatched per descriptor: a single shared formatter would give every transformation the
		// secret counters, so the compression row would silently advertise someone else's numbers.
		foreach (var descriptor in ProjectPresentationCatalog.ContentTransformations)
		{
			var label = descriptor.LegacyOptionId switch
			{
				IgnoreOptionId.HideSecrets when
					availability.SecretMatchesCount is { } matchedCount &&
					availability.SecretRedactionsCount is { } redactionCount =>
					FormatHideSecretsLabel(SecretScanState.Completed, matchedCount, redactionCount),
				IgnoreOptionId.HidePrivateData when
					availability.PrivateDataMatchesCount is { } matchedCount &&
					availability.PrivateDataRedactionsCount is { } redactionCount =>
					FormatContentRedactionLabel(
						IgnoreOptionId.HidePrivateData,
						SecretScanState.Completed,
						matchedCount,
						redactionCount),
				IgnoreOptionId.CompressCode =>
					FormatCompressCodeLabel(availability.CompressedFilesCount, availability.UncompressedFilesCount),
				IgnoreOptionId.StripComments =>
					FormatStripCommentsLabel(
						availability.CommentStrippedFilesCount,
						availability.CommentUnchangedFilesCount),
				IgnoreOptionId.StripBlankLines =>
					FormatStripBlankLinesLabel(
						availability.BlankLineStrippedFilesCount,
						availability.BlankLineUnchangedFilesCount),
				_ => localization[descriptor.LabelKey]
			};
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
