namespace DevProjex.Tests.Unit;

public sealed class IgnoreOptionsServiceExtensionlessCountTests
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
				["Settings.Ignore.ExtensionlessFiles"] = "Files without extension"
			}
		};

	[Theory]
	[InlineData(true, 19, "Files without extension (19)")]
	[InlineData(true, 1, "Files without extension (1)")]
	[InlineData(true, 0, "Files without extension")]
	[InlineData(true, -3, "Files without extension")]
	public void GetOptions_ExtensionlessLabel_UsesCountSuffixWhenPositive(
		bool includeExtensionlessFiles,
		int extensionlessCount,
		string expectedLabel)
	{
		var service = CreateService();

		var options = service.GetOptions(new IgnoreOptionsAvailability(
			IncludeGitIgnore: false,
			IncludeSmartIgnore: false,
			IncludeExtensionlessFiles: includeExtensionlessFiles,
			ExtensionlessFilesCount: extensionlessCount,
			ShowAdvancedCounts: true));

		var option = options.SingleOrDefault(item => item.Id == IgnoreOptionId.ExtensionlessFiles);
		Assert.NotNull(option);
		Assert.Equal(expectedLabel, option!.Label);
		Assert.True(option.DefaultChecked);
	}

	[Fact]
	public void GetOptions_ExtensionlessOption_IsHiddenWhenAvailabilityIsFalse_EvenIfCountIsPositive()
	{
		var service = CreateService();

		var options = service.GetOptions(new IgnoreOptionsAvailability(
			IncludeGitIgnore: false,
			IncludeSmartIgnore: false,
			IncludeExtensionlessFiles: false,
			ExtensionlessFilesCount: 42));

		Assert.DoesNotContain(options, item => item.Id == IgnoreOptionId.ExtensionlessFiles);
	}

	[Fact]
	public void GetOptions_WithAllAvailabilityFlags_PlacesExtensionlessOptionLastWithCountLabel()
	{
		var service = CreateService();

		var options = service.GetOptions(new IgnoreOptionsAvailability(
			IncludeGitIgnore: true,
			IncludeSmartIgnore: true,
			IncludeExtensionlessFiles: true,
			ExtensionlessFilesCount: 2,
			ShowAdvancedCounts: true));

		Assert.Equal(7, options.Count);
		Assert.Equal(IgnoreOptionId.SmartIgnore, options[0].Id);
		Assert.Equal(IgnoreOptionId.UseGitIgnore, options[1].Id);
		Assert.Equal(IgnoreOptionId.HiddenFolders, options[2].Id);
		Assert.Equal(IgnoreOptionId.HiddenFiles, options[3].Id);
		Assert.Equal(IgnoreOptionId.DotFolders, options[4].Id);
		Assert.Equal(IgnoreOptionId.DotFiles, options[5].Id);
		Assert.Equal(IgnoreOptionId.ExtensionlessFiles, options[6].Id);
		Assert.Equal("Files without extension (2)", options[6].Label);
	}

	private static IgnoreOptionsService CreateService()
	{
		var localization = new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En);
		return new IgnoreOptionsService(localization);
	}
}
