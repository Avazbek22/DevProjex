namespace DevProjex.Tests.Integration;

public sealed class IgnoreOptionsExtensionlessDefaultsIntegrationTests
{
	[Theory]
	[MemberData(nameof(SelectedRootCases))]
	public void ExtensionlessAvailabilityPipeline_KeepsOptionCheckedByDefault(
		string[] selectedRoots,
		int expectedCount)
	{
		using var temp = new TemporaryDirectory();
		SeedWorkspace(temp);

		var options = BuildIgnoreOptions(temp.Path, selectedRoots);
		var extensionlessOption = options.SingleOrDefault(option => option.Id == IgnoreOptionId.ExtensionlessFiles);

		if (expectedCount <= 0)
		{
			Assert.Null(extensionlessOption);
			return;
		}

		Assert.NotNull(extensionlessOption);
		Assert.True(extensionlessOption!.DefaultChecked);
		Assert.Equal($"Files without extension ({expectedCount})", extensionlessOption.Label);
	}

	public static IEnumerable<object[]> SelectedRootCases()
	{
		yield return [Array.Empty<string>(), 0];
		yield return [new[] { "src" }, 1];
		yield return [new[] { "tests" }, 1];
		yield return [new[] { "docs" }, 0];
		yield return [new[] { "src", "tests" }, 2];
		yield return [new[] { "src", "docs" }, 1];
	}

	private static IReadOnlyList<IgnoreOptionDescriptor> BuildIgnoreOptions(
		string rootPath,
		IReadOnlyCollection<string> selectedRoots)
	{
		var scanner = new FileSystemScanner();
		var scanOptions = new ScanOptionsUseCase(scanner);
		var smartIgnore = new SmartIgnoreService([]);
		var ignoreRulesService = new IgnoreRulesService(smartIgnore);
		var ignoreRules = ignoreRulesService.Build(rootPath, [], selectedRoots);
		var scan = scanOptions.GetExtensionsAndIgnoreCountsForRootFolders(rootPath, selectedRoots, ignoreRules);
		var availability = ignoreRulesService.GetIgnoreOptionsAvailability(rootPath, selectedRoots) with
		{
			IncludeEmptyFolders = scan.Value.IgnoreOptionCounts.EmptyFolders > 0,
			EmptyFoldersCount = scan.Value.IgnoreOptionCounts.EmptyFolders,
			IncludeEmptyFiles = scan.Value.IgnoreOptionCounts.EmptyFiles > 0,
			EmptyFilesCount = scan.Value.IgnoreOptionCounts.EmptyFiles,
			IncludeExtensionlessFiles = scan.Value.IgnoreOptionCounts.ExtensionlessFiles > 0,
			ExtensionlessFilesCount = scan.Value.IgnoreOptionCounts.ExtensionlessFiles,
			ShowAdvancedCounts = true
		};

		var localization = new LocalizationService(new TestLocalizationCatalog(), AppLanguage.En);
		var ignoreOptionsService = new IgnoreOptionsService(localization);
		return ignoreOptionsService.GetOptions(availability);
	}

	private static void SeedWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile("src/Makefile", "build:\n\tdotnet build");
		temp.CreateFile("src/app.cs", "class App { }");
		temp.CreateFile("tests/LICENSE", "MIT");
		temp.CreateFile("tests/app.test.cs", "class Tests { }");
		temp.CreateFile("docs/readme.md", "# Docs");
	}

	private sealed class TestLocalizationCatalog : ILocalizationCatalog
	{
		private static readonly IReadOnlyDictionary<string, string> EnglishCatalog =
			new Dictionary<string, string>
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
			};

		public IReadOnlyDictionary<string, string> Get(AppLanguage language)
		{
			return EnglishCatalog;
		}
	}
}
