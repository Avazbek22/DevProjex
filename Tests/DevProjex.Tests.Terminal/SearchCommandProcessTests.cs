using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

public sealed class SearchCommandProcessTests
{
	[Fact]
	public void TextJsonAndMarkdownDescribeTheSameMatches()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);

		var text = Run(workspace, project, "needle", "--format", "text");
		var json = Run(workspace, project, "needle", "--format", "json");
		var markdown = Run(workspace, project, "needle", "--format", "markdown");

		Assert.True(text.ExitCode == 0, text.StandardError + text.StandardOutput);
		Assert.True(json.ExitCode == 0, json.StandardError + json.StandardOutput);
		Assert.True(markdown.ExitCode == 0, markdown.StandardError + markdown.StandardOutput);
		Assert.Contains("src/App.cs", text.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("[Search boundary] complete", text.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("[Resolution] resolved=1", text.StandardOutput, StringComparison.Ordinal);
		using var document = JsonDocument.Parse(json.StandardOutput);
		Assert.Equal("devprojex-search-results", document.RootElement.GetProperty("kind").GetString());
		var match = Assert.Single(document.RootElement.GetProperty("matches").EnumerateArray());
		Assert.Equal("src/App.cs", match.GetProperty("path").GetString());
		Assert.Equal(5, match.GetProperty("line").GetInt32());
		Assert.Equal("Demo.Sample.Run", match.GetProperty("declaration").GetString());
		Assert.True(document.RootElement.GetProperty("searchBoundary").GetProperty("complete").GetBoolean());
		Assert.StartsWith("# Search results", markdown.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("src/App.cs", markdown.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public void TextRegexAndSymbolModesRemainDistinct()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);

		var literal = Run(workspace, project, "needle.*value", "--format", "json");
		var regex = Run(workspace, project, "needle.*value", "--regex", "--format", "json");
		var symbol = Run(workspace, project, "Run", "--symbols", "--format", "json");

		Assert.Equal(0, literal.ExitCode);
		Assert.Equal(0, regex.ExitCode);
		Assert.Equal(0, symbol.ExitCode);
		Assert.Empty(JsonDocument.Parse(literal.StandardOutput).RootElement.GetProperty("matches").EnumerateArray());
		Assert.Single(JsonDocument.Parse(regex.StandardOutput).RootElement.GetProperty("matches").EnumerateArray());
		Assert.Single(JsonDocument.Parse(symbol.StandardOutput).RootElement.GetProperty("matches").EnumerateArray());
	}

	[Fact]
	public void InvalidRegularExpressionUsesTheSearchErrorCode()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);

		var result = Run(workspace, project, "(", "--regex");

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Contains("DPX-CLI-SEARCH-PATTERN", result.StandardError, StringComparison.Ordinal);
		Assert.Empty(result.StandardOutput);
	}

	[Fact]
	public void MaximumResultAndDisabledBodyRemainExplicitInJson()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);
		workspace.WriteFile("project/src/Second.cs", "public sealed class Second { string Read() => \"needle value\"; }\n");

		var result = Run(
			workspace,
			project,
			"needle",
			"--max", "1",
			"--search-body-chars", "off",
			"--format", "json");

		Assert.Equal(0, result.ExitCode);
		using var document = JsonDocument.Parse(result.StandardOutput);
		Assert.Single(document.RootElement.GetProperty("matches").EnumerateArray());
		Assert.False(document.RootElement.GetProperty("searchBoundary").GetProperty("complete").GetBoolean());
		Assert.Contains(
			"max-results",
			document.RootElement.GetProperty("searchBoundary").GetProperty("limits")
				.EnumerateArray().Select(value => value.GetString()));
		Assert.All(
			document.RootElement.GetProperty("declarations").EnumerateArray(),
			declaration => Assert.Equal(JsonValueKind.Null, declaration.GetProperty("body").ValueKind));
	}

	[Fact]
	public void OutputWritesOneAtomicDocumentOutsideTheProject()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);
		var output = Path.Combine(workspace.Path, "search.json");

		var result = Run(workspace, project, "needle", "--format", "json", "--output", output);

		Assert.Equal(0, result.ExitCode);
		Assert.Equal(Path.GetFullPath(output), result.StandardOutput.Trim());
		using var document = JsonDocument.Parse(File.ReadAllText(output));
		Assert.Equal("devprojex-search-results", document.RootElement.GetProperty("kind").GetString());
	}

	[Fact]
	public void HideSecretsSearchesAndReturnsOnlyTheRedactedText()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		const string token = "ghp_" + "a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL";
		workspace.WriteFile("project/config.txt", $"token={token} marker\n");

		var result = Run(workspace, project, "marker", "--hide-secrets", "--format", "text");

		Assert.Equal(0, result.ExitCode);
		Assert.DoesNotContain(token, result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[", result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("1:", result.StandardOutput, StringComparison.Ordinal);
	}

	private static string CreateProject(TemporaryDirectory workspace)
	{
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/App.cs", """
			namespace Demo;
			public sealed class Sample
			{
			    public string Run() =>
			        "needle value";
			    public string Call() => Run();
			}
			""");
		return project;
	}

	private static TerminalTestProcessResult Run(
		TemporaryDirectory workspace,
		string project,
		string pattern,
		params string[] extra)
	{
		var start = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		start.ArgumentList.Add(PublishedApplicationLocator.FindApplicationAssembly());
		foreach (var argument in new[]
				 {
					 "search", pattern, project, "--git-mode", "none", "--exclude", "none",
					 "--language", "en", "--plain", "--progress", "never"
				 }.Concat(extra))
		{
			start.ArgumentList.Add(argument);
		}
		start.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = workspace.CreateDirectory("data");
		return TerminalTestProcess.Run(start, TimeSpan.FromMinutes(1));
	}
}
