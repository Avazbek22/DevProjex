namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	private const string ChangeFocusNotice =
		"[Change focus] dependents of changed seeds are grouped by use of the declarations the Git changes touch; " +
		"use lines come from protected text.";

	[Fact]
	public async Task RelatedFilesGroupsPythonDependentsByUseOfTheChangedDeclarations()
	{
		using var workspace = new TemporaryDirectory();
		using var repository = WritePythonFocusFixture();
		repository.Write("pkg/_config.py", "__all__ = [\"Limits\"]\n\nclass Limits:\n    pass\n\nDEFAULT_TIMEOUT = 10\n");
		repository.Write("pkg/_api.py", "from ._config import DEFAULT_TIMEOUT\n\n__all__ = [\"request\"]\n\ndef request(timeout=DEFAULT_TIMEOUT):\n    return timeout * 2\n");
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);

		var text = RelatedText(await server.CallAsync("related_files", new Dictionary<string, object?>
		{
			["path"] = new[] { "pkg/_config.py", "pkg/_api.py" },
			["direction"] = "dependents"
		}));

		Assert.Contains(
			"Seed: pkg/_config.py\n" +
			"Changed declarations: DEFAULT_TIMEOUT at line 6\n" +
			"Dependents using changed declarations:\n" +
			"pkg/_api.py — DEFAULT_TIMEOUT at line 5 · in request — resolved — ",
			text,
			StringComparison.Ordinal);
		Assert.Contains(
			"Tests using changed declarations: none reference them directly\n" +
			"Other dependents, using no changed declaration:\n" +
			"pkg/__init__.py — import at line 2 — resolved — ",
			text,
			StringComparison.Ordinal);
		Assert.Contains("\npkg/_transport.py — import ._config at line 1 — resolved — ", text, StringComparison.Ordinal);
		Assert.Contains(
			"Seed: pkg/_api.py\n" +
			"Changed declarations: request at line 6\n" +
			"Dependents using changed declarations: none reference them directly\n" +
			"Tests using changed declarations:\n" +
			"tests/test_api.py — via pkg/__init__.py · request at line 4 · in test_request — resolved — ",
			text,
			StringComparison.Ordinal);
		Assert.Contains("\nRe-exported by: pkg/__init__.py\n", text, StringComparison.Ordinal);
		Assert.Contains(ChangeFocusNotice, text, StringComparison.Ordinal);
		var trusted = OutsideUntrustedText(text);
		Assert.DoesNotContain("DEFAULT_TIMEOUT", trusted, StringComparison.Ordinal);
		Assert.DoesNotContain("pkg/", trusted, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RelatedFilesKeepsThePlainDependentListWithoutChanges()
	{
		using var workspace = new TemporaryDirectory();
		using var repository = WritePythonFocusFixture();
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);

		var text = RelatedText(await server.CallAsync("related_files", new Dictionary<string, object?>
		{
			["path"] = "pkg/_config.py",
			["direction"] = "dependents"
		}));

		Assert.Contains("Dependents:\npkg/__init__.py — ", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Changed declarations", text, StringComparison.Ordinal);
		Assert.DoesNotContain(ChangeFocusNotice, text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RelatedFilesLeavesDependenciesOfAChangedFileUngrouped()
	{
		using var workspace = new TemporaryDirectory();
		using var repository = WritePythonFocusFixture();
		repository.Write("pkg/_api.py", "from ._config import DEFAULT_TIMEOUT\n\n__all__ = [\"request\"]\n\ndef request(timeout=DEFAULT_TIMEOUT):\n    return timeout * 2\n");
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);

		var text = RelatedText(await server.CallAsync("related_files", new Dictionary<string, object?>
		{
			["path"] = "pkg/_api.py",
			["direction"] = "dependencies"
		}));

		Assert.Contains("Dependencies:\npkg/_config.py — ", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Changed declarations", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RelatedFilesPutsCallersOfAChangedMemberFirst()
	{
		using var workspace = new TemporaryDirectory();
		using var repository = new GitModeRepository();
		// Relative TypeScript imports resolve under a project configuration.
		repository.Write("package.json", "{ \"name\": \"fixture\", \"version\": \"1.0.0\" }\n");
		repository.Write("tsconfig.json", "{ \"compilerOptions\": { \"module\": \"esnext\", \"moduleResolution\": \"bundler\" } }\n");
		repository.Write("src/errors.ts", "export class Failure extends Error {\n  getResponse() {\n    return 'old'\n  }\n}\nexport class Other {}\n");
		repository.Write("src/thrower.ts", "import { Failure } from './errors'\nexport function fail() {\n  throw new Failure('x')\n}\n");
		repository.Write("src/handler.ts", "import { Failure } from './errors'\nexport function handle(err: unknown) {\n  if (err instanceof Failure) {\n    return err.getResponse()\n  }\n  return null\n}\n");
		repository.Write("src/unrelated.ts", "import { Other } from './errors'\nexport const value = new Other()\n");
		repository.Write("src/errors.test.ts", "import {\n  Failure,\n} from './errors'\ntest('response', () => new Failure('x').getResponse())\n");
		repository.Commit();
		repository.Write("src/errors.ts", "export class Failure extends Error {\n  getResponse() {\n    return 'new'\n  }\n}\nexport class Other {}\n");
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);

		var text = RelatedText(await server.CallAsync("related_files", new Dictionary<string, object?>
		{
			["path"] = "src/errors.ts",
			["direction"] = "dependents"
		}));

		Assert.Contains(
			"Changed declarations: Failure at line 3 (Failure.getResponse)\n" +
			"Dependents using changed declarations:\n" +
			"src/handler.ts — .getResponse at line 4 · Failure at line 3 · in handle — resolved — ",
			text,
			StringComparison.Ordinal);
		Assert.Contains("\nsrc/thrower.ts — Failure at line 3 · in fail — resolved — ", text, StringComparison.Ordinal);
		Assert.Contains(
			"Tests using changed declarations:\nsrc/errors.test.ts — .getResponse at line 4 · Failure at line 4 — resolved — ",
			text,
			StringComparison.Ordinal);
		Assert.Contains(
			"Other dependents, using no changed declaration:\nsrc/unrelated.ts — import at line 1 — resolved — ",
			text,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task RelatedFilesFocusesOnTheCallsOwnGitScope()
	{
		using var workspace = new TemporaryDirectory();
		using var repository = WritePythonFocusFixture();
		repository.Write("pkg/_config.py", "__all__ = [\"Limits\"]\n\nclass Limits:\n    pass\n\nDEFAULT_TIMEOUT = 10\n");
		repository.Write("pkg/_api.py", "from ._config import DEFAULT_TIMEOUT\n\n__all__ = [\"request\"]\n\ndef request(timeout=DEFAULT_TIMEOUT):\n    return timeout * 2\n");
		repository.Commit("raise the timeout");
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);

		var text = RelatedText(await server.CallAsync("related_files", new Dictionary<string, object?>
		{
			["path"] = "pkg/_config.py",
			["direction"] = "dependents",
			["git_scope"] = "diff:HEAD~1..HEAD"
		}));

		// The scope narrows the selection to the commit's files, so the dependent from the same
		// commit is grouped and the others are named outside the selection.
		Assert.Contains(
			"Changed declarations: DEFAULT_TIMEOUT at line 6\n" +
			"Dependents using changed declarations:\n" +
			"pkg/_api.py — DEFAULT_TIMEOUT at line 5 · in request — resolved — ",
			text,
			StringComparison.Ordinal);
	}

	// The relation body uses the platform line ending; the assertions compare whole lines.
	private static string RelatedText(ModelContextProtocol.Protocol.CallToolResult result) =>
		Text(result).ReplaceLineEndings("\n");

	private static GitModeRepository WritePythonFocusFixture()
	{
		var repository = new GitModeRepository();
		repository.Write("pkg/__init__.py", "from ._api import *\nfrom ._config import *\nfrom ._transport import build\n");
		repository.Write("pkg/_config.py", "__all__ = [\"Limits\"]\n\nclass Limits:\n    pass\n\nDEFAULT_TIMEOUT = 5\n");
		repository.Write("pkg/_api.py", "from ._config import DEFAULT_TIMEOUT\n\n__all__ = [\"request\"]\n\ndef request(timeout=DEFAULT_TIMEOUT):\n    return timeout\n");
		repository.Write("pkg/_transport.py", "from ._config import Limits\n\ndef build():\n    return Limits()\n");
		repository.Write("tests/test_api.py", "import pkg\n\ndef test_request():\n    assert pkg.request() == 10\n");
		repository.Commit();
		return repository;
	}
}
