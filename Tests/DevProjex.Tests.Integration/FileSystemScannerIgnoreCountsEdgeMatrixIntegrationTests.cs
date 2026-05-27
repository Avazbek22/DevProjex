namespace DevProjex.Tests.Integration;

public sealed class FileSystemScannerIgnoreCountsEdgeMatrixIntegrationTests
{
	private static readonly IgnoreRules BaselineRules = new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(),
		SmartIgnoredFiles: new HashSet<string>());

	public static IEnumerable<object[]> RootCountCases()
	{
		yield return [".dot-root-empty", ScenarioKind.DotRootEmpty];
		yield return [".dot-root-extensionless", ScenarioKind.DotRootWithExtensionlessFile];
		yield return ["normal-root-dotfiles", ScenarioKind.NormalRootWithDotFiles];
		yield return ["normal-root-dotdir", ScenarioKind.NormalRootWithDotDirectory];
		yield return ["normal-root-nested-extless", ScenarioKind.NormalRootWithNestedExtensionlessFile];
	}

	[Theory]
	[MemberData(nameof(RootCountCases))]
	public void GetExtensionsWithIgnoreOptionCounts_RootSemantics_MatchExpected(
		string _,
		ScenarioKind scenario)
	{
		using var temp = new TemporaryDirectory();
		var rootPath = CreateScenario(temp, scenario);

		var scanner = new FileSystemScanner();
		var result = scanner.GetExtensionsWithIgnoreOptionCounts(rootPath, BaselineRules, cancellationToken: TestContext.Current.CancellationToken);
		var counts = result.Value.IgnoreOptionCounts;

		var expected = GetExpectedForFullScan(scenario);
		Assert.Equal(expected.DotFolders, counts.DotFolders);
		Assert.Equal(expected.DotFiles, counts.DotFiles);
		Assert.Equal(expected.EmptyFolders, counts.EmptyFolders);
		Assert.Equal(expected.ExtensionlessFiles, counts.ExtensionlessFiles);
	}

	[Theory]
	[MemberData(nameof(RootCountCases))]
	public void GetRootFileExtensionsWithIgnoreOptionCounts_RootSemantics_MatchExpected(
		string _,
		ScenarioKind scenario)
	{
		using var temp = new TemporaryDirectory();
		var rootPath = CreateScenario(temp, scenario);

		var scanner = new FileSystemScanner();
		var result = scanner.GetRootFileExtensionsWithIgnoreOptionCounts(rootPath, BaselineRules, cancellationToken: TestContext.Current.CancellationToken);
		var counts = result.Value.IgnoreOptionCounts;

		var expected = GetExpectedForRootFileScan(scenario);
		Assert.Equal(expected.DotFolders, counts.DotFolders);
		Assert.Equal(expected.DotFiles, counts.DotFiles);
		Assert.Equal(expected.EmptyFolders, counts.EmptyFolders);
		Assert.Equal(expected.ExtensionlessFiles, counts.ExtensionlessFiles);
	}

	public static IEnumerable<object[]> IgnoreToggleCases()
	{
		foreach (var ignoreDotFolders in new[] { false, true })
		{
			foreach (var ignoreDotFiles in new[] { false, true })
			{
				foreach (var ignoreExtensionless in new[] { false, true })
				{
					foreach (var ignoreEmptyFolders in new[] { false, true })
						yield return [ignoreDotFolders, ignoreDotFiles, ignoreExtensionless, ignoreEmptyFolders];
				}
			}
		}
	}

	[Theory]
	[MemberData(nameof(IgnoreToggleCases))]
	public void GetExtensionsWithIgnoreOptionCounts_CountsStayStableAcrossIgnoreToggles(
		bool ignoreDotFolders,
		bool ignoreDotFiles,
		bool ignoreExtensionlessFiles,
		bool ignoreEmptyFolders)
	{
		using var temp = new TemporaryDirectory();
		var rootPath = temp.CreateDirectory("workspace");
		temp.CreateFile("workspace/.env", "x");
		temp.CreateFile("workspace/README", "x");
		temp.CreateDirectory("workspace/.cache");

		var scanner = new FileSystemScanner();
		var rules = CreateRules(ignoreDotFolders, ignoreDotFiles, ignoreExtensionlessFiles, ignoreEmptyFolders);
		var result = scanner.GetExtensionsWithIgnoreOptionCounts(rootPath, rules, cancellationToken: TestContext.Current.CancellationToken);
		var counts = result.Value.IgnoreOptionCounts;

		// Dot and extensionless counters are inventory metrics.
		// EmptyFolders is derived from effective visibility/pruning and may vary by ignore flags.
		Assert.Equal(1, counts.DotFolders);
		Assert.Equal(1, counts.DotFiles);
		Assert.Equal(1, counts.ExtensionlessFiles);
		Assert.Equal(
			ExpectedEmptyFolders(ignoreDotFolders, ignoreDotFiles, ignoreExtensionlessFiles),
			counts.EmptyFolders);
	}

	public static IEnumerable<object[]> RootSelectionCases()
	{
		yield return ["none", new string[] { }, 1, 0, 0];
		yield return ["src-only", new[] { "src" }, 2, 0, 0];
		yield return ["tests-only", new[] { "tests" }, 1, 1, 0];
		yield return ["empty-only", new[] { "empty" }, 1, 0, 1];
		yield return ["src-tests-empty", new[] { "src", "tests", "empty" }, 2, 1, 1];
	}

	[Theory]
	[MemberData(nameof(RootSelectionCases))]
	public void GetExtensionsAndIgnoreCountsForRootFolders_CountsRespectSelectedRoots(
		string _,
		string[] selectedRoots,
		int expectedExtensionless,
		int expectedDotFiles,
		int expectedEmptyFolders)
	{
		using var temp = new TemporaryDirectory();
		SeedRootSelectionWorkspace(temp);

		var useCase = new ScanOptionsUseCase(new FileSystemScanner());
		var result = useCase.GetExtensionsAndIgnoreCountsForRootFolders(
			temp.Path,
			selectedRoots,
			BaselineRules, cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(expectedExtensionless, result.Value.IgnoreOptionCounts.ExtensionlessFiles);
		Assert.Equal(expectedDotFiles, result.Value.IgnoreOptionCounts.DotFiles);
		Assert.Equal(expectedEmptyFolders, result.Value.IgnoreOptionCounts.EmptyFolders);
	}

	private static IgnoreRules CreateRules(
		bool ignoreDotFolders,
		bool ignoreDotFiles,
		bool ignoreExtensionlessFiles,
		bool ignoreEmptyFolders)
	{
		return new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: ignoreDotFolders,
			IgnoreDotFiles: ignoreDotFiles,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>())
		{
			IgnoreExtensionlessFiles = ignoreExtensionlessFiles,
			IgnoreEmptyFolders = ignoreEmptyFolders
		};
	}

	private static void SeedRootSelectionWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile("README", "root file");
		temp.CreateFile("src/Dockerfile", "src extensionless");
		temp.CreateFile("tests/.env", "dot file");
		temp.CreateDirectory("empty");
	}

	private static string CreateScenario(TemporaryDirectory temp, ScenarioKind scenario)
	{
		return scenario switch
		{
			ScenarioKind.DotRootEmpty => temp.CreateDirectory(".dot-root-empty"),
			ScenarioKind.DotRootWithExtensionlessFile => CreateDotRootWithExtensionlessFile(temp),
			ScenarioKind.NormalRootWithDotFiles => CreateNormalRootWithDotFiles(temp),
			ScenarioKind.NormalRootWithDotDirectory => CreateNormalRootWithDotDirectory(temp),
			ScenarioKind.NormalRootWithNestedExtensionlessFile => CreateNormalRootWithNestedExtensionless(temp),
			_ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unsupported scenario")
		};
	}

	private static (int DotFolders, int DotFiles, int EmptyFolders, int ExtensionlessFiles) GetExpectedForFullScan(
		ScenarioKind scenario)
	{
		return scenario switch
		{
			ScenarioKind.DotRootEmpty => (DotFolders: 1, DotFiles: 0, EmptyFolders: 1, ExtensionlessFiles: 0),
			ScenarioKind.DotRootWithExtensionlessFile => (DotFolders: 1, DotFiles: 0, EmptyFolders: 0, ExtensionlessFiles: 1),
			ScenarioKind.NormalRootWithDotFiles => (DotFolders: 0, DotFiles: 2, EmptyFolders: 0, ExtensionlessFiles: 0),
			ScenarioKind.NormalRootWithDotDirectory => (DotFolders: 1, DotFiles: 0, EmptyFolders: 2, ExtensionlessFiles: 0),
			ScenarioKind.NormalRootWithNestedExtensionlessFile => (DotFolders: 0, DotFiles: 0, EmptyFolders: 0, ExtensionlessFiles: 1),
			_ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unsupported scenario")
		};
	}

	private static (int DotFolders, int DotFiles, int EmptyFolders, int ExtensionlessFiles) GetExpectedForRootFileScan(
		ScenarioKind scenario)
	{
		return scenario switch
		{
			ScenarioKind.DotRootEmpty => (DotFolders: 0, DotFiles: 0, EmptyFolders: 0, ExtensionlessFiles: 0),
			ScenarioKind.DotRootWithExtensionlessFile => (DotFolders: 0, DotFiles: 0, EmptyFolders: 0, ExtensionlessFiles: 1),
			ScenarioKind.NormalRootWithDotFiles => (DotFolders: 0, DotFiles: 2, EmptyFolders: 0, ExtensionlessFiles: 0),
			ScenarioKind.NormalRootWithDotDirectory => (DotFolders: 0, DotFiles: 0, EmptyFolders: 0, ExtensionlessFiles: 0),
			ScenarioKind.NormalRootWithNestedExtensionlessFile => (DotFolders: 0, DotFiles: 0, EmptyFolders: 0, ExtensionlessFiles: 0),
			_ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unsupported scenario")
		};
	}

	private static string CreateDotRootWithExtensionlessFile(TemporaryDirectory temp)
	{
		var root = temp.CreateDirectory(".dot-root-extensionless");
		temp.CreateFile(".dot-root-extensionless/README", "x");
		return root;
	}

	private static string CreateNormalRootWithDotFiles(TemporaryDirectory temp)
	{
		var root = temp.CreateDirectory("normal-root-dotfiles");
		temp.CreateFile("normal-root-dotfiles/.env", "x");
		temp.CreateFile("normal-root-dotfiles/.gitignore", "x");
		return root;
	}

	private static string CreateNormalRootWithDotDirectory(TemporaryDirectory temp)
	{
		var root = temp.CreateDirectory("normal-root-dotdir");
		temp.CreateDirectory("normal-root-dotdir/.cache");
		return root;
	}

	private static string CreateNormalRootWithNestedExtensionless(TemporaryDirectory temp)
	{
		var root = temp.CreateDirectory("normal-root-nested-extless");
		temp.CreateFile("normal-root-nested-extless/src/Dockerfile", "x");
		return root;
	}

	private static int ExpectedEmptyFolders(
		bool ignoreDotFolders,
		bool ignoreDotFiles,
		bool ignoreExtensionlessFiles)
	{
		// workspace/.cache contributes when dot-folders are included.
		// workspace root contributes when both root files are filtered out.
		var childEmpty = ignoreDotFolders ? 0 : 1;
		var rootEmpty = ignoreDotFiles && ignoreExtensionlessFiles ? 1 : 0;
		return childEmpty + rootEmpty;
	}

	public enum ScenarioKind
	{
		DotRootEmpty = 0,
		DotRootWithExtensionlessFile = 1,
		NormalRootWithDotFiles = 2,
		NormalRootWithDotDirectory = 3,
		NormalRootWithNestedExtensionlessFile = 4
	}
}
