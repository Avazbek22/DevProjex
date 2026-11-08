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
	[InlineData("en", "Files that depend on the changed files:", "Tests that cover the changed files:")]
	[InlineData("ru", "Файлы, которые зависят от изменённых:", "Тесты, которые покрывают изменённые файлы:")]
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
}
