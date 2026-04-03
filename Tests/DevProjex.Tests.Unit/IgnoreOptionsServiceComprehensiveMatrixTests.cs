namespace DevProjex.Tests.Unit;

public sealed class IgnoreOptionsServiceComprehensiveMatrixTests
{
	private static readonly IReadOnlyDictionary<AppLanguage, IReadOnlyDictionary<string, string>> CatalogData =
		new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Settings.Ignore.SmartIgnore"] = "Smart ignore",
				["Settings.Ignore.UseGitIgnore"] = "Use .gitignore",
				["Settings.Ignore.HiddenFolders"] = "Hidden folders",
				["Settings.Ignore.HiddenFiles"] = "Hidden files",
				["Settings.Ignore.DotFolders"] = "Dot folders",
				["Settings.Ignore.DotFiles"] = "Dot files",
				["Settings.Ignore.EmptyFolders"] = "Empty folders",
				["Settings.Ignore.EmptyFiles"] = "Empty files",
				["Settings.Ignore.ExtensionlessFiles"] = "Files without extension"
			}
		};

	public static IEnumerable<object[]> AvailabilityMatrix()
	{
		foreach (var includeSmart in new[] { false, true })
		foreach (var includeGit in new[] { false, true })
		foreach (var includeHiddenFolders in new[] { false, true })
		foreach (var includeHiddenFiles in new[] { false, true })
		foreach (var includeDotFolders in new[] { false, true })
		foreach (var includeDotFiles in new[] { false, true })
		foreach (var includeEmptyFolders in new[] { false, true })
		foreach (var includeEmptyFiles in new[] { false, true })
		foreach (var includeExtensionless in new[] { false, true })
		foreach (var showAdvancedCounts in new[] { false, true })
		{
			yield return
			[
				includeSmart,
				includeGit,
				includeHiddenFolders,
				includeHiddenFiles,
				includeDotFolders,
				includeDotFiles,
				includeEmptyFolders,
				includeEmptyFiles,
				includeExtensionless,
				showAdvancedCounts
			];
		}
	}

	[Theory]
	[MemberData(nameof(AvailabilityMatrix))]
	public void GetOptions_ComprehensiveMatrix_ProducesDeterministicOrderedOptions(
		bool includeSmart,
		bool includeGit,
		bool includeHiddenFolders,
		bool includeHiddenFiles,
		bool includeDotFolders,
		bool includeDotFiles,
		bool includeEmptyFolders,
		bool includeEmptyFiles,
		bool includeExtensionless,
		bool showAdvancedCounts)
	{
		var service = CreateService();
		var availability = new IgnoreOptionsAvailability(
			IncludeGitIgnore: includeGit,
			IncludeSmartIgnore: includeSmart,
			IncludeHiddenFolders: includeHiddenFolders,
			HiddenFoldersCount: 2,
			IncludeHiddenFiles: includeHiddenFiles,
			HiddenFilesCount: 3,
			IncludeDotFolders: includeDotFolders,
			DotFoldersCount: 4,
			IncludeDotFiles: includeDotFiles,
			DotFilesCount: 5,
			IncludeEmptyFolders: includeEmptyFolders,
			EmptyFoldersCount: 6,
			IncludeEmptyFiles: includeEmptyFiles,
			EmptyFilesCount: 8,
			IncludeExtensionlessFiles: includeExtensionless,
			ExtensionlessFilesCount: 7,
			ShowAdvancedCounts: showAdvancedCounts);

		var options = service.GetOptions(availability);

		Assert.Equal(options.Count, options.Select(x => x.Id).Distinct().Count());

		var expectedIds = BuildExpectedIds(
			includeSmart,
			includeGit,
			includeEmptyFolders,
			includeEmptyFiles,
			includeHiddenFolders,
			includeHiddenFiles,
			includeDotFolders,
			includeDotFiles,
			includeExtensionless);

		Assert.Equal(expectedIds, options.Select(x => x.Id).ToArray());

		AssertCountLabel(options, IgnoreOptionId.HiddenFolders, "Hidden folders", 2, showAdvancedCounts, includeHiddenFolders);
		AssertCountLabel(options, IgnoreOptionId.HiddenFiles, "Hidden files", 3, showAdvancedCounts, includeHiddenFiles);
		AssertCountLabel(options, IgnoreOptionId.DotFolders, "Dot folders", 4, showAdvancedCounts, includeDotFolders);
		AssertCountLabel(options, IgnoreOptionId.DotFiles, "Dot files", 5, showAdvancedCounts, includeDotFiles);
		AssertCountLabel(options, IgnoreOptionId.EmptyFolders, "Empty folders", 6, showAdvancedCounts, includeEmptyFolders);
		AssertCountLabel(options, IgnoreOptionId.EmptyFiles, "Empty files", 8, showAdvancedCounts, includeEmptyFiles);
		AssertCountLabel(options, IgnoreOptionId.ExtensionlessFiles, "Files without extension", 7, showAdvancedCounts, includeExtensionless);

		if (includeExtensionless)
			Assert.True(options.Single(x => x.Id == IgnoreOptionId.ExtensionlessFiles).DefaultChecked);
	}

	public static IEnumerable<object[]> CountLabelMatrix()
	{
		var ids = new[]
		{
			IgnoreOptionId.HiddenFolders,
			IgnoreOptionId.HiddenFiles,
			IgnoreOptionId.DotFolders,
			IgnoreOptionId.DotFiles,
			IgnoreOptionId.EmptyFolders,
			IgnoreOptionId.EmptyFiles,
			IgnoreOptionId.ExtensionlessFiles
		};

		foreach (var optionId in ids)
		foreach (var showAdvanced in new[] { false, true })
		foreach (var count in new[] { -3, -1, 0, 1, 2, 10 })
			yield return [optionId, showAdvanced, count];
	}

	[Theory]
	[MemberData(nameof(CountLabelMatrix))]
	public void GetOptions_LabelSuffix_AppearsOnlyWhenShowAdvancedAndPositive(
		IgnoreOptionId optionId,
		bool showAdvanced,
		int count)
	{
		var service = CreateService();
		var availability = BuildSingleOptionAvailability(optionId, count, showAdvanced);
		var options = service.GetOptions(availability);

		Assert.Single(options);
		Assert.Equal(optionId, options[0].Id);

		var baseLabel = optionId switch
		{
			IgnoreOptionId.HiddenFolders => "Hidden folders",
			IgnoreOptionId.HiddenFiles => "Hidden files",
			IgnoreOptionId.DotFolders => "Dot folders",
			IgnoreOptionId.DotFiles => "Dot files",
			IgnoreOptionId.EmptyFolders => "Empty folders",
			IgnoreOptionId.EmptyFiles => "Empty files",
			IgnoreOptionId.ExtensionlessFiles => "Files without extension",
			_ => throw new ArgumentOutOfRangeException(nameof(optionId), optionId, "Unsupported option id")
		};

		var expected = showAdvanced && count > 0
			? $"{baseLabel} ({count})"
			: baseLabel;

		Assert.Equal(expected, options[0].Label);
	}

	private static IgnoreOptionId[] BuildExpectedIds(
		bool includeSmart,
		bool includeGit,
		bool includeEmptyFolders,
		bool includeEmptyFiles,
		bool includeHiddenFolders,
		bool includeHiddenFiles,
		bool includeDotFolders,
		bool includeDotFiles,
		bool includeExtensionless)
	{
		var ordered = new List<IgnoreOptionId>(9);
		if (includeSmart) ordered.Add(IgnoreOptionId.SmartIgnore);
		if (includeGit) ordered.Add(IgnoreOptionId.UseGitIgnore);
		if (includeEmptyFolders) ordered.Add(IgnoreOptionId.EmptyFolders);
		if (includeEmptyFiles) ordered.Add(IgnoreOptionId.EmptyFiles);
		if (includeHiddenFolders) ordered.Add(IgnoreOptionId.HiddenFolders);
		if (includeHiddenFiles) ordered.Add(IgnoreOptionId.HiddenFiles);
		if (includeDotFolders) ordered.Add(IgnoreOptionId.DotFolders);
		if (includeDotFiles) ordered.Add(IgnoreOptionId.DotFiles);
		if (includeExtensionless) ordered.Add(IgnoreOptionId.ExtensionlessFiles);
		return ordered.ToArray();
	}

	private static void AssertCountLabel(
		IReadOnlyList<IgnoreOptionDescriptor> options,
		IgnoreOptionId optionId,
		string baseLabel,
		int count,
		bool showAdvancedCounts,
		bool isIncluded)
	{
		var option = options.SingleOrDefault(x => x.Id == optionId);
		if (!isIncluded)
		{
			Assert.Null(option);
			return;
		}

		Assert.NotNull(option);
		var expectedLabel = showAdvancedCounts && count > 0
			? $"{baseLabel} ({count})"
			: baseLabel;
		Assert.Equal(expectedLabel, option!.Label);
	}

	private static IgnoreOptionsAvailability BuildSingleOptionAvailability(
		IgnoreOptionId optionId,
		int count,
		bool showAdvanced)
	{
		return optionId switch
		{
			IgnoreOptionId.HiddenFolders => new IgnoreOptionsAvailability(
				IncludeGitIgnore: false,
				IncludeSmartIgnore: false,
				IncludeHiddenFolders: true,
				HiddenFoldersCount: count,
				IncludeHiddenFiles: false,
				IncludeDotFolders: false,
				IncludeDotFiles: false,
				IncludeEmptyFolders: false,
				IncludeEmptyFiles: false,
				IncludeExtensionlessFiles: false,
				ShowAdvancedCounts: showAdvanced),
			IgnoreOptionId.HiddenFiles => new IgnoreOptionsAvailability(
				IncludeGitIgnore: false,
				IncludeSmartIgnore: false,
				IncludeHiddenFolders: false,
				IncludeHiddenFiles: true,
				HiddenFilesCount: count,
				IncludeDotFolders: false,
				IncludeDotFiles: false,
				IncludeEmptyFolders: false,
				IncludeEmptyFiles: false,
				IncludeExtensionlessFiles: false,
				ShowAdvancedCounts: showAdvanced),
			IgnoreOptionId.DotFolders => new IgnoreOptionsAvailability(
				IncludeGitIgnore: false,
				IncludeSmartIgnore: false,
				IncludeHiddenFolders: false,
				IncludeHiddenFiles: false,
				IncludeDotFolders: true,
				DotFoldersCount: count,
				IncludeDotFiles: false,
				IncludeEmptyFolders: false,
				IncludeEmptyFiles: false,
				IncludeExtensionlessFiles: false,
				ShowAdvancedCounts: showAdvanced),
			IgnoreOptionId.DotFiles => new IgnoreOptionsAvailability(
				IncludeGitIgnore: false,
				IncludeSmartIgnore: false,
				IncludeHiddenFolders: false,
				IncludeHiddenFiles: false,
				IncludeDotFolders: false,
				IncludeDotFiles: true,
				DotFilesCount: count,
				IncludeEmptyFolders: false,
				IncludeEmptyFiles: false,
				IncludeExtensionlessFiles: false,
				ShowAdvancedCounts: showAdvanced),
			IgnoreOptionId.EmptyFolders => new IgnoreOptionsAvailability(
				IncludeGitIgnore: false,
				IncludeSmartIgnore: false,
				IncludeHiddenFolders: false,
				IncludeHiddenFiles: false,
				IncludeDotFolders: false,
				IncludeDotFiles: false,
				IncludeEmptyFolders: true,
				EmptyFoldersCount: count,
				IncludeEmptyFiles: false,
				IncludeExtensionlessFiles: false,
				ShowAdvancedCounts: showAdvanced),
			IgnoreOptionId.EmptyFiles => new IgnoreOptionsAvailability(
				IncludeGitIgnore: false,
				IncludeSmartIgnore: false,
				IncludeHiddenFolders: false,
				IncludeHiddenFiles: false,
				IncludeDotFolders: false,
				IncludeDotFiles: false,
				IncludeEmptyFolders: false,
				IncludeExtensionlessFiles: false,
				IncludeEmptyFiles: true,
				EmptyFilesCount: count,
				ShowAdvancedCounts: showAdvanced),
			IgnoreOptionId.ExtensionlessFiles => new IgnoreOptionsAvailability(
				IncludeGitIgnore: false,
				IncludeSmartIgnore: false,
				IncludeHiddenFolders: false,
				IncludeHiddenFiles: false,
				IncludeDotFolders: false,
				IncludeDotFiles: false,
				IncludeEmptyFolders: false,
				IncludeEmptyFiles: false,
				IncludeExtensionlessFiles: true,
				ExtensionlessFilesCount: count,
				ShowAdvancedCounts: showAdvanced),
			_ => throw new ArgumentOutOfRangeException(nameof(optionId), optionId, "Unsupported option id")
		};
	}

	private static IgnoreOptionsService CreateService()
	{
		var localization = new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En);
		return new IgnoreOptionsService(localization);
	}
}
