namespace DevProjex.Tests.Unit;

public sealed class IgnoreOptionsServiceAvailabilityTests
{
	private static readonly IReadOnlyDictionary<AppLanguage, IReadOnlyDictionary<string, string>> CatalogData =
		new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Settings.Ignore.HideSecrets"] = "Hide secrets",
				["Settings.Ignore.CompressCode"] = "Compress code",
				["Settings.Ignore.StripComments"] = "Strip comments",
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
	// Counts include every content transformation: they are always offered, because whether they
	// would change anything cannot be known without reading the selected content.
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	[InlineData(true, true)]
	public void GetOptions_RespectsAvailabilityFlags(bool includeGitIgnore, bool includeSmartIgnore)
	{
		var service = CreateService();
		var expectedCount = 4 +
		                    IgnoreOptionOrder.Count +
		                    (includeGitIgnore ? 1 : 0) +
		                    (includeSmartIgnore ? 1 : 0);

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

		Assert.Equal(
			IgnoreOptionOrder.Expected(
				[IgnoreOptionId.SmartIgnore],
				[
					IgnoreOptionId.UseGitIgnore,
					IgnoreOptionId.HiddenFolders,
					IgnoreOptionId.HiddenFiles,
					IgnoreOptionId.DotFolders,
					IgnoreOptionId.DotFiles
				]),
			options.Select(static option => option.Id));
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
			IgnoreOptionOrder.Expected(
				[IgnoreOptionId.SmartIgnore],
				[
					IgnoreOptionId.UseGitIgnore,
					IgnoreOptionId.TrackedGitFilesOnly,
					IgnoreOptionId.HiddenFolders,
					IgnoreOptionId.HiddenFiles,
					IgnoreOptionId.DotFolders,
					IgnoreOptionId.DotFiles
				]),
			options.Select(static option => option.Id));
		// The whole transformation block is opt-in; the Git modes that follow keep their defaults.
		Assert.All(
			options.Skip(1).Take(IgnoreOptionOrder.Count),
			static option => Assert.False(option.DefaultChecked));
		Assert.True(options[1 + IgnoreOptionOrder.Count].DefaultChecked);
		Assert.False(options[2 + IgnoreOptionOrder.Count].DefaultChecked);
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
