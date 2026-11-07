using DevProjex.Application.Context;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	private const string DiffNudge = "[Diff] get_diff with the same git_scope returns only the changed lines.";

	[Fact]
	public async Task GetDiffReturnsExactHunksForEveryScope()
	{
		using var workspace = new TemporaryDirectory();
		using var repository = new GitModeRepository();
		repository.Write("app.txt", "one\ntwo\nthree\n");
		repository.Commit("first");
		repository.Write("app.txt", "one\nTWO\nthree\n");
		repository.Commit("second");
		repository.Write("staged.txt", "staged\n");
		repository.Git("add", "staged.txt");
		repository.Write("app.txt", "one\nTWO\nthree\nfour\n");
		repository.Write("new.txt", "new\n");
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);

		var changes = Text(await server.CallAsync("get_diff"));
		var staged = Text(await server.CallAsync("get_diff", new Dictionary<string, object?> { ["git_scope"] = "staged" }));
		var range = Text(await server.CallAsync("get_diff", new Dictionary<string, object?> { ["git_scope"] = "diff:HEAD~1..HEAD" }));
		var tight = Text(await server.CallAsync("get_diff", new Dictionary<string, object?> { ["context_lines"] = 0, ["paths"] = new[] { "app.txt" } }));

		Assert.Contains(
			"Scope: changes\n\nChanged files (largest first):\napp.txt +1 -0 [modified]\nnew.txt +1 -0 [added]\nstaged.txt +1 -0 [added]\n\n" +
			"--- a/app.txt\n+++ b/app.txt\n@@ -1,3 +1,4 @@\n one\n TWO\n three\n+four\n\n" +
			"--- /dev/null\n+++ b/new.txt\n@@ -0,0 +1 @@\n+new\n\n" +
			"--- /dev/null\n+++ b/staged.txt\n@@ -0,0 +1 @@\n+staged\n</untrusted-data-",
			changes,
			StringComparison.Ordinal);
		Assert.Contains("[Diff] scope=changes · files=3 · shown=3 · added=3 · removed=0 · complete", changes, StringComparison.Ordinal);
		Assert.Contains("--- /dev/null\n+++ b/staged.txt\n@@ -0,0 +1 @@\n+staged\n", staged, StringComparison.Ordinal);
		Assert.DoesNotContain("app.txt", staged, StringComparison.Ordinal);
		Assert.Contains("[Diff] scope=staged · files=1 · shown=1 · added=1 · removed=0 · complete", staged, StringComparison.Ordinal);
		Assert.Contains("Scope: diff:HEAD~1..HEAD\n", range, StringComparison.Ordinal);
		Assert.Contains("--- a/app.txt\n+++ b/app.txt\n@@ -1,3 +1,3 @@\n one\n-two\n+TWO\n three\n", range, StringComparison.Ordinal);
		Assert.Contains("[Diff] scope=diff · files=1 · shown=1 · added=1 · removed=1 · complete", range, StringComparison.Ordinal);
		Assert.DoesNotContain("HEAD~1", OutsideUntrustedText(range), StringComparison.Ordinal);
		Assert.Contains("@@ -3,0 +4 @@\n+four\n", tight, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetDiffComparesLinesWhenTheWorkingTreeIsCrlf()
	{
		using var workspace = new TemporaryDirectory();
		using var repository = new GitModeRepository();
		repository.Git("config", "core.autocrlf", "true");
		repository.Git("config", "--unset", "core.eol");
		repository.Write("crlf.txt", "alpha\r\nbeta\r\ngamma\r\ndelta\r\n");
		repository.Commit();
		Assert.DoesNotContain("\r", repository.Git("show", "HEAD:crlf.txt"), StringComparison.Ordinal);
		repository.Write("crlf.txt", "alpha\r\nBETA\r\ngamma\r\ndelta\r\n");
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);

		var text = Text(await server.CallAsync("get_diff"));

		Assert.Contains("@@ -1,4 +1,4 @@\n alpha\n-beta\n+BETA\n gamma\n delta\n", text, StringComparison.Ordinal);
		Assert.Contains("[Diff] scope=changes · files=1 · shown=1 · added=1 · removed=1 · complete", text, StringComparison.Ordinal);
		Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetDiffCountsALineEndingOnlyChangeWithoutHunks()
	{
		using var workspace = new TemporaryDirectory();
		using var repository = new GitModeRepository();
		repository.Write("endings.txt", "x\ny\n");
		repository.Commit();
		repository.Write("endings.txt", "x\r\ny\r\n");
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);

		var text = Text(await server.CallAsync("get_diff"));

		Assert.Contains("endings.txt [modified, line-endings-only]", text, StringComparison.Ordinal);
		Assert.DoesNotContain("@@", text, StringComparison.Ordinal);
		Assert.Contains("[Diff] scope=changes · files=1 · shown=0 · added=0 · removed=0 · complete", text, StringComparison.Ordinal);
		Assert.Contains("[Diff] not shown: line-endings-only=1", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetDiffMasksSecretsInBothVersionsWithOneReplacementPerValue()
	{
		var unchanged = "ghp_" + "Kq3VwX8rT2mN6pL9sD4fG7hJ1zC5bE0aY3uR";
		var replaced = "ghp_" + "Zx4Cv7Bn2Mq9Wr5Ty8Ui3Op6As1Df0Gh7Jk2";
		var replacement = "ghp_" + "Pl0Ok9Ij8Uh7Yg6Tf5Rd4Es3Wa2Qz1Xc0Vb9";
		var removed = "ghp_" + "Mn5Bv4Cx3Zl2Kj1Hg0Fd9Sa8Po7Iu6Yt5Re4";
		using var workspace = new TemporaryDirectory();
		using var repository = new GitModeRepository();
		repository.Write(
			"settings.txt",
			$"header\nunchanged_token = \"{unchanged}\"\nlevel = 1\nrotated_token = \"{replaced}\"\nremoved_token = \"{removed}\"\nfooter\n");
		repository.Commit();
		repository.Write(
			"settings.txt",
			$"header\nunchanged_token = \"{unchanged}\"\nlevel = 2\nrotated_token = \"{replacement}\"\nfooter\n");
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);

		var result = await server.CallAsync("get_diff");
		var text = AllText(result);

		foreach (var secret in new[] { unchanged, replaced, replacement, removed })
			Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
		var lines = text.Split('\n');
		var unchangedLine = Assert.Single(lines, static line => line.Contains("unchanged_token", StringComparison.Ordinal));
		Assert.StartsWith(" unchanged_token = \"DEVPROJEX_REDACTED[", unchangedLine, StringComparison.Ordinal);
		var rotated = lines.Where(static line => line.Contains("rotated_token", StringComparison.Ordinal)).ToArray();
		Assert.Equal(2, rotated.Length);
		Assert.StartsWith("-rotated_token = \"DEVPROJEX_REDACTED[", rotated[0], StringComparison.Ordinal);
		Assert.StartsWith("+rotated_token = \"DEVPROJEX_REDACTED[", rotated[1], StringComparison.Ordinal);
		Assert.NotEqual(rotated[0][1..], rotated[1][1..]);
		Assert.StartsWith("-removed_token = \"DEVPROJEX_REDACTED[", Assert.Single(lines, static line => line.Contains("removed_token", StringComparison.Ordinal)), StringComparison.Ordinal);
		Assert.Contains("[Diff] scope=changes · files=1 · shown=1 · added=2 · removed=3 · complete", text, StringComparison.Ordinal);
		Assert.Contains("[Protection] secrets=always", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetDiffNeverNamesFilesOutsideTheEffectiveSelection()
	{
		const string marker = "DIFF_HIDDEN_SENTINEL_5b1e";
		using var workspace = new TemporaryDirectory();
		using var repository = new GitModeRepository();
		repository.Write(".gitignore", $"ignored-{marker}.txt\n");
		repository.Write("app.txt", "visible\n");
		repository.Write("package.json", "{}\n");
		repository.Write($".dot-{marker}.txt", "dot\n");
		repository.Write($"node_modules/pkg-{marker}/index.js", "module.exports = 1;\n");
		repository.Write($"docs/excluded-{marker}.md", "doc\n");
		repository.Write($"gone-{marker}.txt", "gone\n");
		repository.Commit();
		repository.Write("app.txt", "visible changed\n");
		repository.Write($".dot-{marker}.txt", "dot changed\n");
		repository.Write($"node_modules/pkg-{marker}/index.js", "module.exports = 2;\n");
		repository.Write($"docs/excluded-{marker}.md", "doc changed\n");
		repository.Write($"ignored-{marker}.txt", "ignored\n");
		File.Delete(Path.Combine(repository.Root, $"gone-{marker}.txt"));
		await using var server = await McpTestServer.StartAsync(
			repository.Root,
			workspace.Path,
			exclusions: [ProjectExclusion.SmartIgnore, ProjectExclusion.DotFiles, ProjectExclusion.EmptyFolders]);

		var text = AllText(await server.CallAsync(
			"get_diff",
			new Dictionary<string, object?> { ["exclude_patterns"] = new[] { "docs/**" } }));

		Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
		Assert.Contains("app.txt +1 -1 [modified]", text, StringComparison.Ordinal);
		Assert.Contains("[Diff] scope=changes · files=1 · shown=1 · added=1 · removed=1 · complete", text, StringComparison.Ordinal);
		Assert.Contains("[Diff] not shown: deleted=1", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetDiffListsABinaryFileWithoutItsBytes()
	{
		using var workspace = new TemporaryDirectory();
		using var repository = new GitModeRepository();
		File.WriteAllBytes(Path.Combine(repository.Root, "logo.png"), [0x89, 0x50, 0x4E, 0x47, 0, 1, 2, 3]);
		repository.Write("app.txt", "one\n");
		repository.Commit();
		File.WriteAllBytes(Path.Combine(repository.Root, "logo.png"), [0x89, 0x50, 0x4E, 0x47, 0, 9, 9, 9]);
		repository.Write("app.txt", "two\n");
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);

		var text = Text(await server.CallAsync("get_diff"));

		Assert.Contains("logo.png [modified, binary]", text, StringComparison.Ordinal);
		Assert.DoesNotContain("+++ b/logo.png", text, StringComparison.Ordinal);
		Assert.Contains("[Diff] scope=changes · files=2 · shown=1 · added=1 · removed=1 · complete", text, StringComparison.Ordinal);
		Assert.Contains("[Diff] not shown: binary=1", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetDiffStaysWithinTheResponseLimitAndListsWhatItLeftOut()
	{
		using var workspace = new TemporaryDirectory();
		using var repository = new GitModeRepository();
		repository.Write("keep.txt", "keep\n");
		repository.Commit();
		foreach (var (name, lines) in new[] { ("large-a.txt", 760), ("large-b.txt", 740), ("large-c.txt", 720) })
		{
			repository.Write(name, string.Concat(Enumerable.Range(0, lines).Select(index =>
				$"{name} line {index:D5} with enough text to be measurable\n")));
		}
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);

		var result = await server.CallAsync("get_diff");
		var text = AllText(result);

		Assert.InRange(text.Length, 1, 50_000);
		Assert.Contains("+++ b/large-a.txt\n", text, StringComparison.Ordinal);
		Assert.DoesNotContain("+++ b/large-b.txt", text, StringComparison.Ordinal);
		Assert.Contains("Not shown:\nlarge-b.txt +740 -0 [added]\nlarge-c.txt +720 -0 [added]\n</untrusted-data-", text, StringComparison.Ordinal);
		Assert.Contains("[Diff] scope=changes · files=3 · shown=1 · added=2220 · removed=0 · partial", text, StringComparison.Ordinal);
		Assert.Contains(
			"[Diff] partial · 2 file(s) not shown to stay within 50,000 characters; narrow paths or include_patterns, or lower context_lines.",
			text,
			StringComparison.Ordinal);
		Assert.DoesNotContain("large-b.txt", OutsideUntrustedText(text), StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetDiffTreatsAnUnbornBranchAsAllAdded()
	{
		using var workspace = new TemporaryDirectory();
		using var repository = new GitModeRepository();
		repository.Write("first.txt", "first\n");
		repository.Git("add", "first.txt");
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);

		var staged = Text(await server.CallAsync("get_diff", new Dictionary<string, object?> { ["git_scope"] = "staged" }));
		var changes = Text(await server.CallAsync("get_diff"));

		foreach (var text in new[] { staged, changes })
		{
			Assert.Contains("--- /dev/null\n+++ b/first.txt\n@@ -0,0 +1 @@\n+first\n", text, StringComparison.Ordinal);
			Assert.Contains("· files=1 · shown=1 · added=1 · removed=0 · complete", text, StringComparison.Ordinal);
		}
	}

	[Fact]
	public async Task GetDiffShowsAStagedRenameAsAnAddedFileAndADeletion()
	{
		using var workspace = new TemporaryDirectory();
		using var repository = new GitModeRepository();
		repository.Write("old-name.txt", "content\n");
		repository.Commit();
		repository.Git("mv", "old-name.txt", "new-name.txt");
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);

		var text = AllText(await server.CallAsync("get_diff", new Dictionary<string, object?> { ["git_scope"] = "staged" }));

		Assert.Contains("new-name.txt +1 -0 [added]", text, StringComparison.Ordinal);
		Assert.DoesNotContain("old-name.txt", text, StringComparison.Ordinal);
		Assert.Contains("[Diff] not shown: deleted=1", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AnUnknownRevisionInAShallowCloneIsAnArgumentErrorThatSaysSo()
	{
		using var workspace = new TemporaryDirectory();
		using var origin = new GitModeRepository();
		origin.Write("app.txt", "one\n");
		origin.Commit("first");
		origin.Write("app.txt", "two\n");
		origin.Commit("second");
		var shallow = Path.Combine(workspace.Path, "shallow");
		origin.GitAt(workspace.Path, "clone", "--quiet", "--depth", "1", new Uri(origin.Root).AbsoluteUri, shallow);
		await using var server = await McpTestServer.StartAsync([shallow, origin.Root], workspace.Path);

		var shallowDiff = await server.CallAsync(
			"get_diff",
			new Dictionary<string, object?> { ["project"] = shallow, ["git_scope"] = "diff:HEAD~1..HEAD" });
		var shallowTree = await server.CallAsync(
			"get_tree",
			new Dictionary<string, object?> { ["project"] = shallow, ["git_scope"] = "diff:HEAD~1..HEAD" });
		var unknown = await server.CallAsync(
			"get_diff",
			new Dictionary<string, object?> { ["project"] = origin.Root, ["git_scope"] = "diff:no-such-branch..HEAD" });

		const string shallowMessage =
			"DPX-MCP-INVALID-ARGUMENTS: git_scope names a revision this repository does not have. " +
			"This is a shallow clone, so older commits may be missing. Check the refs, or use git_scope \"changes\" or \"staged\".";
		foreach (var result in new[] { shallowDiff, shallowTree })
		{
			Assert.True(result.IsError);
			Assert.Contains(shallowMessage, Text(result), StringComparison.Ordinal);
		}
		Assert.True(unknown.IsError);
		Assert.Contains(
			"DPX-MCP-INVALID-ARGUMENTS: git_scope names a revision this repository does not have. " +
			"Check the refs, or use git_scope \"changes\" or \"staged\".",
			Text(unknown),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetDiffKeepsProjectNamesAndContentInsideUntrustedData()
	{
		const string marker = "DIFF_TRUST_SENTINEL_2c7a";
		using var workspace = new TemporaryDirectory();
		using var repository = new GitModeRepository();
		repository.Write($"src/{marker}.txt", $"before {marker}\n");
		repository.Commit();
		repository.Git("branch", $"base-{marker}");
		repository.Write($"src/{marker}.txt", $"after {marker}\n");
		repository.Commit();
		repository.Write($"src/{marker}.txt", $"working {marker}\n");
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);

		var changes = await server.CallAsync("get_diff");
		var range = await server.CallAsync(
			"get_diff",
			new Dictionary<string, object?> { ["git_scope"] = $"diff:base-{marker}..HEAD" });

		AssertMarkerOnlyInsideUntrustedData(changes, marker, "get_diff changes");
		AssertMarkerOnlyInsideUntrustedData(range, marker, "get_diff range");
		Assert.Contains($"+after {marker}", Text(range), StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetDiffNeverRunsARepositoryFilter()
	{
		using var workspace = new TemporaryDirectory();
		using var repository = new GitModeRepository();
		repository.Write(".gitattributes", "*.txt filter=hostile\n");
		repository.Write("app.txt", "one\n");
		repository.Commit("first");
		repository.Write("app.txt", "two\n");
		repository.Commit("second");
		repository.Write("app.txt", "three\n");
		repository.Git("add", "app.txt");
		var markers = Directory.CreateDirectory(Path.Combine(workspace.Path, "markers")).FullName;
		foreach (var key in new[] { "clean", "smudge", "process" })
			repository.Git("config", $"filter.hostile.{key}", CreateFilterMarkerCommand(workspace.Path, markers, key));
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);

		var staged = await server.CallAsync("get_diff", new Dictionary<string, object?> { ["git_scope"] = "staged" });
		var range = await server.CallAsync("get_diff", new Dictionary<string, object?> { ["git_scope"] = "diff:HEAD~1..HEAD" });
		var changes = await server.CallAsync("get_diff");

		Assert.Contains("-two\n+three\n", Text(staged), StringComparison.Ordinal);
		Assert.Contains("-one\n+two\n", Text(range), StringComparison.Ordinal);
		Assert.True(changes.IsError);
		Assert.Contains(GitScopeFilter.UnsafeFilterDiagnosticCode, AllText(changes), StringComparison.Ordinal);
		Assert.Empty(Directory.EnumerateFiles(markers));
	}

	[Fact]
	public async Task BranchOnALocalProjectPointsToGetDiff()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "App.cs"), "class App;\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync(
			"get_file",
			new Dictionary<string, object?> { ["path"] = "App.cs", ["branch"] = "HEAD" });

		Assert.True(result.IsError);
		Assert.Contains(
			"DPX-MCP-INVALID-ARGUMENTS: 'branch' is valid only when 'project' is a Git URL. " +
			"To compare a local project with a commit, call get_diff with git_scope \"changes\", \"staged\" or \"diff:<A>..<B>\".",
			Text(result),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task GitScopedTreeAnalysisAndPackPointToGetDiff()
	{
		using var workspace = new TemporaryDirectory();
		using var repository = new GitModeRepository();
		repository.Write("app.txt", "one\n");
		repository.Commit();
		repository.Write("app.txt", "two\n");
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);
		var scoped = new Dictionary<string, object?> { ["git_scope"] = "changes" };

		foreach (var tool in new[] { "get_tree", "analyze", "pack_context" })
		{
			var withScope = AllText(await server.CallAsync(tool, new Dictionary<string, object?>(scoped)));
			var withoutScope = AllText(await server.CallAsync(tool));
			Assert.Contains(DiffNudge, OutsideUntrustedText(withScope), StringComparison.Ordinal);
			Assert.DoesNotContain(DiffNudge, withoutScope, StringComparison.Ordinal);
		}
	}

	private static string OutsideUntrustedText(string text) => Regex.Replace(
		text,
		@"<untrusted-data-(?<nonce>[0-9a-f]{24})>\n[\s\S]*?\n</untrusted-data-\k<nonce>>",
		string.Empty);

	private static string CreateFilterMarkerCommand(string root, string markers, string name)
	{
		var programs = Directory.CreateDirectory(Path.Combine(root, "programs")).FullName;
		var marker = Path.Combine(markers, name + ".marker");
		if (OperatingSystem.IsWindows())
		{
			var script = Path.Combine(programs, name + ".ps1");
			File.WriteAllText(
				script,
				$"Set-Content -LiteralPath '{marker.Replace("'", "''", StringComparison.Ordinal)}' -Value invoked\r\nexit 1\r\n",
				new UTF8Encoding(false));
			return $"powershell.exe -NoLogo -NoProfile -NonInteractive -File '{script.Replace('\\', '/')}'";
		}
		var shell = Path.Combine(programs, name + ".sh");
		File.WriteAllText(
			shell,
			$"#!/bin/sh\nprintf invoked > '{marker.Replace("'", "'\\''", StringComparison.Ordinal)}'\nexit 1\n",
			new UTF8Encoding(false));
		File.SetUnixFileMode(shell, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		return shell;
	}
}
