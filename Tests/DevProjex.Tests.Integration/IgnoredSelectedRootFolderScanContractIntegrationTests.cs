namespace DevProjex.Tests.Integration;

using DevProjex.Tests.Integration.Helpers;
public sealed class IgnoredSelectedRootFolderScanContractIntegrationTests
{
	[Theory]
	[MemberData(nameof(ContractCases))]
	public void GetExtensionsAndIgnoreCountsForRootFolders_WhenSelectedRootLaterBecomesIgnored_SkipsItsSubtree(
		string scenario)
	{
		using var temp = new TemporaryDirectory();
		var (enabledOptions, expectedIgnoredRootName, ignoreRulesService) = CreateScenario(temp, scenario);
		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var initialRules = ignoreRulesService.Build(temp.Path, []);
		var initialRoots = scanOptions.GetRootFolders(temp.Path, initialRules).Value;

		Assert.Contains(expectedIgnoredRootName, initialRoots, StringComparer.Ordinal);

		var enabledRules = ignoreRulesService.Build(temp.Path, enabledOptions, initialRoots);
		var rawScan = scanOptions.GetExtensionsAndIgnoreCountsForRootFolders(
			temp.Path,
			initialRoots,
			enabledRules);
		var effectiveScan = scanOptions.GetEffectiveIgnoreOptionCountsForRootFolders(
			temp.Path,
			initialRoots,
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
			enabledRules,
			rawScan.Value.IgnoreOptionCounts);

		Assert.Equal(1, rawScan.Value.IgnoreOptionCounts.ExtensionlessFiles);
		Assert.Equal(1, effectiveScan.Value.ExtensionlessFiles);
	}

	public static IEnumerable<object[]> ContractCases()
	{
		yield return ["dot-folder-root"];
		yield return ["gitignore-root"];
	}

	private static (IReadOnlyCollection<IgnoreOptionId> EnabledOptions, string IgnoredRootName, IgnoreRulesService RulesService)
		CreateScenario(TemporaryDirectory temp, string scenario)
	{
		switch (scenario)
		{
			case "dot-folder-root":
				temp.CreateFile("README", "visible extensionless");
				temp.CreateFile(Path.Combine("src", "Program.cs"), "class Program { }");
				for (var index = 0; index < 64; index++)
					temp.CreateFile(Path.Combine(".cache", "nested", $"artifact-{index:000}"), $"noise {index}");

				return (
					new[] { IgnoreOptionId.DotFolders, IgnoreOptionId.ExtensionlessFiles },
					".cache",
					new IgnoreRulesService(new SmartIgnoreService([])));

			case "gitignore-root":
				temp.CreateFile(".gitignore", "generated/\n");
				temp.CreateFile("README", "visible extensionless");
				temp.CreateFile(Path.Combine("src", "Program.cs"), "class Program { }");
				for (var index = 0; index < 64; index++)
					temp.CreateFile(Path.Combine("generated", $"artifact-{index:000}"), $"noise {index}");

				return (
					new[] { IgnoreOptionId.UseGitIgnore, IgnoreOptionId.ExtensionlessFiles },
					"generated",
					new IgnoreRulesService(new SmartIgnoreService([])));

			default:
				throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
		}
	}
}
