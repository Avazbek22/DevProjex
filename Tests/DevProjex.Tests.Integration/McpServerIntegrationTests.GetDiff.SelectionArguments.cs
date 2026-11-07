using DevProjex.Application.Context;
using System.Text.Json.Nodes;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	[Theory]
	[InlineData("none", null, "Working.cs")]
	[InlineData("gitignore", null, "Working.cs")]
	[InlineData("tracked", null, "Working.cs")]
	[InlineData("none", "staged", "Staged.cs")]
	[InlineData("gitignore", "staged", "Staged.cs")]
	[InlineData("tracked", "staged", "Staged.cs")]
	[InlineData("none", "diff:HEAD~1..HEAD", "RefChanged.cs")]
	[InlineData("gitignore", "diff:HEAD~1..HEAD", "RefChanged.cs")]
	[InlineData("tracked", "diff:HEAD~1..HEAD", "RefChanged.cs")]
	public async Task GetDiffNarrowsWithAProfileAndKeepsTheRequestedComparison(
		string profileMode, string? scope, string expectedFile)
	{
		using var workspace = new TemporaryDirectory();
		using var repository = new GitModeRepository();
		repository.WriteScopeMatrix();
		var profile = WritePortableProfile(repository.Root, ".diff-profile.json", [expectedFile]);
		var profilePath = Path.Combine(repository.Root, profile);
		var document = JsonNode.Parse(File.ReadAllText(profilePath))!;
		document["selection"]!["gitMode"] = profileMode;
		File.WriteAllText(profilePath, document.ToJsonString());
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);
		var arguments = new Dictionary<string, object?> { ["profile"] = profile };
		if (scope is not null)
			arguments["git_scope"] = scope;

		var response = await server.CallAsync("get_diff", arguments);
		var text = AllText(response);

		Assert.NotEqual(true, response.IsError);
		Assert.Contains($"+++ b/{expectedFile}\n", text, StringComparison.Ordinal);
		Assert.Contains($"Scope: {scope ?? "changes"}\n", text, StringComparison.Ordinal);
		Assert.Contains("· files=1 · shown=1 ·", text, StringComparison.Ordinal);
		foreach (var other in GitModeRepository.ExpectedScopeFiles("none").Where(name => name != expectedFile))
			Assert.DoesNotContain(other, text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetDiffTrackedOnlyRemovesUntrackedChanges()
	{
		using var workspace = new TemporaryDirectory();
		using var repository = new GitModeRepository();
		repository.Write("tracked.txt", "before\n");
		repository.Commit();
		repository.Write("tracked.txt", "after\n");
		repository.Write("untracked.txt", "new\n");
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);
		var control = await server.CallAsync("get_diff");
		Assert.NotEqual(true, control.IsError);
		Assert.Contains("+++ b/untracked.txt\n", AllText(control), StringComparison.Ordinal);

		var response = await server.CallAsync("get_diff", new Dictionary<string, object?> { ["tracked_only"] = true });
		var text = AllText(response);

		Assert.NotEqual(true, response.IsError);
		Assert.Contains("+++ b/tracked.txt\n", text, StringComparison.Ordinal);
		Assert.DoesNotContain("untracked.txt", text, StringComparison.Ordinal);
		Assert.Contains("Scope: changes\n", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetDiffMaximumFileBytesRemovesOversizedChanges()
	{
		using var workspace = new TemporaryDirectory();
		using var repository = new GitModeRepository();
		repository.Write("small.txt", "before\n");
		repository.Write("large.txt", "before\n");
		repository.Commit();
		repository.Write("small.txt", "after\n");
		repository.Write("large.txt", new string('x', 1_000) + "\n");
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path);
		var control = await server.CallAsync("get_diff");
		Assert.NotEqual(true, control.IsError);
		Assert.Contains("+++ b/large.txt\n", AllText(control), StringComparison.Ordinal);

		var response = await server.CallAsync("get_diff", new Dictionary<string, object?> { ["max_file_bytes"] = 100 });
		var text = AllText(response);

		Assert.NotEqual(true, response.IsError);
		Assert.Contains("+++ b/small.txt\n", text, StringComparison.Ordinal);
		Assert.DoesNotContain("large.txt", text, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task GetDiffUsesTheSameExclusionDelegationAsTheTree(bool delegated)
	{
		using var workspace = new TemporaryDirectory();
		using var repository = new GitModeRepository();
		repository.Write("visible.txt", "before\n");
		repository.Write(".hidden.txt", "before\n");
		repository.Commit();
		repository.Write("visible.txt", "after\n");
		repository.Write(".hidden.txt", "after\n");
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path,
			exclusions: [ProjectExclusion.DotFiles], agentExclusions: delegated);
		var control = await server.CallAsync("get_diff");
		Assert.NotEqual(true, control.IsError);
		Assert.DoesNotContain(".hidden.txt", AllText(control), StringComparison.Ordinal);
		var arguments = new Dictionary<string, object?> { ["exclusions"] = Array.Empty<string>() };

		var response = await server.CallAsync("get_diff", arguments);
		var tree = await server.CallAsync("get_tree", arguments);

		Assert.Equal(tree.IsError, response.IsError);
		if (!delegated)
		{
			Assert.True(response.IsError);
			foreach (var result in new[] { response, tree })
				Assert.StartsWith("DPX-MCP-INVALID-ARGUMENTS: unknown argument(s): exclusions. Valid arguments: ",
					ExtractSpotlightBody(AllText(result)), StringComparison.Ordinal);
			return;
		}
		Assert.NotEqual(true, response.IsError);
		Assert.Contains("+++ b/.hidden.txt\n", AllText(response), StringComparison.Ordinal);
		Assert.Contains("+++ b/visible.txt\n", AllText(response), StringComparison.Ordinal);
		var narrowed = await server.CallAsync("get_diff", new Dictionary<string, object?> { ["exclusions"] = new[] { "dot-files" } });
		Assert.NotEqual(true, narrowed.IsError);
		Assert.DoesNotContain(".hidden.txt", AllText(narrowed), StringComparison.Ordinal);
		Assert.Contains("+++ b/visible.txt\n", AllText(narrowed), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("profile", 42)]
	[InlineData("tracked_only", "not-a-boolean")]
	[InlineData("max_file_bytes", 0)]
	[InlineData("max_file_bytes", "9223372036854775808")]
	[InlineData("exclusions", "dot-files")]
	public async Task GetDiffKeepsTheSharedSelectionValidation(string argument, object value)
	{
		using var workspace = new TemporaryDirectory();
		using var repository = new GitModeRepository();
		repository.Write("app.txt", "before\n");
		repository.Commit();
		await using var server = await McpTestServer.StartAsync(repository.Root, workspace.Path, agentExclusions: true);
		var arguments = new Dictionary<string, object?> { [argument] = value };

		var response = await server.CallAsync("get_diff", arguments);
		var neighbour = await server.CallAsync(argument == "profile" ? "pack_context" : "get_tree", arguments);

		Assert.True(response.IsError);
		Assert.True(neighbour.IsError);
		Assert.Equal(ExtractSpotlightBody(AllText(neighbour)), ExtractSpotlightBody(AllText(response)));
	}
}
