using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

/// <summary>
/// Runs the published CLI binary to check the command-line input contract end to end:
/// exit codes, empty stdout on failure, and the error code on stderr.
/// </summary>
public sealed partial class CliInputContractProcessTests
{
	[Fact]
	public void AtPrefixedSearchPatternIsLiteralEvenWhenAMatchingFileExists()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);
		workspace.WriteFile("project/src/Widget.java", "class Widget {\n    @Override\n    public String toString() { return \"needle\"; }\n}\n");
		workspace.WriteFile("rsp.txt", "needle\n");

		var annotation = RunCli(workspace, "search", "@Override", project, "--format", "json", "--git-mode", "none", "--exclude", "none");
		var fileName = RunCli(workspace, "search", "@rsp.txt", project, "--format", "json", "--git-mode", "none", "--exclude", "none");

		Assert.True(annotation.ExitCode == 0, annotation.StandardError);
		var match = Assert.Single(ReadMatches(annotation.StandardOutput));
		Assert.Equal("src/Widget.java", match.GetProperty("path").GetString());
		Assert.Equal(2, match.GetProperty("line").GetInt32());
		Assert.True(fileName.ExitCode == 0, fileName.StandardError);
		Assert.Empty(ReadMatches(fileName.StandardOutput));
	}

	[Fact]
	public void AtPrefixedSelectedPathNamesAScopedPackageFolder()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);
		workspace.WriteFile("project/@scope/pkg/index.js", "export const value = 1;\n");
		workspace.WriteFile("project/other/index.js", "export const other = 2;\n");

		var result = RunCli(workspace, "tree", project, "-s", "@scope/pkg", "--format", "json", "--git-mode", "none", "--exclude", "none");

		Assert.True(result.ExitCode == 0, result.StandardError);
		Assert.Contains("\"@scope\"", result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("\"pkg\"", result.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("\"other\"", result.StandardOutput, StringComparison.Ordinal);
	}

	private static string CreateProject(TemporaryDirectory workspace)
	{
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/App.cs", "namespace Demo;\npublic sealed class App { }\n");
		return project;
	}

	private static JsonElement[] ReadMatches(string json)
	{
		using var document = JsonDocument.Parse(json);
		return document.RootElement
			.GetProperty("matches")
			.EnumerateArray()
			.Select(static match => match.Clone())
			.ToArray();
	}

	private static void AssertUsageFailure(
		TerminalTestProcessResult result,
		string expectedCode,
		string expectedText)
	{
		Assert.True(
			result.ExitCode == CommandLineExitCodes.UsageError,
			$"exit {result.ExitCode}: {result.StandardError}");
		Assert.Empty(result.StandardOutput);
		Assert.Contains($"[{expectedCode}]", result.StandardError, StringComparison.Ordinal);
		Assert.Contains(expectedText, result.StandardError, StringComparison.Ordinal);
	}

	private static TerminalTestProcessResult RunCli(
		TemporaryDirectory workspace,
		params string[] arguments)
	{
		var start = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			WorkingDirectory = workspace.Path
		};
		start.ArgumentList.Add(PublishedApplicationLocator.FindApplicationAssembly());
		foreach (var argument in arguments)
			start.ArgumentList.Add(argument);
		foreach (var argument in new[] { "--language", "en", "--progress", "never" })
			start.ArgumentList.Add(argument);
		start.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = workspace.CreateDirectory("data");
		return TerminalTestProcess.Run(start, TimeSpan.FromMinutes(1));
	}
}
