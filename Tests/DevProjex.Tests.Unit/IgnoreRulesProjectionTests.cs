namespace DevProjex.Tests.Unit;

public sealed class IgnoreRulesProjectionTests
{
	[Fact]
	public void ForExtensionAvailability_RelaxesOnlyFileLevelRules()
	{
		var scopedGitMatcher = new ScopedGitIgnoreMatcher(
			@"C:\workspace\project",
			GitIgnoreMatcher.Empty);
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: true,
			IgnoreHiddenFiles: true,
			IgnoreDotFolders: true,
			IgnoreDotFiles: true,
			SmartIgnoredFolders: new HashSet<string> { "bin" },
			SmartIgnoredFiles: new HashSet<string> { "generated.cs" })
		{
			UseGitIgnore = true,
			EnableGitIgnoreTraversal = true,
			UseSmartIgnore = true,
			IgnoreEmptyFolders = true,
			IgnoreEmptyFiles = true,
			IgnoreExtensionlessFiles = true,
			ScopedGitIgnoreMatchers = [scopedGitMatcher],
			SmartIgnoreScopeRoots = [@"C:\workspace\project"]
		};

		var projection = IgnoreRulesProjection.ForExtensionAvailability(rules);

		Assert.True(projection.IgnoreHiddenFolders);
		Assert.True(projection.IgnoreDotFolders);
		Assert.True(projection.IgnoreEmptyFolders);
		Assert.True(projection.UseGitIgnore);
		Assert.True(projection.EnableGitIgnoreTraversal);
		Assert.True(projection.UseSmartIgnore);
		Assert.Same(rules.SmartIgnoredFolders, projection.SmartIgnoredFolders);
		Assert.Same(rules.SmartIgnoredFiles, projection.SmartIgnoredFiles);
		Assert.Same(rules.ScopedGitIgnoreMatchers, projection.ScopedGitIgnoreMatchers);
		Assert.Same(rules.SmartIgnoreScopeRoots, projection.SmartIgnoreScopeRoots);
		Assert.False(projection.IgnoreHiddenFiles);
		Assert.False(projection.IgnoreDotFiles);
		Assert.False(projection.IgnoreEmptyFiles);
		Assert.False(projection.IgnoreExtensionlessFiles);
	}

	[Fact]
	public void ForExtensionAvailability_AlreadyRelaxedRulesReuseSameInstance()
	{
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: true,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: true,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>())
		{
			IgnoreEmptyFolders = true,
			IgnoreEmptyFiles = false,
			IgnoreExtensionlessFiles = false
		};

		var projection = IgnoreRulesProjection.ForExtensionAvailability(rules);

		Assert.Same(rules, projection);
	}
}
