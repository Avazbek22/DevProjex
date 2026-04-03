namespace DevProjex.Tests.Integration;

public sealed class IgnoreOptionsDynamicDefaultsMatrixIntegrationTests
{
	[Theory]
	[MemberData(nameof(SelectedRootCases))]
	public void DynamicIgnoreOptionsPipeline_Matrix_KeepsDynamicOptionsCheckedByDefault(
		string[] selectedRoots,
		int expectedEmptyFolders,
		int expectedEmptyFiles,
		int expectedExtensionlessFiles)
	{
		using var temp = new TemporaryDirectory();
		SeedWorkspace(temp);

		var options = BuildIgnoreOptions(temp.Path, selectedRoots);

		AssertDynamicOption(options, IgnoreOptionId.EmptyFolders, "Empty folders", expectedEmptyFolders);
		AssertDynamicOption(options, IgnoreOptionId.EmptyFiles, "Empty files", expectedEmptyFiles);
		AssertDynamicOption(options, IgnoreOptionId.ExtensionlessFiles, "Files without extension", expectedExtensionlessFiles);
	}

	public static IEnumerable<object[]> SelectedRootCases()
	{
		yield return [Array.Empty<string>(), 0, 0, 0];
		yield return [new[] { "src" }, 1, 1, 1];
		yield return [new[] { "tests" }, 2, 2, 2];
		yield return [new[] { "docs" }, 0, 0, 0];
		yield return [new[] { "src", "tests" }, 3, 3, 3];
		yield return [new[] { "tests", "src" }, 3, 3, 3];
		yield return [new[] { "missing", "src" }, 1, 1, 1];
		yield return [new[] { "src", "docs" }, 1, 1, 1];
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
		var counts = scan.Value.IgnoreOptionCounts;
		var availability = ignoreRulesService.GetIgnoreOptionsAvailability(rootPath, selectedRoots) with
		{
			IncludeEmptyFolders = counts.EmptyFolders > 0,
			EmptyFoldersCount = counts.EmptyFolders,
			IncludeEmptyFiles = counts.EmptyFiles > 0,
			EmptyFilesCount = counts.EmptyFiles,
			IncludeExtensionlessFiles = counts.ExtensionlessFiles > 0,
			ExtensionlessFilesCount = counts.ExtensionlessFiles,
			ShowAdvancedCounts = true
		};

		var localization = new LocalizationService(new TestLocalizationCatalog(), AppLanguage.En);
		var ignoreOptionsService = new IgnoreOptionsService(localization);
		return ignoreOptionsService.GetOptions(availability);
	}

	private static void SeedWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile("src/Makefile", "build:\n\tdotnet build");
		temp.CreateFile("src/empty.txt", string.Empty);
		temp.CreateDirectory("src/empty-folder");

		temp.CreateFile("tests/LICENSE", "MIT");
		temp.CreateFile("tests/WORKSPACE", "workspace(name = \"app\")");
		temp.CreateFile("tests/blank.txt", string.Empty);
		temp.CreateFile("tests/empty-data.json", string.Empty);
		temp.CreateDirectory("tests/void");
		temp.CreateDirectory("tests/spare");

		temp.CreateFile("docs/readme.md", "# Docs");
	}

	private static void AssertDynamicOption(
		IReadOnlyList<IgnoreOptionDescriptor> options,
		IgnoreOptionId optionId,
		string baseLabel,
		int expectedCount)
	{
		var option = options.SingleOrDefault(item => item.Id == optionId);
		if (expectedCount <= 0)
		{
			Assert.Null(option);
			return;
		}

		Assert.NotNull(option);
		Assert.True(option!.DefaultChecked);
		Assert.Equal($"{baseLabel} ({expectedCount})", option.Label);
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
