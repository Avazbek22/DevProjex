namespace DevProjex.Tests.Integration;

public sealed class ReadmeFeatureDocumentationContractTests
{
	[Fact]
	public void Readme_ExplainsSmartIgnoreScopeEvidenceMonorepoAndUserControl()
	{
		var readme = ReadReadme();

		Assert.Contains("deterministic, local, scope-aware filtering algorithm", readme, StringComparison.Ordinal);
		Assert.Contains("Scope + Evidence", readme, StringComparison.Ordinal);
		Assert.Contains("Require evidence for ambiguous folders", readme, StringComparison.Ordinal);
		Assert.Contains("If the evidence is missing, the folder stays visible.", readme, StringComparison.Ordinal);
		Assert.Contains("Keep mixed monorepos isolated", readme, StringComparison.Ordinal);
		Assert.Contains("applicable ancestor project markers", readme, StringComparison.Ordinal);
		Assert.Contains("every reachable `.gitignore`", readme, StringComparison.Ordinal);
		Assert.Contains("Smart Ignore processes the remaining items", readme, StringComparison.Ordinal);
		Assert.Contains("project-specific patterns in `.gitignore`", readme, StringComparison.Ordinal);
		Assert.Contains("intentionally not an arbitrary user-editable pattern list", readme, StringComparison.Ordinal);
		Assert.Contains("dot-prefixed and hidden items through their separate switches", readme, StringComparison.Ordinal);
		Assert.Contains("[stack rules](Infrastructure/SmartIgnore)", readme, StringComparison.Ordinal);
		Assert.Contains(
			"[evidence-based signature matcher](Kernel/Models/SmartArtifactIgnoreMatcher.cs)",
			readme,
			StringComparison.Ordinal);
	}

	[Fact]
	public void Readme_ExplainsIndexBackedGitModeNestedRepositoriesAndFilterComposition()
	{
		var readme = ReadReadme();

		Assert.Contains("Two Git-aware filtering modes", readme, StringComparison.Ordinal);
		Assert.Contains("**Use `.gitignore`**", readme, StringComparison.Ordinal);
		Assert.Contains("**Tracked Git files only**", readme, StringComparison.Ordinal);
		Assert.Contains("two mutually exclusive modes", readme, StringComparison.Ordinal);
		Assert.Contains(
			"existing working-tree files recorded in each readable Git index",
			readme,
			StringComparison.Ordinal);
		Assert.Contains("Modified tracked files and staged additions remain included", readme, StringComparison.Ordinal);
		Assert.Contains("untracked files are excluded", readme, StringComparison.Ordinal);
		Assert.Contains("every reachable nested repository and worktree independently", readme, StringComparison.Ordinal);
		Assert.Contains("at any reachable nesting level", readme, StringComparison.Ordinal);
		Assert.Contains("never inherits tracked state from its parent or sibling", readme, StringComparison.Ordinal);
		Assert.Contains("does not silently fall back to `.gitignore` patterns", readme, StringComparison.Ordinal);
		Assert.Contains("not a historical snapshot of `HEAD`", readme, StringComparison.Ordinal);
		Assert.Contains("stable toggle pair", readme, StringComparison.Ordinal);
		Assert.Contains("The selected Git mode runs first", readme, StringComparison.Ordinal);
		Assert.Contains("Root-folder, file-type, and checkbox selections", readme, StringComparison.Ordinal);
	}

	private static string ReadReadme()
	{
		var repositoryRoot = FindRepositoryRoot();
		return File.ReadAllText(Path.Combine(repositoryRoot, "README.md"));
	}

	private static string FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "README.md")) &&
			    Directory.Exists(Path.Combine(directory.FullName, "Assets")) &&
			    Directory.Exists(Path.Combine(directory.FullName, "Tests")))
			{
				return directory.FullName;
			}

			directory = directory.Parent;
		}

		throw new DirectoryNotFoundException("Could not locate repository root for README documentation tests.");
	}
}
