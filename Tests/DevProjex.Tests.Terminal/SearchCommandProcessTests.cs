using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

public sealed class SearchCommandProcessTests
{
	private const int MaximumOutputCharacters = 16_000;

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
	public void SymbolTextOutputUsesACliCommandForReadingTheDeclarationFile()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);

		var result = Run(workspace, project, "Run", "--symbols", "--format", "text");

		Assert.Equal(0, result.ExitCode);
		Assert.DoesNotContain("get_file {", result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("devprojex export context", result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("--view content", result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("--select src/App.cs", result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("--git-mode none", result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("--exclude none", result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains(Path.GetFullPath(project), result.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public void DeclarationReadCommandQuotesProjectAndFilePathsWithSpaces()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project with spaces");
		workspace.WriteFile(
			"project with spaces/src/Application Service.cs",
			"namespace Demo; public sealed class Sample { public void Run() { } }");

		var result = Run(workspace, project, "Run", "--symbols", "--format", "text");

		Assert.Equal(0, result.ExitCode);
		var quote = OperatingSystem.IsWindows() ? '"' : '\'';
		Assert.Contains($"{quote}{Path.GetFullPath(project)}{quote}", result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains($"--select {quote}src/Application Service.cs{quote}", result.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public void DeclarationReadCommandPreservesTheResolvedSelectionParameters()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);

		var result = Run(
			workspace,
			project,
			"Run",
			"--symbols",
			"--format", "text",
			"--root", "src",
			"--extension", ".cs",
			"--hide-secrets");

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("--root src", result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("--extension .cs", result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("--hide-secrets", result.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public void DeclarationReadCommandUsesTheSafeRepositorySourceInsteadOfTheCheckoutPath()
	{
		var request = new SearchCommandRequest(
			ProjectPath: @"C:\cache\checkout",
			Pattern: "Run",
			Selection: ProjectSelectionSpec.Standard,
			Mode: SearchMode.Symbols,
			MaximumResults: 20,
			SearchBodyCharacters: 1_800,
			Format: SearchOutputFormat.Text,
			OutputPath: null,
			Output: new TerminalOutputOptions(),
			RepositorySourceUrl: "https://user:secret@example.com/owner/repository.git?token=private",
			RepositoryBranch: "feature/symbol-search");

		var source = SearchCommandHandler.ResolveDeclarationReadSource(request);
		var arguments = SearchCommandHandler.BuildDeclarationReadArguments(request, "src/App.cs");

		Assert.Equal("https://example.com/owner/repository.git", source);
		Assert.DoesNotContain("secret", source, StringComparison.Ordinal);
		Assert.DoesNotContain("token", source, StringComparison.Ordinal);
		Assert.Equal(source, arguments[3]);
		var branchIndex = Array.IndexOf(arguments.ToArray(), "--branch");
		Assert.InRange(branchIndex, 0, arguments.Count - 2);
		Assert.Equal("feature/symbol-search", arguments[branchIndex + 1]);
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

	[Theory]
	[InlineData("text")]
	[InlineData("markdown")]
	public void MatchWhoseCompleteLineDoesNotFitIsReportedAsOmitted(string format)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/long.txt", "needle " + new string('x', 17_000));

		var result = Run(
			workspace,
			project,
			"needle",
			"--search-body-chars", "off",
			"--format", format);

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("[Matches omitted]", result.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("[No matches]", result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("matches=1", result.StandardOutput, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("text")]
	[InlineData("markdown")]
	public void NoMatchesDistinguishesCompleteAndPartialInspection(string format)
	{
		using var workspace = new TemporaryDirectory();
		var completeProject = workspace.CreateDirectory("complete");
		workspace.WriteFile("complete/empty.txt", "ordinary text");
		var partialProject = workspace.CreateDirectory("partial");
		workspace.WriteFile("partial/a.txt", "ordinary text");
		var oversizedPath = workspace.WriteFile("partial/z.bin", string.Empty);
		using (var oversized = new FileStream(oversizedPath, FileMode.Open, FileAccess.Write, FileShare.None))
		{
			oversized.SetLength(65L * 1024 * 1024);
		}

		var complete = Run(workspace, completeProject, "needle", "--format", format);
		var partial = Run(workspace, partialProject, "needle", "--format", format);

		Assert.Equal(0, complete.ExitCode);
		Assert.Contains("[No matches]", complete.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("[Search partial]", complete.StandardOutput, StringComparison.Ordinal);
		Assert.Equal(0, partial.ExitCode);
		Assert.Contains("[Search partial]", partial.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("[No matches]", partial.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("inspection-bytes", partial.StandardOutput, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("text")]
	[InlineData("json")]
	[InlineData("markdown")]
	public void SerializedOutputHonorsTheFormatBudget(string format)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		for (var index = 0; index < 200; index++)
		{
			workspace.WriteFile(
				$"project/src/File{index:D3}.txt",
				$"needle value {index:D3} {new string('x', 100)}{Environment.NewLine}");
		}

		var result = Run(
			workspace,
			project,
			"needle",
			"--max", "200",
			"--search-body-chars", "off",
			"--format", format);

		Assert.Equal(0, result.ExitCode);
		Assert.True(
			result.StandardOutput.Length <= MaximumOutputCharacters,
			$"{format} output contained {result.StandardOutput.Length} characters.");
		if (format == "json")
		{
			using var document = JsonDocument.Parse(result.StandardOutput);
			var boundary = document.RootElement.GetProperty("searchBoundary");
			Assert.Equal(
				document.RootElement.GetProperty("matches").GetArrayLength(),
				boundary.GetProperty("writtenMatches").GetInt32());
			Assert.Contains(
				"response-characters",
				boundary.GetProperty("limits").EnumerateArray().Select(static value => value.GetString()));
		}
		else
		{
			Assert.Contains("response-characters", result.StandardOutput, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void BudgetedFormatsExposeTheSameNonEmptyMatchPrefix()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		for (var index = 0; index < 200; index++)
		{
			workspace.WriteFile(
				$"project/src/File{index:D3}.txt",
				$"needle value {index:D3} {new string('x', 100)}{Environment.NewLine}");
		}

		var text = Run(workspace, project, "needle", "--max", "200", "--search-body-chars", "off", "--format", "text");
		var markdown = Run(workspace, project, "needle", "--max", "200", "--search-body-chars", "off", "--format", "markdown");
		var json = Run(workspace, project, "needle", "--max", "200", "--search-body-chars", "off", "--format", "json");

		Assert.Equal(0, text.ExitCode);
		Assert.Equal(0, markdown.ExitCode);
		Assert.Equal(0, json.ExitCode);
		var textMatches = Regex.Matches(text.StandardOutput, @"(?m)^1:needle value").Count;
		var markdownMatches = Regex.Matches(markdown.StandardOutput, @"(?m)^1:needle value").Count;
		using var document = JsonDocument.Parse(json.StandardOutput);
		var jsonItems = document.RootElement.GetProperty("matches").EnumerateArray().ToArray();
		var jsonMatches = jsonItems.Length;
		Assert.True(jsonMatches > 0, json.StandardOutput);
		Assert.Equal(textMatches, markdownMatches);
		Assert.Equal(textMatches, jsonMatches);
		var pathPattern = @"(?m)^src/File\d{3}\.txt\r?$";
		var textPaths = Regex.Matches(text.StandardOutput, pathPattern)
			.Select(static match => match.Value.TrimEnd('\r'))
			.ToArray();
		var markdownPaths = Regex.Matches(markdown.StandardOutput, pathPattern)
			.Select(static match => match.Value.TrimEnd('\r'))
			.ToArray();
		var jsonPaths = jsonItems
			.Select(static match => match.GetProperty("path").GetString())
			.ToArray();
		Assert.Equal(textPaths, markdownPaths);
		Assert.Equal(textPaths, jsonPaths);
		var boundary = document.RootElement.GetProperty("searchBoundary");
		Assert.False(boundary.GetProperty("complete").GetBoolean());
		Assert.Equal(
			boundary.GetProperty("encounteredMatches").GetInt32() - jsonMatches,
			boundary.GetProperty("omittedMatches").GetInt32());
	}

	[Fact]
	public void UnicodePatternKeepsJsonWithinThePublishedCharacterBudget()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/content.txt", "ordinary text");
		var pattern = string.Concat(Enumerable.Repeat("Ж", 4_096));

		var result = Run(workspace, project, pattern, "--format", "json");

		Assert.Equal(0, result.ExitCode);
		Assert.True(
			result.StandardOutput.Length <= MaximumOutputCharacters,
			$"JSON output contained {result.StandardOutput.Length} characters.");
		using var document = JsonDocument.Parse(result.StandardOutput);
		Assert.Equal(pattern, document.RootElement.GetProperty("query").GetProperty("pattern").GetString());
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
