using DevProjex.Infrastructure.ProjectProfiles;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Theory]
	[InlineData("none", false)]
	[InlineData("gitignore", false)]
	[InlineData("tracked", false)]
	[InlineData("staged", false)]
	[InlineData("changes", false)]
	[InlineData("diff:HEAD~1..HEAD", false)]
	[InlineData("none", true)]
	[InlineData("gitignore", true)]
	[InlineData("tracked", true)]
	[InlineData("staged", true)]
	[InlineData("changes", true)]
	[InlineData("diff:HEAD~1..HEAD", true)]
	public async Task RealServerTreeAndSearchAgreeWithTheGitModeFileSet(string mode, bool live)
	{
		using var repository = new GitModeRepository();
		using var workspace = new TemporaryDirectory();
		repository.WriteScopeMatrix();
		var momentary = mode is "staged" or "changes" || mode.StartsWith("diff:", StringComparison.Ordinal);
		string[] startup = ["--git-mode", momentary ? "none" : mode, "--exclude", "none"];
		await using var server = await ActualMcpProcess.StartAsync(
			repository.Root, workspace.Path, live ? [.. startup, "--live"] : startup,
			environment: GitModeEnvironment(repository));
		if (live)
			await SaveGitModeProfileAsync(server, workspace.Path, mode);
		foreach (var tool in new[] { "get_tree", "search_project" })
		{
			var arguments = new Dictionary<string, object?> { ["include_patterns"] = new[] { "*.cs" } };
			if (momentary)
				arguments["git_scope"] = mode;
			if (tool == "get_tree")
				arguments["format"] = "text";
			else
				arguments["pattern"] = "needle";
			var response = await CallAsync(server, tool, arguments);
			var text = string.Join('\n', response.Content.OfType<TextContentBlock>().Select(static item => item.Text));
			Assert.False(response.IsError == true, text);
			Assert.Equal(GitModeRepository.ExpectedScopeFiles(mode), GitModeRepository.ExpectedScopeFiles("none")
				.Where(name => text.Contains(name, StringComparison.Ordinal)));
			var trusted = OutsideSelectionTrustedText(text);
			foreach (var name in GitModeRepository.ExpectedScopeFiles("none"))
				Assert.DoesNotContain(name, trusted, StringComparison.Ordinal);
		}
	}

	[Theory]
	[InlineData("staged", false)]
	[InlineData("changes", false)]
	[InlineData("diff:HEAD~1..HEAD", false)]
	[InlineData("staged", true)]
	[InlineData("changes", true)]
	[InlineData("diff:HEAD~1..HEAD", true)]
	public async Task RealServerRelatedLiftsTheMomentaryScopeButPreservesTrackedBaseline(string scope, bool live)
	{
		using var repository = new GitModeRepository();
		using var workspace = new TemporaryDirectory();
		repository.Write("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
		repository.Write("Seed.cs", "public class Seed { Target Value; }\n");
		repository.Write("Target.cs", "public class Target { }\n");
		repository.Commit();
		repository.Write("Seed.cs", "public class Seed { Target Value; Untracked Other; }\n");
		repository.Commit();
		repository.Write("Seed.cs", "public class Seed { Target Value; Untracked Other; int Edit; }\n");
		repository.Git("add", "Seed.cs");
		repository.Write("Untracked.cs", "public class Untracked { }\n");
		string[] startup = ["--git-mode", "tracked", "--exclude", "none"];
		await using var server = await ActualMcpProcess.StartAsync(
			repository.Root, workspace.Path, live ? [.. startup, "--live"] : startup,
			environment: GitModeEnvironment(repository));
		if (live)
			await SaveGitModeProfileAsync(server, workspace.Path, "tracked", ["Seed.cs"]);
		var response = await CallAsync(server, "related_files", new Dictionary<string, object?>
		{
			["path"] = "Seed.cs",
			["direction"] = "dependencies",
			["git_scope"] = scope
		});
		var text = string.Join('\n', response.Content.OfType<TextContentBlock>().Select(static item => item.Text));
		Assert.False(response.IsError == true, text);
		Assert.Contains("Target.cs — ", text, StringComparison.Ordinal);
		Assert.Contains("Dependencies outside the selection:", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Untracked.cs", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Target.cs", OutsideSelectionTrustedText(text), StringComparison.Ordinal);
		var readable = await CallAsync(server, "get_file", new Dictionary<string, object?> { ["path"] = "Target.cs" });
		Assert.NotEqual(true, readable.IsError);
		var excluded = await CallAsync(server, "get_file", new Dictionary<string, object?> { ["path"] = "Untracked.cs" });
		Assert.True(excluded.IsError);
	}

	private static Dictionary<string, string> GitModeEnvironment(GitModeRepository repository) => new()
	{
		["GIT_CONFIG_NOSYSTEM"] = "1",
		["GIT_CONFIG_GLOBAL"] = repository.UserConfiguration,
		["HOME"] = Path.GetDirectoryName(repository.Root)!,
		["XDG_CONFIG_HOME"] = Path.Combine(Path.GetDirectoryName(repository.Root)!, "config")
	};

	private static async Task SaveGitModeProfileAsync(ActualMcpProcess server, string dataRoot, string mode, IReadOnlyCollection<string>? focus = null)
	{
		var listed = await CallAsync(server, "list_projects", new Dictionary<string, object?>());
		var canonical = Assert.Single(Structured(listed).GetProperty("projects").EnumerateArray()).GetProperty("path").GetString()!;
		var states = Enum.GetValues<IgnoreOptionId>().ToDictionary(static key => key, static _ => false);
		states[IgnoreOptionId.UseGitIgnore] = mode == "gitignore";
		states[IgnoreOptionId.TrackedGitFilesOnly] = mode == "tracked";
		Assert.True(new ProjectProfileStore(() => dataRoot).TrySaveProfile(canonical,
			new ProjectSelectionProfile([], [".cs"], states.Where(static item => item.Value).Select(static item => item.Key).ToArray(),
				IgnoreOptionStates: states, SelectedPaths: focus)));
	}

	[Theory]
	[InlineData("none", false, true, true)]
	[InlineData("gitignore", false, true, false)]
	[InlineData("tracked", false, false, false)]
	[InlineData("none", true, false, false)]
	public async Task RealServerOutsideSelectionNeverExpandsPersistentGitFilters(string mode, bool trackedOnly, bool untrackedVisible, bool ignoredVisible)
	{
		using var repository = new GitModeRepository();
		using var workspace = new TemporaryDirectory();
		repository.Write("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
		repository.Write("Seed.cs", "public class Seed { Target A; Untracked B; Ignored C; }\n");
		repository.Write("Target.cs", "public class Target { }\n");
		repository.Write(".gitignore", "Ignored.cs\n");
		repository.Commit();
		repository.Write("Untracked.cs", "public class Untracked { }\n");
		repository.Write("Ignored.cs", "public class Ignored { }\n");
		await using var server = await ActualMcpProcess.StartAsync(repository.Root, workspace.Path,
			["--git-mode", mode, "--exclude", "none"], environment: GitModeEnvironment(repository));
		var arguments = new Dictionary<string, object?>
		{
			["path"] = "Seed.cs",
			["direction"] = "dependencies",
			["include_patterns"] = new[] { "Seed.cs" }
		};
		if (trackedOnly)
			arguments["tracked_only"] = true;
		var response = await CallAsync(server, "related_files", arguments);
		var text = string.Join('\n', response.Content.OfType<TextContentBlock>().Select(static item => item.Text));
		Assert.False(response.IsError == true, text);
		Assert.Contains("Target.cs — ", text, StringComparison.Ordinal);
		Assert.Equal(untrackedVisible, text.Contains("Untracked.cs — ", StringComparison.Ordinal));
		Assert.Equal(ignoredVisible, text.Contains("Ignored.cs — ", StringComparison.Ordinal));
	}

	[Fact]
	public async Task RealServerMissingShallowParentReturnsAnUnavailableStateInsteadOfAnEmptySuccess()
	{
		using var repository = new GitModeRepository();
		using var workspace = new TemporaryDirectory();
		repository.Write("file.txt", "baseline\n");
		repository.Commit();
		repository.Write("file.txt", "changed\n");
		repository.Commit();
		var clone = workspace.CreateDirectory("shallow");
		repository.Git("clone", "--quiet", "--depth", "1", new Uri(repository.Root + Path.DirectorySeparatorChar).AbsoluteUri, clone);
		Assert.Equal("true", repository.GitAt(clone, "rev-parse", "--is-shallow-repository").Trim());
		await using var server = await ActualMcpProcess.StartAsync(clone, workspace.CreateDirectory("data"),
			["--git-mode", "none", "--exclude", "none"], environment: GitModeEnvironment(repository));
		var response = await CallAsync(server, "get_tree", new Dictionary<string, object?> { ["git_scope"] = "diff:HEAD~1..HEAD" });
		Assert.True(response.IsError);
		Assert.Contains("DPX-GIT-STATE-UNAVAILABLE", string.Join('\n', response.Content.OfType<TextContentBlock>().Select(static item => item.Text)), StringComparison.Ordinal);
	}
}
