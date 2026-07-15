using DevProjex.Avalonia;
using System.Xml.Linq;

namespace DevProjex.Tests.Integration;

[Trait("Category", "TerminalCommand")]
public sealed class CommandLineStdoutContractIntegrationTests
{
	[Theory]
	[InlineData("ascii")]
	[InlineData("json")]
	[InlineData("xml")]
	[InlineData("md")]
	public async Task Process_TreeStdoutMatrix_WritesOnlyTreePayloadForEveryFormat(string format)
	{
		using var temp = new TemporaryDirectory();
		var projectPath = SeedTerminalWorkspace(temp);

		var result = await RunAppAsync(
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Format, format,
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Extensions, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		AssertNoCommandNoise(result.Stdout);
		AssertTreePayload(result.Stdout, format, projectPath, ["src/App.cs", "src/Utils/Helper.cs"]);
		Assert.DoesNotContain("public sealed class App", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("Guide.md", result.Stdout, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("ascii")]
	[InlineData("json")]
	[InlineData("xml")]
	[InlineData("md")]
	public async Task Process_TreeContentStdoutMatrix_WritesTreeThenPlainTextWithRelativeHeaders(string format)
	{
		using var temp = new TemporaryDirectory();
		var projectPath = SeedTerminalWorkspace(temp);

		var result = await RunAppAsync(
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Export, "tree-content",
			CommandLineOptionTokens.Format, format,
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Extensions, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		AssertNoCommandNoise(result.Stdout);

		var (treePart, contentPart) = SplitTreeContent(format, result.Stdout);
		AssertTreePayload(treePart, format, projectPath, ["src/App.cs", "src/Utils/Helper.cs"]);
		Assert.DoesNotContain("public sealed class App", treePart, StringComparison.Ordinal);
		Assert.Contains("src/App.cs:", contentPart, StringComparison.Ordinal);
		Assert.Contains("src/Utils/Helper.cs:", contentPart, StringComparison.Ordinal);
		Assert.Contains("public sealed class App", contentPart, StringComparison.Ordinal);
		Assert.Contains("public static class Helper", contentPart, StringComparison.Ordinal);
		Assert.DoesNotContain(Path.GetFullPath(projectPath).Replace('\\', '/'), contentPart.Replace('\\', '/'), StringComparison.Ordinal);
		Assert.DoesNotContain("src\\App.cs:", contentPart, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("report-stdout", "json")]
	[InlineData("silent-implicit-report", "json")]
	[InlineData("content-stdout", "text")]
	[InlineData("tree-file-output", "path")]
	public async Task Process_CommonTerminalCommands_KeepStdoutAndStderrContracts(string scenario, string expectedPayloadKind)
	{
		using var temp = new TemporaryDirectory();
		var projectPath = SeedTerminalWorkspace(temp);
		var outputPath = Path.Combine(temp.Path, "exports", "tree-output.xml");

		var result = await RunAppAsync(BuildScenarioArgs(scenario, projectPath, outputPath));

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		AssertNoCommandNoise(result.Stdout);

		switch (expectedPayloadKind)
		{
			case "json":
				using (JsonDocument.Parse(result.Stdout))
				{
				}

				Assert.StartsWith("{", result.Stdout.TrimStart(), StringComparison.Ordinal);
				Assert.EndsWith("}", result.Stdout.TrimEnd(), StringComparison.Ordinal);
				break;
			case "text":
				Assert.Contains($"{Path.Combine(projectPath, "src", "App.cs")}:", result.Stdout, StringComparison.Ordinal);
				Assert.Contains($"{Path.Combine(projectPath, "src", "Utils", "Helper.cs")}:", result.Stdout, StringComparison.Ordinal);
				Assert.Contains("public sealed class App", result.Stdout, StringComparison.Ordinal);
				Assert.DoesNotContain("Root: ", result.Stdout, StringComparison.Ordinal);
				Assert.DoesNotContain("<t ", result.Stdout, StringComparison.Ordinal);
				break;
			case "path":
				Assert.Equal(Path.GetFullPath(outputPath), AssertSingleOutputLine(result.Stdout));
				Assert.True(File.Exists(outputPath));
				AssertXmlTree(File.ReadAllText(outputPath), projectPath, ["src/App.cs", "src/Utils/Helper.cs"]);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(expectedPayloadKind), expectedPayloadKind, "Unexpected stdout payload kind.");
		}
	}

	[Fact]
	public async Task Process_BenchmarkCommand_WritesSummaryStdoutAndDetailedJsonReport()
	{
		using var runCountOverride = TemporaryEnvironmentVariable.Set("DEVPROJEX_BENCHMARK_RUNS", "1");
		using var warmupOverride = TemporaryEnvironmentVariable.Set("DEVPROJEX_BENCHMARK_WARMUP", "0");
		using var temp = new TemporaryDirectory();
		var projectPath = SeedTerminalWorkspace(temp);
		var outputPath = Path.Combine(temp.Path, "benchmark result", "result.json");

		var result = await RunAppAsync(
			CommandLineOptionTokens.Benchmark, projectPath,
			CommandLineOptionTokens.BenchmarkOutput, outputPath);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		Assert.Contains("DevProjex benchmark", result.Stdout, StringComparison.Ordinal);
		Assert.Contains($"Target: {Path.GetFullPath(projectPath).Replace('\\', '/')}", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("Cold process:", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("Warm pipeline:", result.Stdout, StringComparison.Ordinal);
		Assert.Contains(Path.GetFullPath(outputPath).Replace('\\', '/'), result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("\"schemaVersion\"", result.Stdout, StringComparison.Ordinal);
		Assert.True(File.Exists(outputPath));

		using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
		var root = document.RootElement;
		Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
		Assert.Equal(Path.GetFullPath(projectPath).Replace('\\', '/'), root.GetProperty("targetPath").GetString());
		Assert.Equal(Path.GetFullPath(outputPath).Replace('\\', '/'), root.GetProperty("outputPath").GetString());
		Assert.False(root.GetProperty("hasFailures").GetBoolean());
		Assert.Equal(1, root.GetProperty("configuration").GetProperty("runs").GetInt32());
		Assert.Equal(0, root.GetProperty("configuration").GetProperty("warmup").GetInt32());
		var executable = root.GetProperty("executable");
		Assert.Contains(CommandLineOptionTokens.NoUi, ReadStringArray(executable.GetProperty("arguments")));
		Assert.Contains(CommandLineOptionTokens.Report, ReadStringArray(executable.GetProperty("arguments")));
		Assert.Contains(CommandLineOptionTokens.StandardOutputReportPath, ReadStringArray(executable.GetProperty("arguments")));
		Assert.DoesNotContain(CommandLineOptionTokens.Benchmark, ReadStringArray(executable.GetProperty("arguments")));
		var coldRun = Assert.Single(root.GetProperty("coldProcess").GetProperty("runs").EnumerateArray());
		var warmRun = Assert.Single(root.GetProperty("warmPipeline").GetProperty("runs").EnumerateArray());
		Assert.Equal(CommandLineExitCodes.Success, coldRun.GetProperty("exitCode").GetInt32());
		Assert.Equal(CommandLineExitCodes.Success, warmRun.GetProperty("exitCode").GetInt32());
		Assert.True(coldRun.GetProperty("stdoutBytes").GetInt32() > 0);
		Assert.True(warmRun.GetProperty("stdoutBytes").GetInt32() > 0);
		Assert.True(warmRun.GetProperty("allocatedBytes").GetInt64() > 0);
		Assert.Equal(64, root.GetProperty("workload").GetProperty("fingerprint").GetString()!.Length);
		Assert.Equal(64, executable.GetProperty("assemblySha256").GetString()!.Length);
		Assert.True(root.GetProperty("metricsConsistent").GetBoolean());
		Assert.Equal(0, root.GetProperty("coldProcess").GetProperty("warmupRuns").GetArrayLength());
		Assert.Equal(0, root.GetProperty("warmPipeline").GetProperty("warmupRuns").GetArrayLength());
	}

	[Theory]
	[InlineData("unknown-option", "Unknown option '--unknown'.")]
	[InlineData("detached-format", "--output and --export-format require --export.")]
	[InlineData("content-structured-format", "--export-format applies only to tree")]
	[InlineData("report-and-export-stdout", "Cannot combine --report - with --export.")]
	[InlineData("same-report-and-export-file", "--report-path and --output must point to different files.")]
	[InlineData("benchmark-output-without-benchmark", "--benchmark-output requires --benchmark or --benchmark-ui.")]
	[InlineData("benchmark-with-path", "Use --benchmark <folder> without --path or a positional folder.")]
	[InlineData("benchmark-with-report", "--benchmark runs the standard project report benchmark")]
	[InlineData("benchmark-ui-with-path", "Use --benchmark-ui <folder> without --path or a positional folder.")]
	[InlineData("benchmark-ui-with-report", "--benchmark-ui runs the standard desktop UI benchmark")]
	[InlineData("benchmark-ui-with-benchmark", "--benchmark-ui runs the standard desktop UI benchmark")]
	[InlineData("ui-benchmark-script-without-session-metrics", "--ui-benchmark-script is an internal option and requires --session-metrics.")]
	[InlineData("session-metrics-output-without-session-metrics", "--session-metrics-output requires --session-metrics.")]
	[InlineData("session-metrics-with-path", "Use --session-metrics <folder> without --path or a positional folder.")]
	[InlineData("session-metrics-with-no-ui", "--session-metrics opens the desktop app")]
	[InlineData("session-metrics-stdout-output", "--session-metrics-output must point to a JSON file path")]
	public async Task Process_UsageErrors_WriteOnlyStderrAndNeverCreateOutputFiles(string scenario, string expectedError)
	{
		using var temp = new TemporaryDirectory();
		var projectPath = SeedTerminalWorkspace(temp);
		var sharedPath = Path.Combine(temp.Path, "exports", "same-output.json");

		var result = await RunAppAsync(BuildErrorScenarioArgs(scenario, projectPath, sharedPath));

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.StartsWith("DevProjex: ", result.Stderr, StringComparison.Ordinal);
		Assert.Contains(expectedError, result.Stderr, StringComparison.Ordinal);
		Assert.False(File.Exists(sharedPath));
	}

	[Theory]
	[InlineData("no-ui", CommandLineOptionTokens.NoUi)]
	[InlineData("no-uii", CommandLineOptionTokens.NoUi)]
	[InlineData("-no-ui", CommandLineOptionTokens.NoUi)]
	[InlineData("/no-ui", CommandLineOptionTokens.NoUi)]
	[InlineData("silent", CommandLineOptionTokens.Silent)]
	[InlineData("help", CommandLineOptionTokens.Help)]
	[InlineData("version", CommandLineOptionTokens.Version)]
	[InlineData("benchmark", CommandLineOptionTokens.Benchmark)]
	[InlineData("benchmark-ui", CommandLineOptionTokens.BenchmarkUi)]
	[InlineData("session-metrics", CommandLineOptionTokens.SessionMetrics)]
	[InlineData("export", CommandLineOptionTokens.Export)]
	[InlineData("report", CommandLineOptionTokens.Report)]
	[InlineData("preview-search", CommandLineOptionTokens.PreviewSearch)]
	public async Task Process_KnownOptionNameWithoutDashWritesTerminalUsageError(string value, string expectedSuggestion)
	{
		var result = await RunAppAsync(value);

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.StartsWith("DevProjex: ", result.Stderr, StringComparison.Ordinal);
		Assert.Contains($"Did you mean '{expectedSuggestion}'?", result.Stderr, StringComparison.Ordinal);
		Assert.Contains($"Use --path {value}", result.Stderr, StringComparison.Ordinal);
		Assert.DoesNotContain("Папка не найдена", result.Stderr, StringComparison.Ordinal);
		Assert.DoesNotContain("Folder not found", result.Stderr, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("--no-uii", CommandLineOptionTokens.NoUi)]
	[InlineData("--no_ui", CommandLineOptionTokens.NoUi)]
	[InlineData("--noui", CommandLineOptionTokens.NoUi)]
	[InlineData("--silet", CommandLineOptionTokens.Silent)]
	[InlineData("--preview-serch", CommandLineOptionTokens.PreviewSearch)]
	[InlineData("--tree-fomat", CommandLineOptionTokens.TreeFormat)]
	[InlineData("--benchmak", CommandLineOptionTokens.Benchmark)]
	[InlineData("--benchmak-ui", CommandLineOptionTokens.BenchmarkUi)]
	[InlineData("--session-metric", CommandLineOptionTokens.SessionMetrics)]
	[InlineData("/preview-serch", CommandLineOptionTokens.PreviewSearch)]
	public async Task Process_OptionTyposWriteUsageErrorWithSuggestion(string value, string expectedSuggestion)
	{
		var result = await RunAppAsync(value);

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.StartsWith("DevProjex: ", result.Stderr, StringComparison.Ordinal);
		Assert.Contains($"Did you mean '{expectedSuggestion}'?", result.Stderr, StringComparison.Ordinal);
		Assert.DoesNotContain("Usage:", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("Folder not found", result.Stderr, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("--no-ui=true")]
	[InlineData("--silent=false")]
	[InlineData("--help=true")]
	[InlineData("-h=true")]
	[InlineData("--version=true")]
	[InlineData("--preview=true")]
	public async Task Process_ValueLessFlagWithInlineValueWritesUsageError(string value)
	{
		var result = await RunAppAsync(value);

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.StartsWith("DevProjex: ", result.Stderr, StringComparison.Ordinal);
		Assert.Contains("does not accept a value", result.Stderr, StringComparison.Ordinal);
		Assert.DoesNotContain("Usage:", result.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_CommandStyleExportWritesUsageErrorInsteadOfOpeningUi()
	{
		var result = await RunAppAsync("export", "tree");

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.StartsWith("DevProjex: ", result.Stderr, StringComparison.Ordinal);
		Assert.Contains("Did you mean '--export'?", result.Stderr, StringComparison.Ordinal);
		Assert.DoesNotContain("Folder not found", result.Stderr, StringComparison.Ordinal);
	}

	private static string SeedTerminalWorkspace(TemporaryDirectory temp)
	{
		var projectPath = temp.CreateDirectory("terminal project with spaces");
		WriteFile(projectPath, Path.Combine("src", "App.cs"), "namespace TerminalSmoke;\npublic sealed class App {}\n");
		WriteFile(projectPath, Path.Combine("src", "Utils", "Helper.cs"), "namespace TerminalSmoke;\npublic static class Helper {}\n");
		WriteFile(projectPath, Path.Combine("docs", "Guide.md"), "# Guide\n");
		WriteFile(projectPath, "README.md", "# Readme\n");
		Directory.CreateDirectory(Path.Combine(projectPath, "empty-folder"));
		return projectPath;
	}

	private static string[] BuildScenarioArgs(string scenario, string projectPath, string outputPath) =>
		scenario switch
		{
			"report-stdout" =>
			[
				CommandLineOptionTokens.NoUi,
				CommandLineOptionTokens.Path, projectPath,
				CommandLineOptionTokens.Report, CommandLineOptionTokens.StandardOutputReportPath,
				CommandLineOptionTokens.Roots, "src",
				CommandLineOptionTokens.Extensions, "cs",
				CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
			],
			"silent-implicit-report" =>
			[
				CommandLineOptionTokens.Silent,
				projectPath,
				CommandLineOptionTokens.Roots, "src",
				CommandLineOptionTokens.Extensions, "cs",
				CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
			],
			"content-stdout" =>
			[
				CommandLineOptionTokens.Path, projectPath,
				CommandLineOptionTokens.Export, "content",
				CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath,
				CommandLineOptionTokens.Roots, "src",
				CommandLineOptionTokens.Extensions, "cs",
				CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
			],
			"tree-file-output" =>
			[
				CommandLineOptionTokens.Path, projectPath,
				CommandLineOptionTokens.Export, "tree",
				CommandLineOptionTokens.Format, "xml",
				CommandLineOptionTokens.Output, outputPath,
				CommandLineOptionTokens.Roots, "src",
				CommandLineOptionTokens.Extensions, "cs",
				CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
			],
			_ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown stdout contract scenario.")
		};

	private static string[] BuildErrorScenarioArgs(string scenario, string projectPath, string sharedPath) =>
		scenario switch
		{
			"unknown-option" => ["--unknown", projectPath],
			"detached-format" => [projectPath, CommandLineOptionTokens.Format, "json"],
			"content-structured-format" =>
			[
				projectPath,
				CommandLineOptionTokens.Export, "content",
				CommandLineOptionTokens.Format, "xml"
			],
			"report-and-export-stdout" =>
			[
				CommandLineOptionTokens.NoUi,
				CommandLineOptionTokens.Path, projectPath,
				CommandLineOptionTokens.Report, CommandLineOptionTokens.StandardOutputReportPath,
				CommandLineOptionTokens.Export, "tree",
				CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath
			],
			"same-report-and-export-file" =>
			[
				CommandLineOptionTokens.NoUi,
				CommandLineOptionTokens.Path, projectPath,
				CommandLineOptionTokens.ReportPath, sharedPath,
				CommandLineOptionTokens.Export, "tree",
				CommandLineOptionTokens.Output, sharedPath
			],
			"benchmark-output-without-benchmark" =>
			[
				CommandLineOptionTokens.BenchmarkOutput, sharedPath
			],
			"benchmark-with-path" =>
			[
				CommandLineOptionTokens.Benchmark, projectPath,
				CommandLineOptionTokens.Path, projectPath
			],
			"benchmark-with-report" =>
			[
				CommandLineOptionTokens.Benchmark, projectPath,
				CommandLineOptionTokens.Report
			],
			"benchmark-ui-with-path" =>
			[
				CommandLineOptionTokens.BenchmarkUi, projectPath,
				CommandLineOptionTokens.Path, projectPath
			],
			"benchmark-ui-with-report" =>
			[
				CommandLineOptionTokens.BenchmarkUi, projectPath,
				CommandLineOptionTokens.Report
			],
			"benchmark-ui-with-benchmark" =>
			[
				CommandLineOptionTokens.BenchmarkUi, projectPath,
				CommandLineOptionTokens.Benchmark, projectPath
			],
			"ui-benchmark-script-without-session-metrics" =>
			[
				CommandLineOptionTokens.UiBenchmarkScript, "standard"
			],
			"session-metrics-output-without-session-metrics" =>
			[
				CommandLineOptionTokens.SessionMetricsOutput, sharedPath
			],
			"session-metrics-with-path" =>
			[
				CommandLineOptionTokens.SessionMetrics, projectPath,
				CommandLineOptionTokens.Path, projectPath
			],
			"session-metrics-with-no-ui" =>
			[
				CommandLineOptionTokens.SessionMetrics, projectPath,
				CommandLineOptionTokens.NoUi
			],
			"session-metrics-stdout-output" =>
			[
				CommandLineOptionTokens.SessionMetrics, projectPath,
				CommandLineOptionTokens.SessionMetricsOutput, CommandLineOptionTokens.StandardOutputReportPath
			],
			_ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown stderr contract scenario.")
		};

	private static void AssertTreePayload(
		string stdout,
		string format,
		string projectPath,
		IReadOnlyList<string> expectedRelativePaths)
	{
		switch (format)
		{
			case "ascii":
				Assert.StartsWith($"{Path.GetFullPath(projectPath)}:", stdout, StringComparison.Ordinal);
				foreach (var path in expectedRelativePaths)
					Assert.Contains(Path.GetFileName(path), stdout, StringComparison.Ordinal);
				break;
			case "json":
				AssertJsonTree(stdout, projectPath, expectedRelativePaths);
				break;
			case "xml":
				AssertXmlTree(stdout, projectPath, expectedRelativePaths);
				break;
			case "md":
				AssertMarkdownTree(stdout, projectPath, expectedRelativePaths);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported terminal tree format.");
		}
	}

	private static void AssertJsonTree(string stdout, string projectPath, IReadOnlyList<string> expectedRelativePaths)
	{
		using var document = JsonDocument.Parse(stdout);
		Assert.Equal(Path.GetFullPath(projectPath).Replace('\\', '/'), document.RootElement.GetProperty("rootPath").GetString());
		JsonTreeExportTestHelper.AssertOnlyRootPathAndTree(document.RootElement);
		JsonTreeExportTestHelper.AssertNoLegacyTreeContract(document.RootElement);
		Assert.Equal(
			expectedRelativePaths.OrderBy(static path => path, StringComparer.Ordinal).ToArray(),
			JsonTreeExportTestHelper.ExtractFilePaths(JsonTreeExportTestHelper.GetTree(document))
				.OrderBy(static path => path, StringComparer.Ordinal)
				.ToArray());
	}

	private static void AssertXmlTree(string stdout, string projectPath, IReadOnlyList<string> expectedRelativePaths)
	{
		Assert.StartsWith("<t ", stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("<?xml", stdout, StringComparison.Ordinal);
		var document = XDocument.Parse(stdout);
		Assert.Equal(Path.GetFullPath(projectPath).Replace('\\', '/'), document.Root?.Attribute("r")?.Value);
		Assert.Equal(
			expectedRelativePaths.OrderBy(static path => path, StringComparer.Ordinal).ToArray(),
			ExtractXmlFilePaths(document).OrderBy(static path => path, StringComparer.Ordinal).ToArray());
	}

	private static void AssertMarkdownTree(string stdout, string projectPath, IReadOnlyList<string> expectedRelativePaths)
	{
		Assert.StartsWith($"Root: {Path.GetFullPath(projectPath).Replace('\\', '/')}", stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("\t", stdout, StringComparison.Ordinal);
		Assert.All(
			stdout.Split('\n').Skip(2).Select(static line => line.TrimEnd('\r')),
			static line => Assert.False(line.EndsWith(' '), $"Markdown line has trailing spaces: '{line}'."));
		foreach (var path in expectedRelativePaths)
			Assert.Contains($"- {Path.GetFileName(path)}", stdout, StringComparison.Ordinal);
	}

	private static (string TreePart, string ContentPart) SplitTreeContent(string format, string stdout) =>
		format switch
		{
			"json" => SplitJsonTreeContent(stdout),
			"xml" => SplitXmlTreeContent(stdout),
			"md" => SplitTextTreeContent(stdout, startsWithRootHeader: true),
			"ascii" => SplitTextTreeContent(stdout, startsWithRootHeader: false),
			_ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported terminal tree-content format.")
		};

	private static (string TreePart, string ContentPart) SplitJsonTreeContent(string stdout)
	{
		var end = FindTopLevelJsonObjectEnd(stdout);
		Assert.True(end > 0, "Expected tree-content JSON stdout to start with a complete JSON object.");
		return (stdout[..end], stdout[end..]);
	}

	private static (string TreePart, string ContentPart) SplitXmlTreeContent(string stdout)
	{
		var end = stdout.IndexOf("</t>", StringComparison.Ordinal);
		Assert.True(end > 0, "Expected tree-content XML stdout to contain a complete <t> block.");
		var treeEnd = end + "</t>".Length;
		return (stdout[..treeEnd], TrimLeadingSeparator(stdout[treeEnd..]));
	}

	private static (string TreePart, string ContentPart) SplitTextTreeContent(string stdout, bool startsWithRootHeader)
	{
		var normalized = stdout.Replace("\r\n", "\n", StringComparison.Ordinal);
		var start = startsWithRootHeader
			? normalized.IndexOf("\n\n", StringComparison.Ordinal) + 2
			: normalized.IndexOf('\n') + 1;
		var contentStart = FindFirstContentHeader(normalized, Math.Max(0, start));
		Assert.True(contentStart > 0, "Expected tree-content stdout to contain a relative file content header.");
		return (
			TrimTrailingSeparator(normalized[..contentStart]),
			TrimLeadingSeparator(normalized[contentStart..]));
	}

	private static int FindTopLevelJsonObjectEnd(string text)
	{
		var depth = 0;
		var inString = false;
		var escaped = false;
		for (var i = 0; i < text.Length; i++)
		{
			var c = text[i];
			if (inString)
			{
				if (escaped)
				{
					escaped = false;
					continue;
				}

				if (c == '\\')
				{
					escaped = true;
					continue;
				}

				if (c == '"')
					inString = false;

				continue;
			}

			if (c == '"')
			{
				inString = true;
				continue;
			}

			if (c == '{')
				depth++;
			else if (c == '}')
			{
				depth--;
				if (depth == 0)
					return i + 1;
			}
		}

		return -1;
	}

	private static int FindFirstContentHeader(string text, int startIndex)
	{
		var lineStart = startIndex;
		while (lineStart < text.Length)
		{
			var lineEnd = text.IndexOf('\n', lineStart);
			if (lineEnd < 0)
				lineEnd = text.Length;

			var line = text[lineStart..lineEnd].TrimEnd('\r');
			var trimmed = line.TrimStart(' ');
			if (trimmed.Length > 0 &&
			    !trimmed.StartsWith("- ", StringComparison.Ordinal) &&
			    !line.StartsWith("Root: ", StringComparison.Ordinal) &&
			    line.EndsWith(':'))
			{
				return lineStart;
			}

			lineStart = lineEnd + 1;
		}

		return -1;
	}

	private static string TrimLeadingSeparator(string value)
	{
		var start = 0;
		while (start < value.Length)
		{
			var lineEnd = value.IndexOf('\n', start);
			var nextStart = lineEnd < 0 ? value.Length : lineEnd + 1;
			var line = value[start..(lineEnd < 0 ? value.Length : lineEnd)].Trim();
			if (line.Length > 0 && line.Any(static c => c != '\u00A0' && c != '?'))
				break;

			start = nextStart;
		}

		return value[start..];
	}

	private static string TrimTrailingSeparator(string value)
	{
		var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
		while (lines.Count > 0)
		{
			var line = lines[^1].Trim();
			if (line.Length > 0 && line.Any(static c => c != '\u00A0' && c != '?'))
				break;

			lines.RemoveAt(lines.Count - 1);
		}

		return string.Join('\n', lines).TrimEnd('\r', '\n');
	}

	private static string[] ExtractXmlFilePaths(XDocument document)
	{
		var paths = new List<string>();
		CollectXmlFiles(document.Root!, prefix: string.Empty, paths);
		return paths.ToArray();
	}

	private static void CollectXmlFiles(XElement element, string prefix, List<string> paths)
	{
		foreach (var child in element.Elements())
		{
			if (child.Name.LocalName == "f")
			{
				paths.Add(prefix + child.Value);
				continue;
			}

			if (child.Name.LocalName == "d")
			{
				var name = child.Attribute("n")?.Value ?? string.Empty;
				CollectXmlFiles(child, prefix + name + "/", paths);
			}
		}
	}

	private static void AssertNoCommandNoise(string stdout)
	{
		Assert.DoesNotContain("Usage:", stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("DevProjex:", stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("Building output", stdout, StringComparison.Ordinal);
	}

	private static string AssertSingleOutputLine(string stdout)
	{
		var line = Assert.Single(stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
		return line;
	}

	private static async Task<CommandLineProcessResult> RunAppAsync(params string[] args)
	{
		var appPath = typeof(App).Assembly.Location;
		var startInfo = new ProcessStartInfo
		{
			FileName = "dotnet",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
			StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
			UseShellExecute = false,
			CreateNoWindow = true
		};

		startInfo.ArgumentList.Add(appPath);
		foreach (var arg in args)
			startInfo.ArgumentList.Add(arg);

		using var process = Process.Start(startInfo)
			?? throw new InvalidOperationException("Failed to start DevProjex command-line stdout contract process.");
		var stdoutTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		var waitTask = process.WaitForExitAsync(TestContext.Current.CancellationToken);
		var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
		if (completed != waitTask)
		{
			TryKill(process);
			throw new TimeoutException("DevProjex stdout contract process did not exit within 20 seconds.");
		}

		return new CommandLineProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
	}

	private static string[] ReadStringArray(JsonElement element) =>
		element.EnumerateArray()
			.Select(static item => item.GetString() ?? string.Empty)
			.ToArray();

	private static void TryKill(Process process)
	{
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
		}
		catch
		{
			// The test is already failing on timeout; cleanup is best effort.
		}
	}

	private static void WriteFile(string rootPath, string relativePath, string content)
	{
		var path = Path.Combine(rootPath, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
	}

	private sealed record CommandLineProcessResult(int ExitCode, string Stdout, string Stderr);

	private sealed class TemporaryEnvironmentVariable : IDisposable
	{
		private readonly string _name;
		private readonly string? _previousValue;

		private TemporaryEnvironmentVariable(string name, string value)
		{
			_name = name;
			_previousValue = Environment.GetEnvironmentVariable(name);
			Environment.SetEnvironmentVariable(name, value);
		}

		public static TemporaryEnvironmentVariable Set(string name, string value) => new(name, value);

		public void Dispose() => Environment.SetEnvironmentVariable(_name, _previousValue);
	}
}
