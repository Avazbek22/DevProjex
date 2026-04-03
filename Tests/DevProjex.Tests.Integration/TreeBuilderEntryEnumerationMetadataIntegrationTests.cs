namespace DevProjex.Tests.Integration;

public sealed class TreeBuilderEntryEnumerationMetadataIntegrationTests
{
	[Fact]
	public void Build_UsesEntryMetadata_ForDotAndExtensionlessAndEmptyRules()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/.env", "secret");
		temp.CreateFile("src/README", "docs");
		temp.CreateFile("src/empty.txt", string.Empty);
		temp.CreateFile("src/keep.cs", "class Keep {}");

		var builder = new TreeBuilder();
		var result = builder.Build(
			temp.Path,
			new TreeFilterOptions(
				AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".txt" },
				AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src" },
				IgnoreRules: new IgnoreRules(
					IgnoreHiddenFolders: false,
					IgnoreHiddenFiles: false,
					IgnoreDotFolders: false,
					IgnoreDotFiles: true,
					SmartIgnoredFolders: new HashSet<string>(),
					SmartIgnoredFiles: new HashSet<string>())
				{
					IgnoreExtensionlessFiles = true,
					IgnoreEmptyFiles = true
				}));

		var src = Assert.Single(result.Root.Children);
		Assert.Equal("src", src.Name);
		Assert.True(src.Children.Select(child => child.Name).SequenceEqual(["keep.cs"]));
	}

	[Fact]
	public void Build_UsesEntryMetadata_ForHiddenDirectories_WhenPlatformSupportsHiddenAttributes()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		var hiddenFolderPath = Path.Combine(temp.Path, "hidden");
		Directory.CreateDirectory(hiddenFolderPath);
		temp.CreateFile("hidden/file.cs", "class Hidden {}");
		temp.CreateFile("visible/file.cs", "class Visible {}");
		File.SetAttributes(hiddenFolderPath, File.GetAttributes(hiddenFolderPath) | FileAttributes.Hidden);

		var builder = new TreeBuilder();
		var result = builder.Build(
			temp.Path,
			new TreeFilterOptions(
				AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
				AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hidden", "visible" },
				IgnoreRules: new IgnoreRules(
					IgnoreHiddenFolders: true,
					IgnoreHiddenFiles: false,
					IgnoreDotFolders: false,
					IgnoreDotFiles: false,
					SmartIgnoredFolders: new HashSet<string>(),
					SmartIgnoredFiles: new HashSet<string>())));

		Assert.True(result.Root.Children.Select(child => child.Name).SequenceEqual(["visible"]));
	}
}
