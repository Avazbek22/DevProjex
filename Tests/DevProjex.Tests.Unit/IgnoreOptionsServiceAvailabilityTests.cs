namespace DevProjex.Tests.Unit;

public sealed class IgnoreOptionsServiceAvailabilityTests
{
	private static readonly IReadOnlyDictionary<AppLanguage, IReadOnlyDictionary<string, string>> CatalogData =
		new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Settings.Ignore.HideSecrets"] = "Hide secrets",
				["Settings.Ignore.SmartIgnore"] = "Smart Ignore",
				["Settings.Ignore.UseGitIgnore"] = "Use GitIgnore",
				["Settings.Ignore.TrackedGitFilesOnly"] = "Tracked Git files only",
				["Settings.Ignore.HiddenFolders"] = "Ignore hidden folders",
				["Settings.Ignore.HiddenFiles"] = "Ignore hidden files",
				["Settings.Ignore.DotFolders"] = "Ignore dot folders",
				["Settings.Ignore.DotFiles"] = "Ignore dot files"
			}
		};

	[Theory]
	[InlineData(false, false, 5)]
	[InlineData(true, false, 6)]
	[InlineData(false, true, 6)]
	[InlineData(true, true, 7)]
	public void GetOptions_RespectsAvailabilityFlags(bool includeGitIgnore, bool includeSmartIgnore, int expectedCount)
	{
		var service = CreateService();

		var options = service.GetOptions(new IgnoreOptionsAvailability(includeGitIgnore, includeSmartIgnore));

		Assert.Equal(expectedCount, options.Count);
		Assert.Equal(expectedCount, options.Select(option => option.Id).Distinct().Count());
	}

	[Fact]
	public void GetOptions_WhenBothSmartAndGitAreAvailable_UsesExpectedOrder()
	{
		var service = CreateService();

		var options = service.GetOptions(new IgnoreOptionsAvailability(
			IncludeGitIgnore: true,
			IncludeSmartIgnore: true));

		Assert.Equal(IgnoreOptionId.SmartIgnore, options[0].Id);
		Assert.Equal(IgnoreOptionId.HideSecrets, options[1].Id);
		Assert.Equal(IgnoreOptionId.UseGitIgnore, options[2].Id);
		Assert.Equal(IgnoreOptionId.HiddenFolders, options[3].Id);
		Assert.Equal(IgnoreOptionId.HiddenFiles, options[4].Id);
		Assert.Equal(IgnoreOptionId.DotFolders, options[5].Id);
		Assert.Equal(IgnoreOptionId.DotFiles, options[6].Id);
	}

	[Fact]
	public void GetOptions_RepositoryAvailability_KeepsGitModesAdjacentAndMutuallyExclusiveByDefault()
	{
		var service = CreateService();

		var options = service.GetOptions(new IgnoreOptionsAvailability(
			IncludeGitIgnore: true,
			IncludeSmartIgnore: true,
			IncludeTrackedGitFilesOnly: true));

		Assert.Equal(
			[
				IgnoreOptionId.SmartIgnore,
				IgnoreOptionId.HideSecrets,
				IgnoreOptionId.UseGitIgnore,
				IgnoreOptionId.TrackedGitFilesOnly,
				IgnoreOptionId.HiddenFolders,
				IgnoreOptionId.HiddenFiles,
				IgnoreOptionId.DotFolders,
				IgnoreOptionId.DotFiles
			],
			options.Select(static option => option.Id));
		Assert.False(options[1].DefaultChecked);
		Assert.True(options[2].DefaultChecked);
		Assert.False(options[3].DefaultChecked);
	}

	[Fact]
	public void GetOptions_WhenOnlySmartIsAvailable_PlacesItFirst()
	{
		var service = CreateService();

		var options = service.GetOptions(new IgnoreOptionsAvailability(
			IncludeGitIgnore: false,
			IncludeSmartIgnore: true));

		Assert.Equal(IgnoreOptionId.SmartIgnore, options[0].Id);
		Assert.DoesNotContain(options, option => option.Id == IgnoreOptionId.UseGitIgnore);
	}

	private static IgnoreOptionsService CreateService()
	{
		var localization = new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En);
		return new IgnoreOptionsService(localization);
	}
}
