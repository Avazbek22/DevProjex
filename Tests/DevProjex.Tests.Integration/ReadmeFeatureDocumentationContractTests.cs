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
		Assert.Contains("Every reachable `.gitignore`", readme, StringComparison.Ordinal);
		Assert.Contains("Smart Ignore processes the items that remain.", readme, StringComparison.Ordinal);
		Assert.Contains("project-specific patterns in `.gitignore`", readme, StringComparison.Ordinal);
		Assert.Contains("intentionally not an arbitrary user-editable pattern list", readme, StringComparison.Ordinal);
		Assert.Contains("dot-prefixed and hidden items through their separate switches", readme, StringComparison.Ordinal);
		Assert.Contains("[stack rules](Infrastructure/SmartIgnore)", readme, StringComparison.Ordinal);
		Assert.Contains(
			"[evidence-based signature matcher](Kernel/Models/SmartArtifactIgnoreMatcher.cs)",
			readme,
			StringComparison.Ordinal);
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
