namespace DevProjex.Tests.Integration;

public sealed class GitIgnoreToggleIntegrationMatrixTests
{
	public static IEnumerable<object[]> ToggleCases()
	{
		var artifactFolders = new[] { "bin", "Bin", "obj", "Obj" };
		foreach (var folder in artifactFolders)
		{
			foreach (var useGitIgnore in new[] { false, true })
			{
				foreach (var ignoreDotFolders in new[] { false, true })
				{
					yield return new object[] { folder, useGitIgnore, ignoreDotFolders };
				}
			}
		}
	}

	[Theory]
	[MemberData(nameof(ToggleCases))]
	public void TreeBuilder_UseGitIgnoreToggle_AffectsArtifactFolderVisibility(
		string artifactFolder,
		bool useGitIgnore,
		bool ignoreDotFolders)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile($"{artifactFolder}/artifact.txt", "artifact");
		temp.CreateFile(".cache/hidden.txt", "hidden");
		temp.CreateFile("visible.txt", "visible");

		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			AllowedRootFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { artifactFolder, ".cache" },
			IgnoreRules: BuildIgnoreRules(temp.Path, useGitIgnore, ignoreDotFolders));

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);
		var rootChildren = result.Root.Children.Select(child => child.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

		var artifactVisible = rootChildren.Contains(artifactFolder);
		var dotFolderVisible = rootChildren.Contains(".cache");

		Assert.Equal(!useGitIgnore, artifactVisible);
		Assert.Equal(!ignoreDotFolders, dotFolderVisible);
	}

	[Theory]
	[MemberData(nameof(ToggleCases))]
	public void FileSystemScanner_UseGitIgnoreToggle_AffectsRootFolderNames(
		string artifactFolder,
		bool useGitIgnore,
		bool ignoreDotFolders)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile($"{artifactFolder}/artifact.txt", "artifact");
		temp.CreateFile(".cache/hidden.txt", "hidden");
		temp.CreateFile("visible.txt", "visible");

		var scanner = new FileSystemScanner();
		var result = scanner.GetRootFolderNames(temp.Path, BuildIgnoreRules(temp.Path, useGitIgnore, ignoreDotFolders));
		var names = result.Value.ToHashSet(StringComparer.OrdinalIgnoreCase);

		Assert.Equal(!useGitIgnore, names.Contains(artifactFolder));
		Assert.Equal(!ignoreDotFolders, names.Contains(".cache"));
		Assert.False(result.RootAccessDenied);
	}

	private static IgnoreRules BuildIgnoreRules(string rootPath, bool useGitIgnore, bool ignoreDotFolders)
	{
		var matcher = GitIgnoreMatcher.Build(rootPath, new[] { "[Bb]in/", "[Oo]bj/" });
		return new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: ignoreDotFolders,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			UseGitIgnore = useGitIgnore,
			GitIgnoreMatcher = matcher
		};
	}
}
