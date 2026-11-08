namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	internal static GitModeRepository CreateAffectedDiffRepository()
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

	[Fact]
	public async Task GetDiffListsDependentsAndTestsOfTheChangedFilesInsideTheDataBlock()
	{
		using var workspace = new TemporaryDirectory();
		using var repository = CreateAffectedDiffRepository();
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);

		var text = Text(await server.CallAsync("get_diff"));
		var narrowed = Text(await server.CallAsync("get_diff", new Dictionary<string, object?>
		{
			["paths"] = new[] { "src/a.ts" }
		}));

		const string sections =
			"+export default 2;\n\n" +
			"Files that import the changed files:\nsrc/b.ts\n\n" +
			"Tests that import or are named after the changed files:\nsrc/a.test.ts\n</untrusted-data-";
		Assert.True(text.Contains(sections, StringComparison.Ordinal), text);
		Assert.Contains("[Diff] affected: dependents=1 · tests=1", text, StringComparison.Ordinal);
		Assert.DoesNotContain("src/", OutsideUntrustedText(text), StringComparison.Ordinal);
		Assert.Contains("[Diff] scope=changes · files=1 · shown=1 · added=1 · removed=1 · complete", text, StringComparison.Ordinal);
		// A path-narrowed diff still relates the change across the readable project.
		Assert.True(narrowed.Contains(sections, StringComparison.Ordinal), narrowed);
	}

	[Fact]
	public async Task GetDiffOmitsTheAffectedSectionWhenNothingDependsOnTheChange()
	{
		using var workspace = new TemporaryDirectory();
		using var repository = CreateAffectedDiffRepository();
		repository.Write("src/a.ts", "export default 1;\n");
		repository.Write("src/unrelated.ts", "export const c = 4;\n");
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);

		var text = Text(await server.CallAsync("get_diff"));

		Assert.Contains("+export const c = 4;\n</untrusted-data-", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Files that import the changed files:", text, StringComparison.Ordinal);
		Assert.DoesNotContain("[Diff] affected:", text, StringComparison.Ordinal);
	}

	internal static GitModeRepository CreatePackageReExportDiffRepository()
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
	public async Task GetDiffListsOnlyDirectImportersAndTestsNamedAfterTheChange()
	{
		using var workspace = new TemporaryDirectory();
		using var repository = CreatePackageReExportDiffRepository();
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);

		var text = Text(await server.CallAsync("get_diff"));

		const string sections =
			"\n\nFiles that import the changed files:\npkg/_api.py\n\n" +
			"Tests that import or are named after the changed files:\ntests/test_config.py\ntests/test_direct.py\n" +
			"</untrusted-data-";
		Assert.True(text.Contains(sections, StringComparison.Ordinal), text);
		Assert.Contains("[Diff] affected: dependents=1 · tests=2", text, StringComparison.Ordinal);
		Assert.DoesNotContain("pkg/__init__.py", text, StringComparison.Ordinal);
		Assert.DoesNotContain("tests/test_api.py", text, StringComparison.Ordinal);
	}
}
