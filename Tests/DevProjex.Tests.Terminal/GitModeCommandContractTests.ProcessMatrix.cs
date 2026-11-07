using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

public sealed partial class GitModeCommandContractTests
{
	[Theory]
	[InlineData("none")]
	[InlineData("gitignore")]
	[InlineData("tracked")]
	[InlineData("staged")]
	[InlineData("changes")]
	[InlineData("diff:HEAD~1..HEAD")]
	public void RealCommandsAgreeOnTheExactGitScopeFileSet(string mode)
	{
		using var repository = new GitModeRepository();
		using var state = new TemporaryDirectory();
		repository.WriteScopeMatrix();
		var expected = GitModeRepository.ExpectedScopeFiles(mode);
		string[][] commands =
		[
			["tree", repository.Root, "--format", "text"],
			["export", "context", repository.Root, "--view", "content", "--format", "json", "-o", "-"],
			["search", "needle", repository.Root, "--format", "json"]
		];
		foreach (var command in commands)
		{
			var result = RunScopeCommand(repository, state.Path,
				[.. command, "--git-mode", mode, "--exclude", "none", "--extension", ".cs", "--language", "en", "--plain"]);
			Assert.True(result.ExitCode == 0, result.StandardError);
			Assert.Empty(result.StandardError);
			var actual = GitModeRepository.ExpectedScopeFiles("none")
				.Where(name => result.StandardOutput.Contains(name, StringComparison.Ordinal)).ToArray();
			Assert.Equal(expected, actual);
		}
		var analyze = RunScopeCommand(repository, state.Path,
			["analyze", repository.Root, "--git-mode", mode, "--exclude", "none", "--extension", ".cs", "--format", "json"]);
		Assert.True(analyze.ExitCode == 0, analyze.StandardError);
		using var document = JsonDocument.Parse(analyze.StandardOutput);
		Assert.Equal(expected.Length, document.RootElement.GetProperty("inventory").GetProperty("files").GetInt32());
	}

	[Theory]
	[InlineData("staged", false)]
	[InlineData("changes", false)]
	[InlineData("diff:HEAD~1..HEAD", false)]
	[InlineData("staged", true)]
	[InlineData("changes", true)]
	[InlineData("diff:HEAD~1..HEAD", true)]
	public void RealRelatedCommandLiftsMomentaryScopeButRetainsGitIgnore(string mode, bool trackedBaseline)
	{
		using var repository = new GitModeRepository();
		using var state = new TemporaryDirectory();
		repository.Write("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
		repository.Write("Seed.cs", "public class Seed { Target Value; }\n");
		repository.Write("Target.cs", "public class Target { }\n");
		repository.Write(".gitignore", "Hidden.cs\n");
		repository.Commit();
		repository.Write("Seed.cs", "public class Seed { Target Value; Hidden Other; Untracked Third; }\n");
		repository.Commit();
		repository.Write("Seed.cs", "public class Seed { Target Value; Hidden Other; Untracked Third; int Changed; }\n");
		repository.Git("add", "Seed.cs");
		repository.Write("Hidden.cs", "public class Hidden { }\n");
		repository.Write("Untracked.cs", "public class Untracked { }\n");
		var profile = trackedBaseline
			? state.WriteFile("profile.json", "{\"schemaVersion\":2,\"selection\":{\"gitMode\":\"tracked\",\"exclusions\":[]}}")
			: "standard";
		var result = RunScopeCommand(repository, state.Path,
			["related", "Seed.cs", "--project", repository.Root, "--direction", "dependencies", "--format", "json",
			 "--git-mode", mode, "--profile", profile, "--exclude", "none", "--language", "en", "--plain"]);
		Assert.True(result.ExitCode == 0, result.StandardError);
		using var document = JsonDocument.Parse(result.StandardOutput);
		var outside = Assert.Single(document.RootElement.GetProperty("outsideSelection").EnumerateArray());
		Assert.Equal("Seed.cs", outside.GetProperty("seed").GetString());
		Assert.Contains(outside.GetProperty("dependencies").EnumerateArray(),
			static dependency => dependency.GetProperty("path").GetString() == "Target.cs");
		Assert.DoesNotContain("Hidden.cs", result.StandardOutput, StringComparison.Ordinal);
		Assert.Equal(!trackedBaseline && mode != "changes", outside.GetProperty("dependencies").EnumerateArray()
			.Any(static dependency => dependency.GetProperty("path").GetString() == "Untracked.cs"));
	}

	private static TerminalTestProcessResult RunScopeCommand(GitModeRepository repository, string state, string[] arguments)
		=> RunScopeCommand(repository, state, arguments, configureEnvironment: null);

	private static TerminalTestProcessResult RunScopeCommand(
		GitModeRepository repository, string state, string[] arguments, Action<ProcessStartInfo>? configureEnvironment)
	{
		var start = new ProcessStartInfo("dotnet")
		{
			WorkingDirectory = repository.Root,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		repository.SetProcessEnvironment(start);
		configureEnvironment?.Invoke(start);
		start.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = state;
		start.ArgumentList.Add(PublishedApplicationLocator.FindApplicationAssembly());
		foreach (var argument in arguments)
			start.ArgumentList.Add(argument);
		return TerminalTestProcess.Run(start);
	}

	[Theory]
	[InlineData("absolute-global")]
	[InlineData("empty-home")]
	[InlineData("xdg")]
	[InlineData("relative-global")]
	[InlineData("integer-global")]
	public void RealChangesCommandResolvesUserConfigurationWithoutInheritingTheMachinesFiles(string source)
	{
		using var repository = new GitModeRepository();
		using var state = new TemporaryDirectory();
		repository.Write("Untouched.cs", "class Untouched {}\n");
		repository.Write("Edited.cs", "class Edited {}\n");
		repository.Commit();
		repository.Write("Untouched.cs", "class Untouched {}\r\n");
		repository.Write("Edited.cs", "class Edited { int Value; }\r\n");
		File.SetLastWriteTimeUtc(Path.Combine(repository.Root, "Untouched.cs"), new DateTime(2001, 1, 2, 3, 4, 5, DateTimeKind.Utc));
		Assert.Equal("Edited.cs", repository.Git("-c", "core.autocrlf=true", "diff", "--name-only").Trim());
		repository.Git("config", "--unset", "core.autocrlf");
		repository.Git("config", "--unset", "core.eol");
		File.WriteAllText(repository.UserConfiguration, $"[core]\n autocrlf = {(source == "integer-global" ? "2" : "true")}\n");
		var xdg = state.CreateDirectory("xdg/git");
		File.WriteAllText(Path.Combine(xdg, "config"), "[core]\n autocrlf = true\n");
		var result = RunScopeCommand(repository, state.CreateDirectory("data"),
			["tree", repository.Root, "--git-mode", "changes", "--exclude", "none", "--format", "text"], start =>
			{
				if (source == "empty-home")
					start.Environment["HOME"] = string.Empty;
				if (source == "xdg")
				{
					start.Environment["GIT_CONFIG_GLOBAL"] = string.Empty;
					start.Environment["XDG_CONFIG_HOME"] = Path.GetDirectoryName(xdg)!;
				}
				if (source == "relative-global")
					start.Environment["GIT_CONFIG_GLOBAL"] = "relative.config";
			});
		Assert.True(result.ExitCode == 0, result.StandardError);
		Assert.Contains("Edited.cs", result.StandardOutput, StringComparison.Ordinal);
		Assert.Equal(source == "relative-global", result.StandardOutput.Contains("Untouched.cs", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("none", true, true)]
	[InlineData("gitignore", true, false)]
	[InlineData("tracked", false, false)]
	public void RealRelatedCommandNeverExpandsPersistentGitFilters(string mode, bool untrackedVisible, bool ignoredVisible)
	{
		using var repository = new GitModeRepository();
		using var state = new TemporaryDirectory();
		repository.Write("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
		repository.Write("Seed.cs", "public class Seed { Target A; Untracked B; Ignored C; }\n");
		repository.Write("Target.cs", "public class Target { }\n");
		repository.Write(".gitignore", "Ignored.cs\n");
		repository.Commit();
		repository.Write("Untracked.cs", "public class Untracked { }\n");
		repository.Write("Ignored.cs", "public class Ignored { }\n");
		var result = RunScopeCommand(repository, state.Path,
			["related", "Seed.cs", "--project", repository.Root, "--select", "Seed.cs", "--direction", "dependencies",
			 "--git-mode", mode, "--exclude", "none", "--format", "json", "--language", "en", "--plain"]);
		Assert.True(result.ExitCode == 0, result.StandardError);
		using var document = JsonDocument.Parse(result.StandardOutput);
		var outside = Assert.Single(document.RootElement.GetProperty("outsideSelection").EnumerateArray());
		var paths = outside.GetProperty("dependencies").EnumerateArray().Select(static item => item.GetProperty("path").GetString()).ToArray();
		Assert.Contains("Target.cs", paths);
		Assert.Equal(untrackedVisible, paths.Contains("Untracked.cs"));
		Assert.Equal(ignoredVisible, paths.Contains("Ignored.cs"));
		Assert.Equal(1 + (untrackedVisible ? 1 : 0) + (ignoredVisible ? 1 : 0), paths.Length);
	}
}
