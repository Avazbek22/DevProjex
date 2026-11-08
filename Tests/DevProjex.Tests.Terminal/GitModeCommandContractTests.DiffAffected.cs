namespace DevProjex.Tests.Terminal;

public sealed partial class GitModeCommandContractTests
{
	private static GitModeRepository CreateAffectedDiffRepository()
	{
		var repository = new GitModeRepository();
		repository.Write("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}\n");
		repository.Write("src/a.ts", "export default 1;\n");
		repository.Write("src/b.ts", "import a from './a.js'; export const b = a;\n");
		repository.Write("src/a.test.ts", "import a from './a.js'; export const checked = a;\n");
		repository.Write("src/unrelated.ts", "export const c = 3;\n");
		repository.Commit("first");
		repository.Write("src/a.ts", "export default 2;\n");
		return repository;
	}

	[Theory]
	[InlineData("en", "Files that import the changed files:", "Tests that import or are named after the changed files:")]
	[InlineData("ru", "Файлы, которые импортируют изменённые файлы:", "Тесты, которые импортируют изменённые файлы или названы по ним:")]
	public void RealDiffCommandListsWhatTheChangeAffectsAfterTheHunks(
		string language,
		string dependentsHeading,
		string testsHeading)
	{
		using var repository = CreateAffectedDiffRepository();
		using var state = new TemporaryDirectory();

		var result = RunScopeCommand(repository, state.Path,
			["diff", "--project", repository.Root, "--language", language, "--plain"]);

		Assert.True(result.ExitCode == 0, result.StandardError);
		Assert.Contains(
			"+export default 2;\n\n" +
			$"{dependentsHeading}\nsrc/b.ts\n\n" +
			$"{testsHeading}\nsrc/a.test.ts\n\n",
			result.StandardOutput,
			StringComparison.Ordinal);
		Assert.DoesNotContain("unrelated", result.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public void RealDiffCommandWritesAffectedFilesToJson()
	{
		using var repository = CreateAffectedDiffRepository();
		using var state = new TemporaryDirectory();

		var result = RunScopeCommand(repository, state.Path,
			["diff", "--project", repository.Root, "--format", "json", "--select", "src/a.ts"]);

		Assert.True(result.ExitCode == 0, result.StandardError);
		using var document = JsonDocument.Parse(result.StandardOutput);
		var affected = document.RootElement.GetProperty("affected");
		Assert.Equal(["src/b.ts"], affected.GetProperty("dependents").EnumerateArray().Select(static item => item.GetString()));
		Assert.Equal(["src/a.test.ts"], affected.GetProperty("tests").EnumerateArray().Select(static item => item.GetString()));
	}

	private static GitModeRepository CreatePackageReExportDiffRepository()
	{
		var repository = new GitModeRepository();
		repository.Write("pyproject.toml", "[project]\nname = \"fixture\"\n");
		repository.Write("pkg/__init__.py", "from ._config import Timeout\nfrom ._api import get\n");
		repository.Write("pkg/_config.py", "class Timeout: pass\n");
		repository.Write("pkg/_api.py", "from ._config import Timeout\ndef get(): return Timeout()\n");
		repository.Write("tests/test_config.py", "def test_timeout(): pass\n");
		repository.Write("tests/test_api.py", "from pkg import Timeout\ndef test_api(): pass\n");
		repository.Write("tests/test_direct.py", "from pkg._config import Timeout\ndef test_direct(): pass\n");
		repository.Commit("first");
		repository.Write("pkg/_config.py", "class Timeout:\n    seconds = 5\n");
		return repository;
	}

	[Fact]
	public void RealDiffCommandListsOnlyDirectImportersAndTestsNamedAfterTheChange()
	{
		using var repository = CreatePackageReExportDiffRepository();
		using var state = new TemporaryDirectory();

		var text = RunScopeCommand(repository, state.Path,
			["diff", "--project", repository.Root, "--language", "en", "--plain"]);
		var json = RunScopeCommand(repository, state.Path,
			["diff", "--project", repository.Root, "--format", "json"]);

		Assert.True(text.ExitCode == 0, text.StandardError);
		Assert.Contains(
			"\n\nFiles that import the changed files:\npkg/_api.py\n\n" +
			"Tests that import or are named after the changed files:\ntests/test_config.py\ntests/test_direct.py\n\n",
			text.StandardOutput,
			StringComparison.Ordinal);
		Assert.DoesNotContain("pkg/__init__.py", text.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("tests/test_api.py", text.StandardOutput, StringComparison.Ordinal);
		Assert.True(json.ExitCode == 0, json.StandardError);
		using var document = JsonDocument.Parse(json.StandardOutput);
		var affected = document.RootElement.GetProperty("affected");
		Assert.Equal(["pkg/_api.py"], affected.GetProperty("dependents").EnumerateArray().Select(static item => item.GetString()));
		Assert.Equal(
			["tests/test_config.py", "tests/test_direct.py"],
			affected.GetProperty("tests").EnumerateArray().Select(static item => item.GetString()));
	}
}
