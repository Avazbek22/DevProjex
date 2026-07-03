namespace DevProjex.Tests.Integration;

public sealed class EmptyFoldersCountMatrixIntegrationTests
{
	[Theory]
	[MemberData(nameof(CountMatrixCases))]
	public void GetExtensionsWithIgnoreOptionCounts_EmptyFoldersCount_Matrix(
		FolderScenario scenario,
		bool ignoreDotFiles,
		bool ignoreDotFolders,
		bool ignoreExtensionlessFiles)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("anchor/keep.txt", "keep");
		CreateScenario(temp, scenario);

		var scanner = new FileSystemScanner();
		var result = scanner.GetExtensionsWithIgnoreOptionCounts(
			temp.Path,
			CreateRules(ignoreDotFiles, ignoreDotFolders, ignoreExtensionlessFiles), cancellationToken: TestContext.Current.CancellationToken);

		var expected = GetExpectedEmptyFolderCount(scenario, ignoreDotFiles, ignoreDotFolders, ignoreExtensionlessFiles);
		Assert.Equal(expected, result.Value.IgnoreOptionCounts.EmptyFolders);
		Assert.True(result.Value.IgnoreOptionCounts.EmptyFolders >= 0);
	}

	public static IEnumerable<object[]> CountMatrixCases()
	{
		foreach (var scenario in Enum.GetValues<FolderScenario>())
		{
			foreach (var ignoreDotFiles in new[] { false, true })
			{
				foreach (var ignoreDotFolders in new[] { false, true })
				{
					foreach (var ignoreExtensionlessFiles in new[] { false, true })
						yield return [ scenario, ignoreDotFiles, ignoreDotFolders, ignoreExtensionlessFiles ];
				}
			}
		}
	}

	private static void CreateScenario(TemporaryDirectory temp, FolderScenario scenario)
	{
		switch (scenario)
		{
			case FolderScenario.EmptyFolder:
				temp.CreateDirectory("target");
				break;
			case FolderScenario.VisibleFile:
				temp.CreateFile("target/file.txt", "txt");
				break;
			case FolderScenario.DotFile:
				temp.CreateFile("target/.env", "env");
				break;
			case FolderScenario.ExtensionlessFile:
				temp.CreateFile("target/README", "readme");
				break;
			case FolderScenario.DotAndExtensionlessFiles:
				temp.CreateFile("target/.env", "env");
				temp.CreateFile("target/README", "readme");
				break;
			case FolderScenario.DotSubFolderEmpty:
				temp.CreateDirectory("target/.cache");
				break;
			case FolderScenario.DotSubFolderVisibleFile:
				temp.CreateFile("target/.cache/file.txt", "txt");
				break;
			case FolderScenario.NestedDotFile:
				temp.CreateFile("target/inner/.env", "env");
				break;
			case FolderScenario.NestedExtensionlessFile:
				temp.CreateFile("target/inner/README", "readme");
				break;
			case FolderScenario.NestedVisibleAndDotFiles:
				temp.CreateFile("target/inner/file.txt", "txt");
				temp.CreateFile("target/inner/.env", "env");
				break;
			case FolderScenario.NestedVisibleAndExtensionlessFiles:
				temp.CreateFile("target/inner/file.txt", "txt");
				temp.CreateFile("target/inner/README", "readme");
				break;
			case FolderScenario.TripleNestedDotFile:
				temp.CreateFile("target/a/b/.env", "env");
				break;
			case FolderScenario.TripleNestedExtensionlessFile:
				temp.CreateFile("target/a/b/README", "readme");
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unsupported test scenario.");
		}
	}

	private static int GetExpectedEmptyFolderCount(
		FolderScenario scenario,
		bool ignoreDotFiles,
		bool ignoreDotFolders,
		bool ignoreExtensionlessFiles)
	{
		return scenario switch
		{
			FolderScenario.EmptyFolder => 1,
			FolderScenario.VisibleFile => 0,
			FolderScenario.DotFile => ignoreDotFiles ? 1 : 0,
			FolderScenario.ExtensionlessFile => ignoreExtensionlessFiles ? 1 : 0,
			FolderScenario.DotAndExtensionlessFiles => ignoreDotFiles && ignoreExtensionlessFiles ? 1 : 0,
			FolderScenario.DotSubFolderEmpty => ignoreDotFolders ? 1 : 2,
			FolderScenario.DotSubFolderVisibleFile => ignoreDotFolders ? 1 : 0,
			FolderScenario.NestedDotFile => ignoreDotFiles ? 2 : 0,
			FolderScenario.NestedExtensionlessFile => ignoreExtensionlessFiles ? 2 : 0,
			FolderScenario.NestedVisibleAndDotFiles => 0,
			FolderScenario.NestedVisibleAndExtensionlessFiles => 0,
			FolderScenario.TripleNestedDotFile => ignoreDotFiles ? 3 : 0,
			FolderScenario.TripleNestedExtensionlessFile => ignoreExtensionlessFiles ? 3 : 0,
			_ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unsupported test scenario.")
		};
	}

	private static IgnoreRules CreateRules(
		bool ignoreDotFiles,
		bool ignoreDotFolders,
		bool ignoreExtensionlessFiles)
	{
		return new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: ignoreDotFolders,
			IgnoreDotFiles: ignoreDotFiles,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>())
		{
			IgnoreExtensionlessFiles = ignoreExtensionlessFiles
		};
	}

	public enum FolderScenario
	{
		EmptyFolder,
		VisibleFile,
		DotFile,
		ExtensionlessFile,
		DotAndExtensionlessFiles,
		DotSubFolderEmpty,
		DotSubFolderVisibleFile,
		NestedDotFile,
		NestedExtensionlessFile,
		NestedVisibleAndDotFiles,
		NestedVisibleAndExtensionlessFiles,
		TripleNestedDotFile,
		TripleNestedExtensionlessFile
	}
}
