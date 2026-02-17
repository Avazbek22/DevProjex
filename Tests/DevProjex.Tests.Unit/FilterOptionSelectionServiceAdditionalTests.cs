using DevProjex.Application.Services;

namespace DevProjex.Tests.Unit;

public sealed class FilterOptionSelectionServiceAdditionalTests
{
	[Theory]
	// Verifies no extensions are selected when no previous selections exist.
	[InlineData(".cs", false)]
	[InlineData(".sln", false)]
	[InlineData(".csproj", false)]
	[InlineData(".designer", false)]
	[InlineData(".txt", false)]
	[InlineData(".md", false)]
	[InlineData(".json", false)]
	[InlineData(".png", false)]
	[InlineData(".yaml", false)]
	[InlineData(".xml", false)]
	public void BuildExtensionOptions_DefaultSelections(string extension, bool expectedChecked)
	{
		var service = new FilterOptionSelectionService();
		var options = service.BuildExtensionOptions(
			new[] { ".txt", ".csproj", ".cs", ".sln", ".designer", ".md", ".json", ".png", ".yaml", ".xml" },
			new HashSet<string>(StringComparer.OrdinalIgnoreCase));

		var target = options.Single(option => option.Name.Equals(extension, StringComparison.OrdinalIgnoreCase));

		Assert.Equal(expectedChecked, target.IsChecked);
	}

	[Theory]
	// Verifies previous selections override default-extension auto-selection.
	[InlineData(".cs", ".cs", true)]
	[InlineData(".cs", ".txt", false)]
	[InlineData(".txt", ".txt", true)]
	[InlineData(".md", ".txt", false)]
	[InlineData(".sln;.md", ".sln", true)]
	[InlineData(".sln;.md", ".md", true)]
	[InlineData(".sln;.md", ".cs", false)]
	[InlineData(".designer", ".designer", true)]
	[InlineData(".designer", ".csproj", false)]
	[InlineData(".csproj;.xml", ".xml", true)]
	[InlineData(".csproj;.xml", ".csproj", true)]
	[InlineData(".csproj;.xml", ".sln", false)]
	public void BuildExtensionOptions_RespectsPreviousSelections(string previousSelections, string extension, bool expectedChecked)
	{
		var service = new FilterOptionSelectionService();
		var previous = previousSelections.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		var options = service.BuildExtensionOptions(
			new[] { ".txt", ".csproj", ".cs", ".sln", ".designer", ".md", ".xml" },
			new HashSet<string>(previous, StringComparer.OrdinalIgnoreCase));

		var target = options.Single(option => option.Name.Equals(extension, StringComparison.OrdinalIgnoreCase));

		Assert.Equal(expectedChecked, target.IsChecked);
	}

	[Fact]
	// Verifies extension options are sorted case-insensitively.
	public void BuildExtensionOptions_SortsCaseInsensitive()
	{
		var service = new FilterOptionSelectionService();
		var options = service.BuildExtensionOptions(
			new[] { ".b", ".A", ".c", ".aa" },
			new HashSet<string>(StringComparer.OrdinalIgnoreCase));

		var ordered = options.Select(option => option.Name).ToList();

		Assert.Equal(new[] { ".A", ".aa", ".b", ".c" }, ordered);
	}

	[Theory]
	// Verifies ignored folders are unchecked when no previous selections exist.
	[InlineData("bin", true)]
	[InlineData("obj", true)]
	[InlineData(".git", false)]
	[InlineData("node_modules", false)]
	[InlineData("src", true)]
	[InlineData("docs", true)]
	[InlineData("build", true)]
	[InlineData("Assets", true)]
	[InlineData(".idea", false)]
	[InlineData(".vscode", false)]
	public void BuildRootFolderOptions_DefaultsToNonIgnored(string folderName, bool expectedChecked)
	{
		var service = new FilterOptionSelectionService();
		var rules = new IgnoreRules(IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: true,
			IgnoreDotFiles: true,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules", ".idea", ".vscode" },
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

		var options = service.BuildRootFolderOptions(
			new[] { "bin", "obj", ".git", "node_modules", "src", "docs", "build", "Assets", ".idea", ".vscode" },
			new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			rules);

		var target = options.Single(option => option.Name.Equals(folderName, StringComparison.OrdinalIgnoreCase));

		Assert.Equal(expectedChecked, target.IsChecked);
	}

	[Theory]
	// Verifies previous selections control root-folder selections regardless of ignore rules.
	[InlineData("src;docs", "src", true)]
	[InlineData("src;docs", "docs", true)]
	[InlineData("src;docs", "bin", false)]
	[InlineData("bin", "bin", true)]
	[InlineData("bin", "src", false)]
	[InlineData(".git", ".git", true)]
	[InlineData(".git", "node_modules", false)]
	[InlineData("node_modules", "node_modules", true)]
	public void BuildRootFolderOptions_RespectsPreviousSelections(string previousSelections, string folderName, bool expectedChecked)
	{
		var service = new FilterOptionSelectionService();
		var rules = new IgnoreRules(IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: true,
			IgnoreDotFiles: true,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules" },
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

		var options = service.BuildRootFolderOptions(
			new[] { "bin", "obj", ".git", "node_modules", "src", "docs" },
			new HashSet<string>(
				previousSelections.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
				StringComparer.OrdinalIgnoreCase),
			rules);

		var target = options.Single(option => option.Name.Equals(folderName, StringComparison.OrdinalIgnoreCase));

		Assert.Equal(expectedChecked, target.IsChecked);
	}
}




