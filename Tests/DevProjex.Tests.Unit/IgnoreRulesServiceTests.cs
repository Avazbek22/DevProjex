namespace DevProjex.Tests.Unit;

public sealed class IgnoreRulesServiceTests
{
	// Verifies selected ignore options and smart-ignore rules are merged into IgnoreRules.
	[Fact]
	public void Build_CombinesSelectedOptionsAndSmartIgnore()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "bin/");

		var smartResult = new SmartIgnoreResult(
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cache" },
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "thumbs.db" });
		var smart = new SmartIgnoreService([new StubSmartIgnoreRule(smartResult)]);
		var service = new IgnoreRulesService(smart);

		var rules = service.Build(temp.Path, [IgnoreOptionId.SmartIgnore, IgnoreOptionId.HiddenFolders, IgnoreOptionId.DotFiles
		]);

		Assert.True(rules.IgnoreHiddenFolders);
		Assert.True(rules.IgnoreDotFiles);
		Assert.False(rules.IgnoreHiddenFiles);
		Assert.Contains("cache", rules.SmartIgnoredFolders);
		Assert.Contains("thumbs.db", rules.SmartIgnoredFiles);
	}

	// Verifies no selections keep ignore flags disabled.
	[Fact]
	public void Build_ReturnsAllFlagsFalseWhenNoSelections()
	{
		var smart = new SmartIgnoreService(new ISmartIgnoreRule[0]);
		var service = new IgnoreRulesService(smart);

		var rules = service.Build("/root", []);

		Assert.False(rules.IgnoreHiddenFolders);
		Assert.False(rules.IgnoreHiddenFiles);
		Assert.False(rules.IgnoreHiddenFolders);
		Assert.False(rules.IgnoreHiddenFiles);
		Assert.False(rules.IgnoreDotFolders);
		Assert.False(rules.IgnoreDotFiles);
	}

	// Verifies smart-ignore results are case-insensitive.
	[Fact]
	public void Build_MergesSmartIgnoreCaseInsensitive()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "bin/");

		var smartResult = new SmartIgnoreResult(
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Cache" },
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Thumbs.DB" });
		var smart = new SmartIgnoreService([new StubSmartIgnoreRule(smartResult)]);
		var service = new IgnoreRulesService(smart);

		var rules = service.Build(temp.Path, [IgnoreOptionId.SmartIgnore]);

		Assert.Contains("cache", rules.SmartIgnoredFolders);
		Assert.Contains("thumbs.db", rules.SmartIgnoredFiles);
	}

	// Verifies all selected options enable all ignore flags.
	[Fact]
	public void Build_SetsAllFlagsWhenAllOptionsSelected()
	{
		var smart = new SmartIgnoreService(new ISmartIgnoreRule[0]);
		var service = new IgnoreRulesService(smart);

		var rules = service.Build("/root", [
			IgnoreOptionId.HiddenFolders,
			IgnoreOptionId.HiddenFiles,
			IgnoreOptionId.HiddenFolders,
			IgnoreOptionId.HiddenFiles,
			IgnoreOptionId.DotFolders,
			IgnoreOptionId.DotFiles
		]);

		Assert.True(rules.IgnoreHiddenFolders);
		Assert.True(rules.IgnoreHiddenFiles);
		Assert.True(rules.IgnoreHiddenFolders);
		Assert.True(rules.IgnoreHiddenFiles);
		Assert.True(rules.IgnoreDotFolders);
		Assert.True(rules.IgnoreDotFiles);
	}

	// Verifies SmartIgnore is disabled when it is not selected.
	[Fact]
	public void Build_DisablesSmartIgnoreWhenGitIgnoreNotSelected()
	{
		var smartResult = new SmartIgnoreResult(
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cache" },
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "thumbs.db" });
		var smart = new SmartIgnoreService([new StubSmartIgnoreRule(smartResult)]);
		var service = new IgnoreRulesService(smart);

		var rules = service.Build("/root", []);

		// SmartIgnore should be empty when UseGitIgnore is not selected
		Assert.Empty(rules.SmartIgnoredFolders);
		Assert.Empty(rules.SmartIgnoredFiles);
	}

	// Verifies SmartIgnore works independently from GitIgnore.
	[Fact]
	public void Build_UsesSmartIgnoreWhenSmartIgnoreSelected()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "bin/");

		var smartResult = new SmartIgnoreResult(
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cache" },
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "thumbs.db" });
		var smart = new SmartIgnoreService([new StubSmartIgnoreRule(smartResult)]);
		var service = new IgnoreRulesService(smart);

		var rules = service.Build(temp.Path, [IgnoreOptionId.SmartIgnore]);

		Assert.Contains("cache", rules.SmartIgnoredFolders);
		Assert.Contains("thumbs.db", rules.SmartIgnoredFiles);
	}
}

