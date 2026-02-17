using DevProjex.Infrastructure.SmartIgnore;
using DevProjex.Tests.Unit.Helpers;

namespace DevProjex.Tests.Unit;

public sealed class PolyglotSmartIgnoreRulesTests
{
	[Theory]
	[InlineData("package.json")]
	[InlineData("package-lock.json")]
	[InlineData("pnpm-lock.yaml")]
	[InlineData("yarn.lock")]
	[InlineData("bun.lockb")]
	[InlineData("bun.lock")]
	[InlineData("pnpm-workspace.yaml")]
	[InlineData("npm-shrinkwrap.json")]
	public void FrontendArtifactsIgnoreRule_ActivatesForAnyFrontendMarker(string marker)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(marker, "{}");
		var rule = new FrontendArtifactsIgnoreRule();

		var result = rule.Evaluate(temp.Path);

		Assert.Contains("node_modules", result.FolderNames, StringComparer.OrdinalIgnoreCase);
		Assert.Contains("coverage", result.FolderNames, StringComparer.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData("node_modules")]
	[InlineData("dist")]
	[InlineData("build")]
	[InlineData(".next")]
	[InlineData(".nuxt")]
	[InlineData(".turbo")]
	[InlineData(".svelte-kit")]
	[InlineData(".angular")]
	[InlineData("coverage")]
	[InlineData(".cache")]
	[InlineData(".parcel-cache")]
	[InlineData(".vite")]
	[InlineData(".output")]
	[InlineData(".astro")]
	[InlineData("storybook-static")]
	[InlineData("out")]
	public void FrontendArtifactsIgnoreRule_IncludesExtendedFolderSet(string folderName)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("package.json", "{}");
		var rule = new FrontendArtifactsIgnoreRule();

		var result = rule.Evaluate(temp.Path);

		Assert.Contains(folderName, result.FolderNames, StringComparer.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData("pyproject.toml")]
	[InlineData("requirements.txt")]
	[InlineData("requirements-dev.txt")]
	[InlineData("setup.py")]
	[InlineData("setup.cfg")]
	[InlineData("Pipfile")]
	[InlineData("poetry.lock")]
	[InlineData("environment.yml")]
	public void PythonArtifactsIgnoreRule_ActivatesForAnyPythonMarker(string marker)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(marker, "x");
		var rule = new PythonArtifactsIgnoreRule();

		var result = rule.Evaluate(temp.Path);

		Assert.Contains("__pycache__", result.FolderNames, StringComparer.OrdinalIgnoreCase);
		Assert.Contains(".venv", result.FolderNames, StringComparer.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData("__pycache__")]
	[InlineData(".pytest_cache")]
	[InlineData(".mypy_cache")]
	[InlineData(".ruff_cache")]
	[InlineData(".tox")]
	[InlineData(".nox")]
	[InlineData(".venv")]
	[InlineData("venv")]
	[InlineData("env")]
	[InlineData(".hypothesis")]
	[InlineData(".ipynb_checkpoints")]
	[InlineData(".pyre")]
	public void PythonArtifactsIgnoreRule_IncludesKnownPythonArtifactFolders(string folderName)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("pyproject.toml", "[project]");
		var rule = new PythonArtifactsIgnoreRule();

		var result = rule.Evaluate(temp.Path);

		Assert.Contains(folderName, result.FolderNames, StringComparer.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData("pom.xml")]
	[InlineData("build.gradle")]
	[InlineData("build.gradle.kts")]
	[InlineData("settings.gradle")]
	[InlineData("settings.gradle.kts")]
	public void JvmArtifactsIgnoreRule_ActivatesForMavenOrGradleMarkers(string marker)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(marker, "x");
		var rule = new JvmArtifactsIgnoreRule();

		var result = rule.Evaluate(temp.Path);

		Assert.Contains("target", result.FolderNames, StringComparer.OrdinalIgnoreCase);
		Assert.Contains("build", result.FolderNames, StringComparer.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData("target")]
	[InlineData(".gradle")]
	[InlineData("build")]
	[InlineData("out")]
	public void JvmArtifactsIgnoreRule_IncludesKnownJvmArtifactFolders(string folderName)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("pom.xml", "<project/>");
		var rule = new JvmArtifactsIgnoreRule();

		var result = rule.Evaluate(temp.Path);

		Assert.Contains(folderName, result.FolderNames, StringComparer.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData("go.mod")]
	[InlineData("go.work")]
	public void GoArtifactsIgnoreRule_ActivatesForGoMarkers(string marker)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(marker, "module sample");
		var rule = new GoArtifactsIgnoreRule();

		var result = rule.Evaluate(temp.Path);

		Assert.Contains("vendor", result.FolderNames, StringComparer.OrdinalIgnoreCase);
		Assert.Contains("bin", result.FolderNames, StringComparer.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData("Gemfile")]
	[InlineData("Gemfile.lock")]
	public void RubyArtifactsIgnoreRule_ActivatesForRubyMarkers(string marker)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(marker, "x");
		var rule = new RubyArtifactsIgnoreRule();

		var result = rule.Evaluate(temp.Path);

		Assert.Contains(".bundle", result.FolderNames, StringComparer.OrdinalIgnoreCase);
		Assert.Contains("vendor", result.FolderNames, StringComparer.OrdinalIgnoreCase);
		Assert.Contains("log", result.FolderNames, StringComparer.OrdinalIgnoreCase);
		Assert.Contains("tmp", result.FolderNames, StringComparer.OrdinalIgnoreCase);
	}

	[Fact]
	public void PhpArtifactsIgnoreRule_ActivatesForComposerProjects()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("composer.json", "{}");
		var rule = new PhpArtifactsIgnoreRule();

		var result = rule.Evaluate(temp.Path);

		Assert.Contains("vendor", result.FolderNames, StringComparer.OrdinalIgnoreCase);
	}

	[Fact]
	public void RustArtifactsIgnoreRule_ActivatesForCargoProjects()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("Cargo.toml", "[package]");
		var rule = new RustArtifactsIgnoreRule();

		var result = rule.Evaluate(temp.Path);

		Assert.Contains("target", result.FolderNames, StringComparer.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData("frontend")]
	[InlineData("python")]
	[InlineData("jvm")]
	[InlineData("go")]
	[InlineData("php")]
	[InlineData("ruby")]
	[InlineData("rust")]
	public void Rules_WithoutMatchingMarkers_ReturnEmptySets(string stack)
	{
		using var temp = new TemporaryDirectory();
		var result = stack switch
		{
			"frontend" => new FrontendArtifactsIgnoreRule().Evaluate(temp.Path),
			"python" => new PythonArtifactsIgnoreRule().Evaluate(temp.Path),
			"jvm" => new JvmArtifactsIgnoreRule().Evaluate(temp.Path),
			"go" => new GoArtifactsIgnoreRule().Evaluate(temp.Path),
			"php" => new PhpArtifactsIgnoreRule().Evaluate(temp.Path),
			"ruby" => new RubyArtifactsIgnoreRule().Evaluate(temp.Path),
			"rust" => new RustArtifactsIgnoreRule().Evaluate(temp.Path),
			_ => throw new ArgumentOutOfRangeException(nameof(stack), stack, null)
		};

		Assert.Empty(result.FolderNames);
		Assert.Empty(result.FileNames);
	}
}
