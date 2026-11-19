namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	[Fact]
	public async Task GetDiffMasksTheOldSideOfAStagedRenameByTheRulesOfItsOwnName()
	{
		const string secret = "short signing phrase";
		using var workspace = new TemporaryDirectory();
		using var repository = new GitModeRepository();
		repository.Write(
			"settings.py",
			$"SECRET_KEY = \"{secret}\"\nDEBUG = False\nNAME = \"app\"\nPORT = 8080\nHOST = \"localhost\"\n");
		repository.Commit();
		repository.Git("mv", "settings.py", "settings_example.py");
		repository.Write(
			"settings_example.py",
			$"SECRET_KEY = \"{secret}\"\nDEBUG = True\nNAME = \"app\"\nPORT = 8080\nHOST = \"localhost\"\n");
		repository.Git("add", "settings_example.py");
		// The rename source is back in the working tree, so the staged selection names it.
		repository.Write("settings.py", "PLACEHOLDER = 1\n");
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);

		var text = AllText(await server.CallAsync("get_diff", new Dictionary<string, object?> { ["git_scope"] = "staged" }));

		Assert.Contains("settings_example.py +1 -1 [renamed from settings.py]", text, StringComparison.Ordinal);
		Assert.Contains("--- a/settings.py\n+++ b/settings_example.py\n", text, StringComparison.Ordinal);
		Assert.Contains(" SECRET_KEY = \"DEVPROJEX_REDACTED[", text, StringComparison.Ordinal);
		Assert.Contains("-DEBUG = False\n+DEBUG = True\n", text, StringComparison.Ordinal);
		Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
	}
}
