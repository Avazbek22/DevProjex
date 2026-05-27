namespace DevProjex.Tests.Integration;

public sealed class EmptyFoldersTreeFilterMatrixIntegrationTests
{
	[Theory]
	[MemberData(nameof(TreeMatrixCases))]
	public void Build_IgnoreEmptyFoldersFiltering_Matrix(
		FolderScenario scenario,
		bool ignoreDotFiles,
		bool ignoreDotFolders,
		bool ignoreExtensionlessFiles,
		bool ignoreEmptyFolders)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("anchor/keep.txt", "keep");
		CreateScenario(temp, scenario);

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				".txt",
				".env"
			},
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				"anchor",
				"target"
			},
			IgnoreRules: CreateRules(ignoreDotFiles, ignoreDotFolders, ignoreExtensionlessFiles, ignoreEmptyFolders),
			NameFilter: null), cancellationToken: TestContext.Current.CancellationToken);

		var targetNode = result.Root.Children.SingleOrDefault(x => x.Name == "target");
		var shouldContainTarget = ShouldContainTarget(
			scenario,
			ignoreDotFiles,
			ignoreDotFolders,
			ignoreExtensionlessFiles,
			ignoreEmptyFolders);

		Assert.Equal(shouldContainTarget, targetNode is not null);

		if (targetNode is not null && scenario == FolderScenario.DotSubFolderVisibleFile)
		{
			var hasDotSubFolder = targetNode.Children.Any(x => x.Name == ".cache");
			Assert.Equal(!ignoreDotFolders, hasDotSubFolder);
		}
	}

	public static IEnumerable<object[]> TreeMatrixCases()
	{
		foreach (var scenario in Enum.GetValues<FolderScenario>())
		{
			foreach (var ignoreDotFiles in new[] { false, true })
			{
				foreach (var ignoreDotFolders in new[] { false, true })
				{
					foreach (var ignoreExtensionlessFiles in new[] { false, true })
					{
						foreach (var ignoreEmptyFolders in new[] { false, true })
							yield return [ scenario, ignoreDotFiles, ignoreDotFolders, ignoreExtensionlessFiles, ignoreEmptyFolders ];
					}
				}
			}
		}
	}

	private static bool ShouldContainTarget(
		FolderScenario scenario,
		bool ignoreDotFiles,
		bool ignoreDotFolders,
		bool ignoreExtensionlessFiles,
		bool ignoreEmptyFolders)
	{
		if (!ignoreEmptyFolders)
			return true;

		return scenario switch
		{
			FolderScenario.EmptyFolder => false,
			FolderScenario.DotFile => !ignoreDotFiles,
			FolderScenario.ExtensionlessFile => !ignoreExtensionlessFiles,
			FolderScenario.DotSubFolderEmpty => false,
			FolderScenario.DotSubFolderVisibleFile => !ignoreDotFolders,
			_ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unsupported test scenario.")
		};
	}

	private static void CreateScenario(TemporaryDirectory temp, FolderScenario scenario)
	{
		switch (scenario)
		{
			case FolderScenario.EmptyFolder:
				temp.CreateDirectory("target");
				break;
			case FolderScenario.DotFile:
				temp.CreateFile("target/.env", "env");
				break;
			case FolderScenario.ExtensionlessFile:
				temp.CreateFile("target/README", "readme");
				break;
			case FolderScenario.DotSubFolderEmpty:
				temp.CreateDirectory("target/.cache");
				break;
			case FolderScenario.DotSubFolderVisibleFile:
				temp.CreateFile("target/.cache/file.txt", "txt");
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unsupported test scenario.");
		}
	}

	private static IgnoreRules CreateRules(
		bool ignoreDotFiles,
		bool ignoreDotFolders,
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

	public enum FolderScenario
	{
		EmptyFolder,
		DotFile,
		ExtensionlessFile,
		DotSubFolderEmpty,
		DotSubFolderVisibleFile
	}
}
