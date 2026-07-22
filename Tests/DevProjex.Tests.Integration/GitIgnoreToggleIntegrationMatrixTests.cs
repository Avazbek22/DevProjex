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
					yield return [ folder, useGitIgnore, ignoreDotFolders ];
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
		var result = builder.Build(temp.Path, options, cancellationToken: TestContext.Current.CancellationToken);
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
		var result = scanner.GetRootFolderNames(temp.Path, BuildIgnoreRules(temp.Path, useGitIgnore, ignoreDotFolders), cancellationToken: TestContext.Current.CancellationToken);
		var names = result.Value.ToHashSet(StringComparer.OrdinalIgnoreCase);

		Assert.Equal(!useGitIgnore, names.Contains(artifactFolder));
		Assert.Equal(!ignoreDotFolders, names.Contains(".cache"));
		Assert.False(result.RootAccessDenied);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void GitAdministrativeBoundary_TreeAndScannerStayAlignedWithoutHidingLookalikes(bool useGitIgnore)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "logs/\n");
		temp.CreateFile(".git/objects/pack/object.data", "GIT-OBJECT-SENTINEL\n");
		temp.CreateFile(".github/workflows/ci.yml", "name: CI\n");
		temp.CreateFile(".git-owned/source.txt", "source\n");
		temp.CreateFile("src/app.txt", "APP-SENTINEL\n");
		var rules = BuildIgnoreRules(temp.Path, useGitIgnore, ignoreDotFolders: false);
		var allowedRoots = new HashSet<string>(PathComparer.Default)
		{
			".git", ".github", ".git-owned", "src"
		};
		var options = new TreeFilterOptions(
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".data", ".txt", ".yml" },
			allowedRoots,
			rules);

		var tree = new TreeBuilder().Build(temp.Path, options, TestContext.Current.CancellationToken);
		var treeRoots = tree.Root.Children.Select(static child => child.Name).ToHashSet(PathComparer.Default);
		var scannedRoots = new FileSystemScanner()
			.GetRootFolderNames(temp.Path, rules, TestContext.Current.CancellationToken)
			.Value
			.ToHashSet(PathComparer.Default);

		Assert.Equal(!useGitIgnore, treeRoots.Contains(".git"));
		Assert.Equal(!useGitIgnore, scannedRoots.Contains(".git"));
		Assert.Contains(".github", treeRoots);
		Assert.Contains(".github", scannedRoots);
		Assert.Contains(".git-owned", treeRoots);
		Assert.Contains(".git-owned", scannedRoots);
		Assert.Contains("src", treeRoots);
		Assert.Contains("src", scannedRoots);
	}

	private static IgnoreRules BuildIgnoreRules(string rootPath, bool useGitIgnore, bool ignoreDotFolders)
	{
		var matcher = GitIgnoreMatcher.Build(rootPath, ["[Bb]in/", "[Oo]bj/"]);
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
