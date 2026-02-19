namespace DevProjex.Tests.Unit;

public sealed class IgnoreOptionsServiceAvailabilityTests
{
	private static readonly IReadOnlyDictionary<AppLanguage, IReadOnlyDictionary<string, string>> CatalogData =
		new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Settings.Ignore.SmartIgnore"] = "Smart Ignore",
				["Settings.Ignore.UseGitIgnore"] = "Use GitIgnore",
				["Settings.Ignore.HiddenFolders"] = "Ignore hidden folders",
				["Settings.Ignore.HiddenFiles"] = "Ignore hidden files",
				["Settings.Ignore.DotFolders"] = "Ignore dot folders",
				["Settings.Ignore.DotFiles"] = "Ignore dot files"
			}
		};

	[Theory]
	[InlineData(false, false, 4)]
	[InlineData(true, false, 5)]
	[InlineData(false, true, 5)]
	[InlineData(true, true, 6)]
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
		Assert.Equal(IgnoreOptionId.UseGitIgnore, options[1].Id);
		Assert.Equal(IgnoreOptionId.HiddenFolders, options[2].Id);
		Assert.Equal(IgnoreOptionId.HiddenFiles, options[3].Id);
		Assert.Equal(IgnoreOptionId.DotFolders, options[4].Id);
		Assert.Equal(IgnoreOptionId.DotFiles, options[5].Id);
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
