namespace DevProjex.Tests.Terminal;

public sealed partial class GitModeCommandContractTests
{
	[Theory]
	[InlineData("changes", "Working.cs")]
	[InlineData("staged", "Staged.cs")]
	[InlineData("diff:HEAD~1..HEAD", "RefChanged.cs")]
	public void RealDiffCommandPrintsTheChangedLinesOfEachScope(string mode, string file)
	{
		using var repository = new GitModeRepository();
		using var state = new TemporaryDirectory();
		repository.WriteScopeMatrix();
		var name = Path.GetFileNameWithoutExtension(file);

		var result = RunScopeCommand(repository, state.Path,
			["diff", "--project", repository.Root, "--git-mode", mode, "--exclude", "none", "--extension", ".cs",
			 "--language", "en", "--plain"]);

		Assert.True(result.ExitCode == 0, result.StandardError);
		Assert.Contains(
			$"--- a/{file}\n+++ b/{file}\n@@ -1 +1 @@\n-public class {name} {{ }} // needle\n+public class {name} {{ int Value; }} // needle\n",
			result.StandardOutput,
			StringComparison.Ordinal);
		var expected = GitModeRepository.ExpectedScopeFiles(mode);
		Assert.Equal(expected, GitModeRepository.ExpectedScopeFiles("none")
			.Where(candidate => result.StandardOutput.Contains(candidate, StringComparison.Ordinal)));
		Assert.EndsWith(
			$"Changed files: {expected.Length}, shown: {expected.Length}; lines added: {expected.Length}, " +
			$"removed: {(mode == "changes" ? 2 : 1)}.\n",
			result.StandardOutput,
			StringComparison.Ordinal);
		Assert.DoesNotContain("\r", result.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public void RealDiffCommandDefaultsToUncommittedChangesAndWritesJson()
	{
		using var repository = new GitModeRepository();
		using var state = new TemporaryDirectory();
		repository.Write("app.txt", "one\ntwo\nthree\n");
		File.WriteAllBytes(Path.Combine(repository.Root, "logo.png"), [0x89, 0x50, 0x4E, 0x47, 0, 1]);
		repository.Commit();
		repository.Write("app.txt", "one\nTWO\nthree\n");
		File.WriteAllBytes(Path.Combine(repository.Root, "logo.png"), [0x89, 0x50, 0x4E, 0x47, 0, 2]);

		var result = RunScopeCommand(repository, state.Path,
			["diff", "--project", repository.Root, "--context", "0", "--format", "json", "--exclude", "none"]);

		Assert.True(result.ExitCode == 0, result.StandardError);
		using var document = JsonDocument.Parse(result.StandardOutput);
		var root = document.RootElement;
		Assert.Equal("devprojex-diff", root.GetProperty("kind").GetString());
		Assert.Equal("changes", root.GetProperty("scope").GetString());
		var file = Assert.Single(root.GetProperty("files").EnumerateArray());
		Assert.Equal("app.txt", file.GetProperty("path").GetString());
		Assert.Equal("modified", file.GetProperty("status").GetString());
		Assert.False(file.TryGetProperty("oldPath", out _));
		var hunk = Assert.Single(file.GetProperty("hunks").EnumerateArray());
		Assert.Equal((2, 1, 2, 1), (
			hunk.GetProperty("oldStart").GetInt32(),
			hunk.GetProperty("oldLines").GetInt32(),
			hunk.GetProperty("newStart").GetInt32(),
			hunk.GetProperty("newLines").GetInt32()));
		Assert.Equal(["-two", "+TWO"], hunk.GetProperty("lines").EnumerateArray().Select(static line => line.GetString()));
		var omitted = Assert.Single(root.GetProperty("notShown").EnumerateArray());
		Assert.Equal("logo.png", omitted.GetProperty("path").GetString());
		Assert.Equal("binary", omitted.GetProperty("reason").GetString());
		Assert.Equal(1, root.GetProperty("counts").GetProperty("binary").GetInt32());
		Assert.False(root.GetProperty("complete").GetBoolean());
	}

	[Fact]
	public void RealDiffCommandMasksASecretOnBothSidesWhenSecretsAreHidden()
	{
		var secret = "ghp_" + "Qw8Er7Ty6Ui5Op4As3Df2Gh1Jk0Lz9Xc8Vb7";
		var rotated = "ghp_" + "Mn1Bv2Cx3Za4Sd5Fg6Hj7Kl8Po9Iu0Yt1Re2";
		using var repository = new GitModeRepository();
		using var state = new TemporaryDirectory();
		repository.Write("settings.txt", $"kept = \"{secret}\"\nrotated = \"{rotated}\"\n");
		repository.Commit();
		repository.Write("settings.txt", $"kept = \"{secret}\"\nrotated = \"plain\"\n");

		var result = RunScopeCommand(repository, state.Path,
			["diff", "--project", repository.Root, "--hide-secrets", "--exclude", "none", "--language", "en"]);

		Assert.True(result.ExitCode == 0, result.StandardError);
		Assert.DoesNotContain(secret[..^1], result.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain(rotated[..^1], result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains(" kept = \"DEVPROJEX_REDACTED[", result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("-rotated = \"DEVPROJEX_REDACTED[", result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("+rotated = \"plain\"", result.StandardOutput, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("--git-mode", "tracked")]
	[InlineData("--git-mode", "gitignore")]
	[InlineData("--context", "21")]
	[InlineData("--context", "-1")]
	public void RealDiffCommandRefusesAnInvalidScopeOrContext(string option, string value)
	{
		using var repository = new GitModeRepository();
		using var state = new TemporaryDirectory();
		repository.Write("app.txt", "one\n");
		repository.Commit();

		var result = RunScopeCommand(repository, state.Path,
			["diff", "--project", repository.Root, option, value, "--language", "en"]);

		Assert.Equal(2, result.ExitCode);
		Assert.Empty(result.StandardOutput);
	}

	[Fact]
	public void RealDiffCommandReportsAnEmptyScopeAsSuccess()
	{
		using var repository = new GitModeRepository();
		using var state = new TemporaryDirectory();
		repository.Write("app.txt", "one\n");
		repository.Commit();

		var result = RunScopeCommand(repository, state.Path,
			["diff", "--project", repository.Root, "--language", "en"]);

		Assert.True(result.ExitCode == 0, result.StandardError);
		Assert.Equal("No changes in the effective selection for this Git scope.\n", result.StandardOutput);
	}
}
