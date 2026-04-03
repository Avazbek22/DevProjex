namespace DevProjex.Tests.Integration;

public sealed class FileSystemScannerEntryEnumerationMetadataIntegrationTests
{
	[Fact]
	public void GetRootFileExtensionsWithIgnoreOptionCounts_UsesEntryMetadataForRootFiles()
	{
		using var temp = new TemporaryDirectory();
		WriteFile(temp.Path, "Program.cs", "class Program {}");
		WriteFile(temp.Path, "README", "docs");
		WriteFile(temp.Path, ".env", "secret");
		WriteFile(temp.Path, "empty.txt", string.Empty);

		var scanner = new FileSystemScanner();
		var result = scanner.GetRootFileExtensionsWithIgnoreOptionCounts(temp.Path, CreateBaseRules());

		AssertSetEquals(
			new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				".cs",
				"README",
				".env",
				".txt"
			},
			result.Value.Extensions);
		Assert.Equal(1, result.Value.IgnoreOptionCounts.DotFiles);
		Assert.Equal(1, result.Value.IgnoreOptionCounts.EmptyFiles);
		Assert.Equal(1, result.Value.IgnoreOptionCounts.ExtensionlessFiles);
	}

	[Fact]
	public void GetExtensionsWithIgnoreOptionCounts_UsesEntryMetadataForNestedFiles()
	{
		using var temp = new TemporaryDirectory();
		WriteFile(temp.Path, "src/app.cs", "class App {}");
		WriteFile(temp.Path, "src/README", "docs");
		WriteFile(temp.Path, "src/.env", "secret");
		WriteFile(temp.Path, "src/empty.txt", string.Empty);

		var scanner = new FileSystemScanner();
		var result = scanner.GetExtensionsWithIgnoreOptionCounts(
			Path.Combine(temp.Path, "src"),
			CreateBaseRules());

		AssertSetEquals(
			new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				".cs",
				"README",
				".env",
				".txt"
			},
			result.Value.Extensions);
		Assert.Equal(1, result.Value.IgnoreOptionCounts.DotFiles);
		Assert.Equal(1, result.Value.IgnoreOptionCounts.EmptyFiles);
		Assert.Equal(1, result.Value.IgnoreOptionCounts.ExtensionlessFiles);
	}

	[Fact]
	public void GetRootFolderNames_IgnoresDotFoldersWithoutExtraMetadataLookups()
	{
		using var temp = new TemporaryDirectory();
		Directory.CreateDirectory(Path.Combine(temp.Path, "src"));
		Directory.CreateDirectory(Path.Combine(temp.Path, ".cache"));

		var scanner = new FileSystemScanner();
		var rules = CreateBaseRules() with { IgnoreDotFolders = true };

		var result = scanner.GetRootFolderNames(temp.Path, rules);

		Assert.Equal(["src"], result.Value);
	}

	[Fact]
	public void GetRootFolderNames_IgnoresHiddenFolders_WhenPlatformSupportsHiddenAttributes()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		Directory.CreateDirectory(Path.Combine(temp.Path, "src"));
		var hiddenFolderPath = Path.Combine(temp.Path, "hidden");
		Directory.CreateDirectory(hiddenFolderPath);
		File.SetAttributes(hiddenFolderPath, File.GetAttributes(hiddenFolderPath) | FileAttributes.Hidden);

		var scanner = new FileSystemScanner();
		var rules = CreateBaseRules() with { IgnoreHiddenFolders = true };

		var result = scanner.GetRootFolderNames(temp.Path, rules);

		Assert.Equal(["src"], result.Value);
	}

	[Fact]
	public void GetIgnoreSectionSnapshot_RootFilesRespectEntryMetadataInEffectiveCounts()
	{
		using var temp = new TemporaryDirectory();
		WriteFile(temp.Path, ".env", "secret");
		WriteFile(temp.Path, "README", "docs");
		WriteFile(temp.Path, "empty.txt", string.Empty);

		var scanner = new FileSystemScanner();
		var discoveryRules = CreateBaseRules();
		var effectiveRules = CreateBaseRules() with
		{
			IgnoreDotFiles = true,
			IgnoreEmptyFiles = true,
			IgnoreExtensionlessFiles = true
		};

		var result = scanner.GetRootFileIgnoreSectionSnapshot(
			temp.Path,
			discoveryRules,
			effectiveRules,
			effectiveAllowedExtensions: null);

		Assert.Equal(new IgnoreOptionCounts(DotFiles: 1, ExtensionlessFiles: 1, EmptyFiles: 1), result.Value.EffectiveIgnoreOptionCounts);
	}

	private static IgnoreRules CreateBaseRules() => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(),
		SmartIgnoredFiles: new HashSet<string>());

	private static string WriteFile(string rootPath, string relativePath, string content)
	{
		var fullPath = Path.Combine(rootPath, relativePath);
		var directoryPath = Path.GetDirectoryName(fullPath);
		if (!string.IsNullOrWhiteSpace(directoryPath))
			Directory.CreateDirectory(directoryPath);

		File.WriteAllText(fullPath, content);
		return fullPath;
	}

	private static void AssertSetEquals(
		IReadOnlySet<string> expected,
		IReadOnlySet<string> actual)
	{
		Assert.True(
			expected.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
				.SequenceEqual(actual.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
	}
}
