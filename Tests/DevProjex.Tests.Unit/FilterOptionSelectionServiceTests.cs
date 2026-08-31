namespace DevProjex.Tests.Unit;

public sealed class FilterOptionSelectionServiceTests
{
	// Verifies no extensions are pre-selected when there are no prior selections.
	[Fact]
	public void BuildExtensionOptions_DoesNotSelectWhenNoPrevious()
	{
		var service = new FilterOptionSelectionService();

		var options = service.BuildExtensionOptions([".txt", ".cs", ".sln"], new HashSet<string>());

		Assert.Equal(3, options.Count);
		Assert.False(options.Single(o => o.Name == ".cs").IsChecked);
		Assert.False(options.Single(o => o.Name == ".sln").IsChecked);
		Assert.False(options.Single(o => o.Name == ".txt").IsChecked);
	}

	// Verifies extension options are sorted by name using an ordinal ignore-case comparer.
	[Fact]
	public void BuildExtensionOptions_SortsExtensionsCaseInsensitive()
	{
		var service = new FilterOptionSelectionService();

		var options = service.BuildExtensionOptions([".B", ".a"], new HashSet<string>());

		Assert.Equal(".a", options[0].Name);
		Assert.Equal(".B", options[1].Name);
	}

	// Verifies prior selections override default extension choices.
	[Fact]
	public void BuildExtensionOptions_RespectsPreviousSelections()
	{
		var service = new FilterOptionSelectionService();
		var previous = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" };

		var options = service.BuildExtensionOptions([".txt", ".cs"], previous);

		Assert.True(options.Single(o => o.Name == ".txt").IsChecked);
		Assert.False(options.Single(o => o.Name == ".cs").IsChecked);
	}

	// Verifies ignored folders are not pre-selected when no prior selections exist.
	[Fact]
	public void BuildRootFolderOptions_ExcludesIgnoredWhenNoPrevious()
	{
		var service = new FilterOptionSelectionService();
		var rules = new IgnoreRules(IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: true,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "logs" },
			SmartIgnoredFiles: new HashSet<string>())
		{
			UseSmartIgnore = true
		};

		var options = service.BuildRootFolderOptions(["bin", "obj", "logs", ".cache", "src"], new HashSet<string>(), rules);

		Assert.True(options.Single(o => o.Name == "bin").IsChecked);
		Assert.True(options.Single(o => o.Name == "obj").IsChecked);
		Assert.False(options.Single(o => o.Name == "logs").IsChecked);
		Assert.False(options.Single(o => o.Name == ".cache").IsChecked);
		Assert.True(options.Single(o => o.Name == "src").IsChecked);
	}

	// Verifies explicit previous folder selections are honored.
	[Fact]
	public void BuildRootFolderOptions_RespectsPreviousSelections()
	{
		var service = new FilterOptionSelectionService();
		var rules = new IgnoreRules(IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: true,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>());
		var previous = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin" };

		var options = service.BuildRootFolderOptions(["bin", "src"], previous, rules);

		Assert.True(options.Single(o => o.Name == "bin").IsChecked);
		Assert.False(options.Single(o => o.Name == "src").IsChecked);
	}

	// Verifies previous selections are applied case-insensitively.
	[Fact]
	public void BuildExtensionOptions_RespectsPreviousSelectionsCaseInsensitive()
	{
		var service = new FilterOptionSelectionService();
		var previous = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".CS" };

		var options = service.BuildExtensionOptions([".cs"], previous);

		Assert.True(options.Single(o => o.Name == ".cs").IsChecked);
	}

	// Verifies explicit selection keeps ignored folders checked.
	[Fact]
	public void BuildRootFolderOptions_RespectsExplicitSelectionEvenIfIgnored()
	{
		var service = new FilterOptionSelectionService();
		var rules = new IgnoreRules(IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>());
		var previous = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin" };

		var options = service.BuildRootFolderOptions(["bin", "src"], previous, rules);

		Assert.True(options.Single(o => o.Name == "bin").IsChecked);
	}

	// Verifies root folder options preserve the input order.
	[Fact]
	public void BuildRootFolderOptions_PreservesInputOrder()
	{
		var service = new FilterOptionSelectionService();
		var rules = new IgnoreRules(IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>());

		var options = service.BuildRootFolderOptions(["b", "a"], new HashSet<string>(), rules);

		Assert.Equal("b", options[0].Name);
		Assert.Equal("a", options[1].Name);
	}

	// Verifies empty extension list yields empty options.
	[Fact]
	public void BuildExtensionOptions_ReturnsEmptyWhenNoExtensions()
	{
		var service = new FilterOptionSelectionService();

		var options = service.BuildExtensionOptions([], new HashSet<string>());

		Assert.Empty(options);
	}

	// Verifies empty root folders yield empty options.
	[Fact]
	public void BuildRootFolderOptions_ReturnsEmptyWhenNoFolders()
	{
		var service = new FilterOptionSelectionService();
		var rules = new IgnoreRules(IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>());

		var options = service.BuildRootFolderOptions([], new HashSet<string>(), rules);

		Assert.Empty(options);
	}

	// Verifies previous selections not present do not auto-select new options.
	[Fact]
	public void BuildExtensionOptions_IgnoresPreviousSelectionsNotInList()
	{
		var service = new FilterOptionSelectionService();
		var previous = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".missing" };

		var options = service.BuildExtensionOptions([".cs"], previous);

		Assert.False(options.Single(o => o.Name == ".cs").IsChecked);
	}

	// Verifies smart ignored folders are excluded when no previous selection.
	[Fact]
	public void BuildRootFolderOptions_ExcludesSmartIgnoredFolders()
	{
		var service = new FilterOptionSelectionService();
		var rules = new IgnoreRules(IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Cache" },
			SmartIgnoredFiles: new HashSet<string>())
		{
			UseSmartIgnore = true
		};

		var options = service.BuildRootFolderOptions(["cache", "src"], new HashSet<string>(), rules);

		Assert.False(options.Single(o => o.Name == "cache").IsChecked);
		Assert.True(options.Single(o => o.Name == "src").IsChecked);
	}

	// Verifies dot folders are excluded when IgnoreDotFolders enabled.
	[Fact]
	public void BuildRootFolderOptions_ExcludesDotFolders()
	{
		var service = new FilterOptionSelectionService();
		var rules = new IgnoreRules(IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: true,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>());

		var options = service.BuildRootFolderOptions([".git", "src"], new HashSet<string>(), rules);

		Assert.False(options.Single(o => o.Name == ".git").IsChecked);
		Assert.True(options.Single(o => o.Name == "src").IsChecked);
	}

	[Fact]
	public void BuildRootFolderOptions_ExplicitEmptyPreviousSelections_DisablesDefaultChecks()
	{
		var service = new FilterOptionSelectionService();
		var rules = new IgnoreRules(IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>());

		var options = service.BuildRootFolderOptions(
			["src", "tests"],
			new HashSet<string>(),
			rules,
			hasPreviousSelections: true);

		Assert.All(options, option => Assert.False(option.IsChecked));
	}

	[Fact]
	public void BuildRootFolderOptions_CaseDistinctExactSelectionDoesNotSelectSibling()
	{
		var service = new FilterOptionSelectionService();
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>());

		var options = service.BuildRootFolderOptions(
			["Foo", "foo"],
			new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer) { "Foo" },
			rules,
			hasPreviousSelections: true);

		Assert.True(options.Single(option => option.Name == "Foo").IsChecked);
		Assert.False(options.Single(option => option.Name == "foo").IsChecked);
	}

	[Fact]
	public void BuildRootFolderOptions_AmbiguousCompatibilityAliasSelectsNeitherSibling()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("Compatibility aliases are Windows-only.");

		var service = new FilterOptionSelectionService();
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>());

		var options = service.BuildRootFolderOptions(
			["Foo", "foo"],
			new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer) { "FOO" },
			rules,
			hasPreviousSelections: true,
			previousStateCache: new Dictionary<string, bool>(ProjectTreePathIdentity.CanonicalComparer)
			{
				["FOO"] = true
			});

		Assert.All(options, option => Assert.False(option.IsChecked));
	}

	[Fact]
	public void BuildRootFolderOptions_UniqueCompatibilityAliasIsWindowsOnly()
	{
		var service = new FilterOptionSelectionService();
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>());

		var option = Assert.Single(service.BuildRootFolderOptions(
			["Foo"],
			new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer) { "foo" },
			rules,
			hasPreviousSelections: true));

		Assert.Equal(OperatingSystem.IsWindows(), option.IsChecked);
	}
}




