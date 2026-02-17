using DevProjex.Application.Services;

namespace DevProjex.Tests.Unit;

public sealed class FilterOptionSelectionServiceTests
{
	// Verifies no extensions are pre-selected when there are no prior selections.
	[Fact]
	public void BuildExtensionOptions_DoesNotSelectWhenNoPrevious()
	{
		var service = new FilterOptionSelectionService();

		var options = service.BuildExtensionOptions(new[] { ".txt", ".cs", ".sln" }, new HashSet<string>());

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

		var options = service.BuildExtensionOptions(new[] { ".B", ".a" }, new HashSet<string>());

		Assert.Equal(".a", options[0].Name);
		Assert.Equal(".B", options[1].Name);
	}

	// Verifies prior selections override default extension choices.
	[Fact]
	public void BuildExtensionOptions_RespectsPreviousSelections()
	{
		var service = new FilterOptionSelectionService();
		var previous = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" };

		var options = service.BuildExtensionOptions(new[] { ".txt", ".cs" }, previous);

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
			SmartIgnoredFiles: new HashSet<string>());

		var options = service.BuildRootFolderOptions(new[] { "bin", "obj", "logs", ".cache", "src" }, new HashSet<string>(), rules);

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

		var options = service.BuildRootFolderOptions(new[] { "bin", "src" }, previous, rules);

		Assert.True(options.Single(o => o.Name == "bin").IsChecked);
		Assert.False(options.Single(o => o.Name == "src").IsChecked);
	}

	// Verifies previous selections are applied case-insensitively.
	[Fact]
	public void BuildExtensionOptions_RespectsPreviousSelectionsCaseInsensitive()
	{
		var service = new FilterOptionSelectionService();
		var previous = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".CS" };

		var options = service.BuildExtensionOptions(new[] { ".cs" }, previous);

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

		var options = service.BuildRootFolderOptions(new[] { "bin", "src" }, previous, rules);

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

		var options = service.BuildRootFolderOptions(new[] { "b", "a" }, new HashSet<string>(), rules);

		Assert.Equal("b", options[0].Name);
		Assert.Equal("a", options[1].Name);
	}

	// Verifies empty extension list yields empty options.
	[Fact]
	public void BuildExtensionOptions_ReturnsEmptyWhenNoExtensions()
	{
		var service = new FilterOptionSelectionService();

		var options = service.BuildExtensionOptions(Array.Empty<string>(), new HashSet<string>());

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

		var options = service.BuildRootFolderOptions(Array.Empty<string>(), new HashSet<string>(), rules);

		Assert.Empty(options);
	}

	// Verifies previous selections not present do not auto-select new options.
	[Fact]
	public void BuildExtensionOptions_IgnoresPreviousSelectionsNotInList()
	{
		var service = new FilterOptionSelectionService();
		var previous = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".missing" };

		var options = service.BuildExtensionOptions(new[] { ".cs" }, previous);

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
			SmartIgnoredFiles: new HashSet<string>());

		var options = service.BuildRootFolderOptions(new[] { "cache", "src" }, new HashSet<string>(), rules);

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

		var options = service.BuildRootFolderOptions(new[] { ".git", "src" }, new HashSet<string>(), rules);

		Assert.False(options.Single(o => o.Name == ".git").IsChecked);
		Assert.True(options.Single(o => o.Name == "src").IsChecked);
	}
}




