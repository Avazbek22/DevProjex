using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyFileImportsIntegrationTests
{
	[Fact]
	public async Task PythonFileImportsMatchTheFullIndexForEveryFile()
	{
		using var fixture = new TemporaryDirectory();
		fixture.CreateFile("pyproject.toml", "[project]\nname = \"pkg\"\nversion = \"1.0\"\n");
		fixture.CreateFile("pkg/__init__.py", "from ._api import *\nfrom ._client import Client\nfrom . import _config\n");
		fixture.CreateFile("pkg/_api.py", "from ._config import DEFAULT_TIMEOUT\n\n__all__ = [\"request\"]\n\ndef request(timeout=DEFAULT_TIMEOUT):\n    return timeout\n");
		fixture.CreateFile("pkg/_client.py", "import json\nfrom ._config import Limits\n\nclass Client:\n    limits = Limits\n");
		fixture.CreateFile("pkg/_config.py", "class Limits:\n    pass\n\nDEFAULT_TIMEOUT = 5\n");
		fixture.CreateFile("pkg/sub/__init__.py", "");
		fixture.CreateFile("pkg/sub/tools.py", "from .. import Client\nfrom ..sub import helpers\nfrom pkg._api import request\nimport missing_module\n");
		fixture.CreateFile("pkg/sub/helpers.py", "def helper():\n    return 1\n");
		fixture.CreateFile("tests/test_api.py", "import pkg\nfrom pkg import Client, request\n");

		await AssertMatchesFullIndexAsync(fixture.Path);
	}

	[Fact]
	public async Task TypeScriptFileImportsMatchTheFullIndexForEveryFile()
	{
		using var fixture = new TemporaryDirectory();
		fixture.CreateFile("package.json", "{ \"name\": \"fixture\", \"version\": \"1.0.0\" }\n");
		fixture.CreateFile("tsconfig.json", "{ \"compilerOptions\": { \"module\": \"esnext\", \"moduleResolution\": \"bundler\", \"baseUrl\": \".\", \"paths\": { \"@lib/*\": [\"src/lib/*\"] } } }\n");
		fixture.CreateFile("src/index.ts", "export * from './helper/conninfo'\nexport { serve } from './adapter/server'\n");
		fixture.CreateFile("src/helper/conninfo/index.ts", "export const getConnInfo = () => 1\n");
		fixture.CreateFile("src/adapter/server.ts", "import { getConnInfo } from '../helper/conninfo'\nimport type { Env } from '../types'\nimport { readFile } from 'node:fs/promises'\nimport { util } from '@lib/util'\nimport { gone } from './gone'\nexport const serve = () => getConnInfo()\n");
		fixture.CreateFile("src/types.ts", "export type Env = {}\n");
		fixture.CreateFile("src/lib/util.ts", "export const util = 1\n");

		await AssertMatchesFullIndexAsync(fixture.Path);
	}

	[Fact]
	public async Task ResolvedImportsNameTheModuleFiles()
	{
		using var fixture = new TemporaryDirectory();
		fixture.CreateFile("package.json", "{ \"name\": \"fixture\", \"version\": \"1.0.0\" }\n");
		fixture.CreateFile("tsconfig.json", "{ \"compilerOptions\": { \"module\": \"esnext\", \"moduleResolution\": \"bundler\" } }\n");
		fixture.CreateFile("src/helper/conninfo/index.ts", "export const getConnInfo = () => 1\n");
		var server = fixture.CreateFile("src/adapter/bun/server.ts", "import { getConnInfo } from '../../helper/conninfo'\nimport { readFile } from 'node:fs/promises'\nexport const serve = () => getConnInfo()\n");
		using var engine = CreateEngine();

		var imports = await engine.ResolveFileImportsAsync(fixture.Path, Files(fixture.Path), server);

		Assert.NotNull(imports);
		Assert.Equal(
		[
			new DependencyFileImport(1, "../../helper/conninfo", ResolutionStatus.Resolved, ["src/helper/conninfo/index.ts"]),
			new DependencyFileImport(2, "node:fs/promises", ResolutionStatus.External, [])
		], imports, new FileImportComparer());
	}

	[Fact]
	public async Task LanguagesWithoutModuleImportsReturnNull()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("src/App.cs", "namespace App;\npublic sealed class Program { }\n");
		using var engine = CreateEngine();

		Assert.Null(await engine.ResolveFileImportsAsync(fixture.Path, Files(fixture.Path), source));
	}

	[Fact]
	public async Task AFileOutsideTheManifestReturnsNull()
	{
		using var fixture = new TemporaryDirectory();
		var inside = fixture.CreateFile("a.py", "import b\n");
		var outside = fixture.CreateFile("b.py", "x = 1\n");
		using var engine = CreateEngine();

		Assert.Null(await engine.ResolveFileImportsAsync(fixture.Path, [inside], outside));
	}

	[Fact]
	public async Task ACachedIndexAnswersWithTheSameImports()
	{
		using var fixture = new TemporaryDirectory();
		fixture.CreateFile("pkg/__init__.py", "from ._api import request\n");
		fixture.CreateFile("pkg/_api.py", "def request():\n    return 1\n");
		var source = fixture.CreateFile("pkg/use.py", "from pkg import request\nfrom ._api import request as call\n");
		using var engine = CreateEngine();
		var files = Files(fixture.Path);

		var before = await engine.ResolveFileImportsAsync(fixture.Path, files, source);
		await engine.IndexAsync(fixture.Path, files);
		var after = await engine.ResolveFileImportsAsync(fixture.Path, files, source);

		Assert.NotNull(before);
		Assert.Equal(before, after!, new FileImportComparer());
	}

	private static async Task AssertMatchesFullIndexAsync(string root)
	{
		var files = Files(root);
		using var reference = CreateEngine();
		var index = await reference.IndexAsync(root, files);
		foreach (var file in files)
		{
			var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
			using var engine = CreateEngine();
			var imports = await engine.ResolveFileImportsAsync(root, files, file);
			if (imports is null)
				continue;
			var expected = index.EdgesBySource.GetValueOrDefault(relative)?
				.Where(static edge => edge.Layer == EvidenceLayer.ExplicitImport)
				.Select(static edge => (edge.Evidence[0].Line, edge.Reference, edge.Status,
					Targets: edge.Status == ResolutionStatus.Ambiguous ? edge.Candidates : edge.Target is null ? [] : (IReadOnlyList<string>)[edge.Target]))
				.GroupBy(static item => (item.Line, item.Reference))
				.Select(static group => $"{group.Key.Line}|{group.Key.Reference}|{string.Join(",", group.SelectMany(static item => item.Targets).Distinct().Order(StringComparer.Ordinal))}")
				.Order(StringComparer.Ordinal)
				.ToArray() ?? [];
			var actual = imports
				.Select(static item => $"{item.Line}|{item.Reference}|{string.Join(",", item.Targets)}")
				.Order(StringComparer.Ordinal)
				.ToArray();
			Assert.True(expected.SequenceEqual(actual), $"{relative}: expected [{string.Join("; ", expected)}] but got [{string.Join("; ", actual)}]");
		}
	}

	private static string[] Files(string root) =>
		Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

	private static DependencyFactsEngine CreateEngine() => new(
		new TreeSitterDependencyFactExtractor(),
		new FileDependencyConfigurationProvider());

	private sealed class FileImportComparer : IEqualityComparer<DependencyFileImport>
	{
		public bool Equals(DependencyFileImport? x, DependencyFileImport? y) =>
			x is not null && y is not null && x.Line == y.Line && x.Reference == y.Reference &&
			x.Status == y.Status && x.Targets.SequenceEqual(y.Targets);

		public int GetHashCode(DependencyFileImport obj) => HashCode.Combine(obj.Line, obj.Reference, obj.Status);
	}
}
