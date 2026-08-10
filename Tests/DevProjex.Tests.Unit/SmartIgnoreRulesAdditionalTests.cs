namespace DevProjex.Tests.Unit;

public sealed class SmartIgnoreRulesAdditionalTests
{
	// CommonSmartIgnoreRule no longer includes folders - all folders (.git, .vs, .idea, etc.)
	// are now controlled via DotFolders filter for predictable user control.
	// This test was removed as folders are no longer part of CommonSmartIgnoreRule.

	[Fact]
	public void CommonSmartIgnoreRule_DoesNotIncludeFolders()
	{
		var rule = new CommonSmartIgnoreRule();

		var result = rule.Evaluate("any");

		// CommonSmartIgnoreRule now returns empty folder set
		Assert.Empty(result.FolderNames);
	}

	[Theory]
	// Verifies common smart ignore rule includes expected file names.
	[InlineData(".ds_store")]
	[InlineData("thumbs.db")]
	[InlineData("desktop.ini")]
	public void CommonSmartIgnoreRule_IncludesDefaultFiles(string fileName)
	{
		var rule = new CommonSmartIgnoreRule();

		var result = rule.Evaluate("any");

		Assert.Contains(fileName, result.FileNames, StringComparer.OrdinalIgnoreCase);
	}

	[Theory]
	// Verifies frontend artifacts rule returns empty sets without marker files.
	[InlineData("readme.md")]
	[InlineData("package.txt")]
	[InlineData("yarn.json")]
	[InlineData("lockfile")]
	public void FrontendArtifactsIgnoreRule_NoMarkers_ReturnsEmpty(string fileName)
	{
		using var temp = new TemporaryDirectory();
		var rule = new FrontendArtifactsIgnoreRule();
		temp.CreateFile(fileName, "content");

		var result = rule.Evaluate(temp.Path);

		Assert.Empty(result.FolderNames);
		Assert.Empty(result.FileNames);
	}

	[Theory]
	// Verifies frontend artifacts rule activates when marker files are present.
	[InlineData("package.json")]
	[InlineData("package-lock.json")]
	[InlineData("pnpm-lock.yaml")]
	[InlineData("yarn.lock")]
	public void FrontendArtifactsIgnoreRule_WithMarker_IncludesFolders(string markerFile)
	{
		using var temp = new TemporaryDirectory();
		var rule = new FrontendArtifactsIgnoreRule();
		temp.CreateFile(markerFile, "content");

		var result = rule.Evaluate(temp.Path);

		Assert.Contains("node_modules", result.FolderNames, StringComparer.OrdinalIgnoreCase);
		Assert.Contains("dist", result.FolderNames, StringComparer.OrdinalIgnoreCase);
		Assert.Contains("build", result.FolderNames, StringComparer.OrdinalIgnoreCase);
		Assert.Contains(".next", result.FolderNames, StringComparer.OrdinalIgnoreCase);
		Assert.Contains(".nuxt", result.FolderNames, StringComparer.OrdinalIgnoreCase);
		Assert.Contains(".turbo", result.FolderNames, StringComparer.OrdinalIgnoreCase);
		Assert.Contains(".svelte-kit", result.FolderNames, StringComparer.OrdinalIgnoreCase);
	}

	[Fact]
	public void BuiltInFactsAwareRules_MatchLegacyPathEvaluationForMarkerProjects()
	{
		var cases = new[]
		{
			new RuleCase(new FrontendArtifactsIgnoreRule(), "package.json"),
			new RuleCase(new DotNetArtifactsIgnoreRule(), "App.csproj"),
			new RuleCase(new PythonArtifactsIgnoreRule(), "requirements.txt"),
			new RuleCase(new JvmArtifactsIgnoreRule(), "settings.gradle"),
			new RuleCase(new GoArtifactsIgnoreRule(), "go.mod"),
			new RuleCase(new PhpArtifactsIgnoreRule(), "composer.json"),
			new RuleCase(new RubyArtifactsIgnoreRule(), "Gemfile"),
			new RuleCase(new RustArtifactsIgnoreRule(), "Cargo.toml"),
			new RuleCase(new SwiftArtifactsIgnoreRule(), "Package.swift"),
			new RuleCase(new DartArtifactsIgnoreRule(), "pubspec.yaml")
		};

		foreach (var testCase in cases)
		{
			using var temp = new TemporaryDirectory();
			temp.CreateFile(testCase.MarkerFile, string.Empty);
			var facts = new ProjectRootFactsProvider(cacheLimit: 0).Get(temp.Path);

			var legacy = testCase.Rule.Evaluate(temp.Path);
			var factsAware = Assert.IsAssignableFrom<IProjectRootFactsSmartIgnoreRule>(testCase.Rule)
				.Evaluate(facts);

			AssertEquivalentResult(legacy, factsAware);
		}
	}

	[Fact]
	public void BuiltInFactsAwareRules_MatchLegacyPathEvaluationWithoutMarkers()
	{
		var rules = new ISmartIgnoreRule[]
		{
			new FrontendArtifactsIgnoreRule(),
			new DotNetArtifactsIgnoreRule(),
			new PythonArtifactsIgnoreRule(),
			new JvmArtifactsIgnoreRule(),
			new GoArtifactsIgnoreRule(),
			new PhpArtifactsIgnoreRule(),
			new RubyArtifactsIgnoreRule(),
			new RustArtifactsIgnoreRule(),
			new SwiftArtifactsIgnoreRule(),
			new DartArtifactsIgnoreRule()
		};

		foreach (var rule in rules)
		{
			using var temp = new TemporaryDirectory();
			temp.CreateFile("README.md", "docs");
			var facts = new ProjectRootFactsProvider(cacheLimit: 0).Get(temp.Path);

			var legacy = rule.Evaluate(temp.Path);
			var factsAware = Assert.IsAssignableFrom<IProjectRootFactsSmartIgnoreRule>(rule)
				.Evaluate(facts);

			AssertEquivalentResult(legacy, factsAware);
		}
	}

	private static void AssertEquivalentResult(
		SmartIgnoreResult expected,
		SmartIgnoreResult actual)
	{
		Assert.Equal(
			expected.FolderNames.Order(StringComparer.OrdinalIgnoreCase),
			actual.FolderNames.Order(StringComparer.OrdinalIgnoreCase));
		Assert.Equal(
			expected.FileNames.Order(StringComparer.OrdinalIgnoreCase),
			actual.FileNames.Order(StringComparer.OrdinalIgnoreCase));
	}

	private sealed record RuleCase(ISmartIgnoreRule Rule, string MarkerFile);
}
