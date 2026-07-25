namespace DevProjex.Tests.Integration;

public sealed class FileSystemScannerFilenameEdgeMatrixIntegrationTests
{
	public static IEnumerable<object[]> FilenameAndToggleCases()
	{
		foreach (var (fileName, expectedExtensionless) in BuildFilenameCases())
		{
			foreach (var ignoreExtensionless in new[] { false, true })
				yield return [fileName, expectedExtensionless, ignoreExtensionless];
		}
	}

	[Theory]
	[MemberData(nameof(FilenameAndToggleCases))]
	public void RootFileScan_FilenameClassification_MatchesExpected(
		string fileName,
		bool expectedExtensionless,
		bool ignoreExtensionless)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(fileName, "x");

		var rules = CreateRules(ignoreExtensionless);
		var scanner = new FileSystemScanner();

		var rootResult = scanner.GetRootFileExtensionsWithIgnoreOptionCounts(temp.Path, rules, cancellationToken: TestContext.Current.CancellationToken);
		var fullResult = scanner.GetExtensionsWithIgnoreOptionCounts(temp.Path, rules, cancellationToken: TestContext.Current.CancellationToken);

		var expectedCount = expectedExtensionless ? 1 : 0;
		Assert.Equal(expectedCount, rootResult.Value.IgnoreOptionCounts.ExtensionlessFiles);
		Assert.Equal(expectedCount, fullResult.Value.IgnoreOptionCounts.ExtensionlessFiles);

		if (expectedExtensionless)
		{
			Assert.Equal(!ignoreExtensionless, rootResult.Value.Extensions.Contains(fileName));
			Assert.Equal(!ignoreExtensionless, fullResult.Value.Extensions.Contains(fileName));
		}
		else
		{
			var extension = Path.GetExtension(fileName);
			Assert.False(string.IsNullOrWhiteSpace(extension));
			Assert.Contains(extension, rootResult.Value.Extensions);
			Assert.Contains(extension, fullResult.Value.Extensions);
		}
	}

	[Theory]
	[InlineData(false, false, false, false)]
	[InlineData(true, false, false, false)]
	[InlineData(false, true, false, false)]
	[InlineData(false, false, true, false)]
	[InlineData(false, false, false, true)]
	[InlineData(true, true, false, false)]
	[InlineData(true, false, true, false)]
	[InlineData(true, false, false, true)]
	[InlineData(false, true, true, false)]
	[InlineData(false, true, false, true)]
	[InlineData(false, false, true, true)]
	[InlineData(true, true, true, false)]
	[InlineData(true, true, false, true)]
	[InlineData(true, false, true, true)]
	[InlineData(false, true, true, true)]
	[InlineData(true, true, true, true)]
	public void FullScan_IgnoreToggleCombinations_KeepInventoryCountsStable(
		bool ignoreHiddenFiles,
		bool ignoreDotFiles,
		bool ignoreExtensionless,
		bool ignoreHiddenFolders)
	{
		using var temp = new TemporaryDirectory();
		var hiddenFilePath = temp.CreateFile("hidden-no-ext", "x");
		var dotFilePath = temp.CreateFile(".dot-no-ext", "x");
		temp.CreateFile("normal.txt", "x");
		temp.CreateFile("folder/.env", "x");
		var hiddenDirPath = temp.CreateDirectory("hidden-dir");
		temp.CreateFile("hidden-dir/a.txt", "x");

		if (OperatingSystem.IsWindows())
		{
			File.SetAttributes(hiddenFilePath, File.GetAttributes(hiddenFilePath) | FileAttributes.Hidden);
			File.SetAttributes(hiddenDirPath, File.GetAttributes(hiddenDirPath) | FileAttributes.Hidden);
		}

		var rules = new IgnoreRules(
			IgnoreHiddenFolders: ignoreHiddenFolders,
			IgnoreHiddenFiles: ignoreHiddenFiles,
			IgnoreDotFolders: false,
			IgnoreDotFiles: ignoreDotFiles,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>())
		{
			IgnoreExtensionlessFiles = ignoreExtensionless
		};

		var scanner = new FileSystemScanner();
		var result = scanner.GetExtensionsWithIgnoreOptionCounts(temp.Path, rules, cancellationToken: TestContext.Current.CancellationToken);

		// Inventory counters should not depend on visibility toggles.
		Assert.Equal(1, result.Value.IgnoreOptionCounts.ExtensionlessFiles);
		Assert.Equal(2, result.Value.IgnoreOptionCounts.DotFiles);

		if (OperatingSystem.IsWindows())
		{
			Assert.True(result.Value.IgnoreOptionCounts.HiddenFiles >= 1);
			Assert.True(result.Value.IgnoreOptionCounts.HiddenFolders >= 1);
		}
	}

	[Fact]
	public void ExtensionDiscovery_ParallelCaseVariantsUseOneCanonicalValue()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("alpha/one.CS", "x");
		temp.CreateFile("beta/two.cs", "x");
		temp.CreateFile("gamma/three.Cs", "x");
		var scanner = new FileSystemScanner();
		var rules = CreateRules(ignoreExtensionless: false);

		for (var iteration = 0; iteration < 20; iteration++)
		{
			var result = scanner.GetExtensionsWithIgnoreOptionCounts(
				temp.Path,
				rules,
				TestContext.Current.CancellationToken);

			Assert.Equal(".cs", Assert.Single(result.Value.Extensions));
		}
	}

	private static IgnoreRules CreateRules(bool ignoreExtensionless)
	{
		return new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>())
		{
			IgnoreExtensionlessFiles = ignoreExtensionless
		};
	}

	private static IEnumerable<(string FileName, bool Extensionless)> BuildFilenameCases()
	{
		var stems = new[]
		{
			"alpha", "beta", "gamma", "docker", "license", "makefile", "module", "config", "index", "readme"
		};

		foreach (var stem in stems)
		{
			yield return ($"{stem}", true);
			yield return ($"{stem}.txt", false);
			yield return ($".{stem}", false);
			yield return ($"{stem}.{stem}", false);
			yield return ($"{stem}..{stem}", false);
			yield return ($".{stem}.{stem}", false);
			yield return ($"{stem.ToUpperInvariant()}", true);
			yield return ($"{stem}_UPPER.TXT", false);
		}
	}
}
