namespace DevProjex.Tests.Unit;

public sealed class IgnoreOptionsServiceEmptyFoldersCountTests
{
	private static readonly IReadOnlyDictionary<AppLanguage, IReadOnlyDictionary<string, string>> CatalogData =
		new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Settings.Ignore.SmartIgnore"] = "Smart ignore",
				["Settings.Ignore.HideSecrets"] = "Hide secrets",
				["Settings.Ignore.UseGitIgnore"] = "Use .gitignore",
				["Settings.Ignore.HiddenFolders"] = "Hidden folders",
				["Settings.Ignore.HiddenFiles"] = "Hidden files",
				["Settings.Ignore.DotFolders"] = "Dot folders",
				["Settings.Ignore.DotFiles"] = "Dot files",
				["Settings.Ignore.EmptyFolders"] = "Empty folders",
				["Settings.Ignore.ExtensionlessFiles"] = "Files without extension"
			}
		};

	[Theory]
	[MemberData(nameof(VisibleOptionLabelMatrix))]
	public void GetOptions_EmptyFoldersLabel_Matrix(
		bool showAdvancedCounts,
		int emptyFoldersCount,
		string expectedLabel)
	{
		var service = CreateService();

		var options = service.GetOptions(new IgnoreOptionsAvailability(
			IncludeGitIgnore: false,
			IncludeSmartIgnore: false,
			IncludeEmptyFolders: true,
			EmptyFoldersCount: emptyFoldersCount,
			ShowAdvancedCounts: showAdvancedCounts));

		var option = options.SingleOrDefault(item => item.Id == IgnoreOptionId.EmptyFolders);
		Assert.NotNull(option);
		Assert.Equal(expectedLabel, option!.Label);
		Assert.True(option.DefaultChecked);
	}

	[Theory]
	[MemberData(nameof(HiddenOptionMatrix))]
	public void GetOptions_EmptyFoldersOption_IsHidden_WhenAvailabilityDisabled(
		bool showAdvancedCounts,
		int emptyFoldersCount)
	{
		var service = CreateService();

		var options = service.GetOptions(new IgnoreOptionsAvailability(
			IncludeGitIgnore: false,
			IncludeSmartIgnore: false,
			IncludeEmptyFolders: false,
			EmptyFoldersCount: emptyFoldersCount,
			ShowAdvancedCounts: showAdvancedCounts));

		Assert.DoesNotContain(options, item => item.Id == IgnoreOptionId.EmptyFolders);
	}

	[Theory]
	[MemberData(nameof(OrderMatrix))]
	public void GetOptions_EmptyFoldersOption_HasStableOrder(
		bool includeSmartIgnore,
		bool includeGitIgnore,
		bool includeExtensionless)
	{
		var service = CreateService();

		var options = service.GetOptions(new IgnoreOptionsAvailability(
			IncludeGitIgnore: includeGitIgnore,
			IncludeSmartIgnore: includeSmartIgnore,
			IncludeHiddenFolders: true,
			IncludeHiddenFiles: true,
			IncludeDotFolders: true,
			IncludeDotFiles: true,
			IncludeEmptyFolders: true,
			EmptyFoldersCount: 3,
			IncludeExtensionlessFiles: includeExtensionless,
			ExtensionlessFilesCount: 5,
			ShowAdvancedCounts: true));

		var ids = options.Select(x => x.Id).ToList();
		var emptyIndex = ids.IndexOf(IgnoreOptionId.EmptyFolders);
		Assert.NotEqual(-1, emptyIndex);

		var hiddenFoldersIndex = ids.IndexOf(IgnoreOptionId.HiddenFolders);
		Assert.NotEqual(-1, hiddenFoldersIndex);
		Assert.True(emptyIndex < hiddenFoldersIndex);

		if (includeExtensionless)
		{
			var extensionlessIndex = ids.IndexOf(IgnoreOptionId.ExtensionlessFiles);
			Assert.NotEqual(-1, extensionlessIndex);
			Assert.True(emptyIndex < extensionlessIndex);
		}
	}

	[Fact]
	public void GetOptions_EmptyFoldersLabel_DoesNotShowCountSuffix_WhenCountNotPositive()
	{
		var service = CreateService();

		var options = service.GetOptions(new IgnoreOptionsAvailability(
			IncludeGitIgnore: false,
			IncludeSmartIgnore: false,
			IncludeEmptyFolders: true,
			EmptyFoldersCount: 0,
			ShowAdvancedCounts: true));

		var option = options.Single(item => item.Id == IgnoreOptionId.EmptyFolders);
		Assert.Equal("Empty folders", option.Label);
	}

	public static IEnumerable<object[]> VisibleOptionLabelMatrix()
	{
		foreach (var showAdvancedCounts in new[] { false, true })
		{
			foreach (var emptyFoldersCount in new[] { -10, -1, 0, 1, 2, 3, 7, 19, 50 })
			{
				var expectedLabel = showAdvancedCounts && emptyFoldersCount > 0
					? $"Empty folders ({emptyFoldersCount})"
					: "Empty folders";

				yield return [ showAdvancedCounts, emptyFoldersCount, expectedLabel ];
			}
		}
	}

	public static IEnumerable<object[]> HiddenOptionMatrix()
	{
		foreach (var showAdvancedCounts in new[] { false, true })
		{
			foreach (var emptyFoldersCount in new[] { -10, -1, 0, 1, 2, 3, 7, 19, 50 })
				yield return [ showAdvancedCounts, emptyFoldersCount ];
		}
	}

	public static IEnumerable<object[]> OrderMatrix()
	{
		foreach (var includeSmartIgnore in new[] { false, true })
		{
			foreach (var includeGitIgnore in new[] { false, true })
			{
				foreach (var includeExtensionless in new[] { false, true })
					yield return [ includeSmartIgnore, includeGitIgnore, includeExtensionless ];
			}
		}
	}

	private static IgnoreOptionsService CreateService()
	{
		var localization = new LocalizationService(new StubLocalizationCatalog(CatalogData), AppLanguage.En);
		return new IgnoreOptionsService(localization);
	}
}
