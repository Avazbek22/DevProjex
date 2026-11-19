using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Theory]
	[InlineData("changes", "Working.cs", "-public class Working { } // needle\n+public class Working { int Value; } // needle\n")]
	[InlineData("staged", "Staged.cs", "-public class Staged { } // needle\n+public class Staged { int Value; } // needle\n")]
	[InlineData("diff:HEAD~1..HEAD", "RefChanged.cs", "-public class RefChanged { } // needle\n+public class RefChanged { int Value; } // needle\n")]
	public async Task RealServerGetDiffReturnsTheChangedLinesOfEachScope(string scope, string file, string lines)
	{
		using var repository = new GitModeRepository();
		using var workspace = new TemporaryDirectory();
		repository.WriteScopeMatrix();
		await using var server = await ActualMcpProcess.StartAsync(
			repository.Root,
			workspace.Path,
			["--exclude", "none"],
			environment: GitModeEnvironment(repository));

		var response = await CallAsync(server, "get_diff", new Dictionary<string, object?> { ["git_scope"] = scope });
		var text = string.Join('\n', response.Content.OfType<TextContentBlock>().Select(static item => item.Text));

		Assert.False(response.IsError == true, text);
		Assert.Contains($"--- a/{file}\n+++ b/{file}\n@@ -1 +1 @@\n{lines}", text, StringComparison.Ordinal);
		Assert.Equal(
			GitModeRepository.ExpectedScopeFiles(scope),
			GitModeRepository.ExpectedScopeFiles("none").Where(name => text.Contains(name, StringComparison.Ordinal)));
		var trusted = OutsideSelectionTrustedText(text);
		foreach (var name in GitModeRepository.ExpectedScopeFiles("none"))
			Assert.DoesNotContain(name, trusted, StringComparison.Ordinal);
		Assert.Contains($"[Diff] scope={(scope.StartsWith("diff:", StringComparison.Ordinal) ? "diff" : scope)} · ", trusted, StringComparison.Ordinal);
		Assert.Contains("· complete", trusted, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealServerGetDiffNeverReturnsASecretFromEitherVersion()
	{
		var rotated = "ghp_" + "Rt5Yu6Io7Pa8Sd9Fg0Hj1Kl2Zx3Cv4Bn5Mq6";
		using var repository = new GitModeRepository();
		using var workspace = new TemporaryDirectory();
		repository.Write("settings.txt", $"header\nkept = \"{Secret}\"\nrotated = \"{rotated}\"\nfooter\n");
		repository.Commit();
		repository.Write("settings.txt", $"header\nkept = \"{Secret}\"\nrotated = \"changed\"\nfooter\n");
		await using var server = await ActualMcpProcess.StartAsync(
			repository.Root,
			workspace.Path,
			environment: GitModeEnvironment(repository));

		var response = await CallAsync(server, "get_diff", new Dictionary<string, object?>());
		var text = string.Join('\n', response.Content.OfType<TextContentBlock>().Select(static item => item.Text));

		Assert.False(response.IsError == true, text);
		Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
		Assert.DoesNotContain(rotated, text, StringComparison.Ordinal);
		Assert.Contains(" kept = \"DEVPROJEX_REDACTED[", text, StringComparison.Ordinal);
		Assert.Contains("-rotated = \"DEVPROJEX_REDACTED[", text, StringComparison.Ordinal);
		Assert.Contains("+rotated = \"changed\"", text, StringComparison.Ordinal);
		Assert.Contains("[Diff] scope=changes · files=1 · shown=1 · added=1 · removed=1 · complete", text, StringComparison.Ordinal);
	}
}
