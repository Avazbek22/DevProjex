namespace DevProjex.Tests.Integration;

public sealed class ExtensionlessIgnoreCountsIntegrationTests
{
	[Fact]
	public void GetExtensionsAndIgnoreCountsForRootFolders_ExtensionlessCount_TracksFilesNotUniqueTokens()
	{
		using var temp = new TemporaryDirectory();
		SeedWorkspaceWithDuplicateExtensionlessNames(temp);

		var useCase = new ScanOptionsUseCase(new FileSystemScanner());
		var result = useCase.GetExtensionsAndIgnoreCountsForRootFolders(
			temp.Path,
			["src", "tests"],
			CreateRules(ignoreExtensionlessFiles: false), cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(4, result.Value.IgnoreOptionCounts.ExtensionlessFiles);
		Assert.Equal(2, CountExtensionlessTokens(result.Value.Extensions));
		Assert.Contains("Dockerfile", result.Value.Extensions);
		Assert.Contains("Makefile", result.Value.Extensions);
	}

	[Fact]
	public void GetExtensionsAndIgnoreCountsForRootFolders_ExtensionlessCount_PreservedWhenFilteringEnabled()
	{
		using var temp = new TemporaryDirectory();
		SeedWorkspaceWithDuplicateExtensionlessNames(temp);

		var useCase = new ScanOptionsUseCase(new FileSystemScanner());
		var result = useCase.GetExtensionsAndIgnoreCountsForRootFolders(
			temp.Path,
			["src", "tests"],
			CreateRules(ignoreExtensionlessFiles: true), cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(4, result.Value.IgnoreOptionCounts.ExtensionlessFiles);
		Assert.DoesNotContain("Dockerfile", result.Value.Extensions);
		Assert.DoesNotContain("Makefile", result.Value.Extensions);
	}

	[Fact]
	public void GetExtensionsAndIgnoreCountsForRootFolders_ExtensionlessCount_RespectsSelectedRoots()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Dockerfile", "root");
		temp.CreateFile("README", "root");
		temp.CreateFile("src/Makefile", "nested");
		temp.CreateFile("tests/LICENSE", "nested");

		var useCase = new ScanOptionsUseCase(new FileSystemScanner());
		var result = useCase.GetExtensionsAndIgnoreCountsForRootFolders(
			temp.Path,
			[],
			CreateRules(ignoreExtensionlessFiles: false), cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(2, result.Value.IgnoreOptionCounts.ExtensionlessFiles);
		Assert.Equal(2, CountExtensionlessTokens(result.Value.Extensions));
		Assert.Contains("Dockerfile", result.Value.Extensions);
		Assert.Contains("README", result.Value.Extensions);
	}

	private static void SeedWorkspaceWithDuplicateExtensionlessNames(TemporaryDirectory temp)
	{
		temp.CreateFile("Dockerfile", "root");
		temp.CreateFile("src/Dockerfile", "src");
		temp.CreateFile("src/Makefile", "src");
		temp.CreateFile("tests/Dockerfile", "tests");
		temp.CreateFile("tests/app.cs", "class App { }");
	}

	private static IgnoreRules CreateRules(bool ignoreExtensionlessFiles) => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(),
		SmartIgnoredFiles: new HashSet<string>())
	{
		IgnoreExtensionlessFiles = ignoreExtensionlessFiles
	};

	private static int CountExtensionlessTokens(IEnumerable<string> entries)
	{
		var count = 0;
		foreach (var entry in entries)
		{
			if (string.IsNullOrWhiteSpace(entry))
				continue;

			var extension = Path.GetExtension(entry);
			if (string.IsNullOrEmpty(extension) || extension == ".")
				count++;
		}

		return count;
	}
}
