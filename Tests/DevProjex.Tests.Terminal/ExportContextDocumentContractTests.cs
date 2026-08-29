using System.Globalization;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace DevProjex.Tests.Terminal;

public sealed class ExportContextDocumentContractTests
{
	[Fact]
	public async Task JsonDocumentPreservesOrderingAndOmitsBinaryBytes()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("z-last.txt", "last");
		workspace.WriteFile("a-empty", string.Empty);
		workspace.WriteFile("Юникод/данные.cs", "class Данные {}\n");
		var binaryPath = workspace.WriteFile("assets/raw.bin", "placeholder");
		await File.WriteAllBytesAsync(
			binaryPath,
			[0x00, 0x01, 0x02, 0xFF],
			TestContext.Current.CancellationToken);

		var first = new TestTerminalEnvironment();
		var second = new TestTerminalEnvironment();
		Assert.Equal(CommandLineExitCodes.Success, await RunAsync(workspace, first, "json"));
		Assert.Equal(CommandLineExitCodes.Success, await RunAsync(workspace, second, "json"));
		Assert.Equal(first.StandardOutput, second.StandardOutput);

		using var document = JsonDocument.Parse(first.StandardOutput);
		Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
		Assert.Equal("devprojex-context", document.RootElement.GetProperty("kind").GetString());
		var files = document.RootElement.GetProperty("files").EnumerateArray().ToArray();
		Assert.Equal(
			files.Select(static file => file.GetProperty("path").GetString()).OrderBy(static path => path, StringComparer.Ordinal),
			files.Select(static file => file.GetProperty("path").GetString()));

		var binary = Assert.Single(
			files,
			static file => file.GetProperty("path").GetString() == "assets/raw.bin");
		Assert.True(binary.GetProperty("isBinary").GetBoolean());
		Assert.Equal(JsonValueKind.Null, binary.GetProperty("content").ValueKind);
		Assert.DoesNotContain("AAEC", first.StandardOutput, StringComparison.Ordinal);
		Assert.Equal(
			Directory.EnumerateFiles(workspace.Path, "*", SearchOption.AllDirectories)
				.Sum(static path => new FileInfo(path).Length),
			document.RootElement.GetProperty("metrics").GetProperty("bytes").GetInt64());

		var empty = Assert.Single(
			files,
			static file => file.GetProperty("path").GetString() == "a-empty");
		Assert.False(empty.GetProperty("isBinary").GetBoolean());
		Assert.Equal(string.Empty, empty.GetProperty("content").GetString());
		Assert.Contains("Юникод/данные.cs", first.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(first.StandardError);
	}

	[Fact]
	public async Task XmlDocumentIsWellFormedAndEscapesFileContent()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/a&b.cs", "if (a < b && b > 0) {}\n");
		var environment = new TestTerminalEnvironment();

		Assert.Equal(CommandLineExitCodes.Success, await RunAsync(workspace, environment, "xml"));

		Assert.StartsWith(
			"<?xml version=\"1.0\" encoding=\"utf-8\"?>",
			environment.StandardOutput,
			StringComparison.Ordinal);
		var document = XDocument.Parse(environment.StandardOutput);
		Assert.Equal("devprojexContext", document.Root!.Name.LocalName);
		Assert.Equal("1", document.Root.Attribute("schemaVersion")?.Value);
		var file = Assert.Single(document.Root.Element("files")!.Elements("file"));
		Assert.Equal("src/a&b.cs", file.Attribute("path")?.Value);
		Assert.Equal("if (a < b && b > 0) {}\n", file.Element("content")?.Value);
	}

	[Fact]
	public async Task XmlDocumentRemainsWellFormedWhenTextContainsInvalidXmlCharacters()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/control.txt", "before\u000Bafter\n");
		var environment = new TestTerminalEnvironment();

		Assert.Equal(CommandLineExitCodes.Success, await RunAsync(workspace, environment, "xml"));

		var document = XDocument.Parse(environment.StandardOutput);
		var file = Assert.Single(document.Root!.Element("files")!.Elements("file"));
		var content = file.Element("content")?.Value;
		Assert.NotNull(content);
		Assert.Equal("before\uFFFDafter\n", content);
	}

	[Theory]
	[InlineData("json")]
	[InlineData("xml")]
	public async Task TokenBudgetAddsMachineReportAndKeepsIncludedFileOrder(string format)
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("A-large.txt", new string('a', 40));
		workspace.WriteFile("B-small.txt", "bbbb");
		workspace.WriteFile("C-small.txt", "cccc");
		var environment = new TestTerminalEnvironment();

		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(workspace, environment, format, maximumEstimatedTokens: 2));

		if (format == "json")
		{
			using var document = JsonDocument.Parse(environment.StandardOutput);
			Assert.Equal(
				["B-small.txt", "C-small.txt"],
				document.RootElement.GetProperty("files")
					.EnumerateArray()
					.Select(static file => file.GetProperty("path").GetString()));
			var report = document.RootElement.GetProperty("tokenBudget");
			Assert.Equal(2, report.GetProperty("maximumEstimatedTokens").GetInt64());
			Assert.Equal(2, report.GetProperty("includedFiles").GetInt32());
			Assert.Equal(1, report.GetProperty("skippedFiles").GetInt32());
			Assert.Equal("A-large.txt", report.GetProperty("largestSkippedFiles")[0]
				.GetProperty("path").GetString());
		}
		else
		{
			var document = XDocument.Parse(environment.StandardOutput);
			Assert.Equal(
				["B-small.txt", "C-small.txt"],
				document.Root!.Element("files")!.Elements("file")
					.Select(static file => file.Attribute("path")?.Value));
			var report = document.Root.Element("tokenBudget")!;
			Assert.Equal("2", report.Element("maximumEstimatedTokens")?.Value);
			Assert.Equal("2", report.Element("includedFiles")?.Value);
			Assert.Equal("1", report.Element("skippedFiles")?.Value);
			Assert.Equal(
				"A-large.txt",
				report.Element("largestSkippedFiles")?.Element("file")?.Attribute("path")?.Value);
		}

		Assert.Contains("Token budget 2:", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("A-large.txt", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("--compress-code", environment.StandardError, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("text")]
	[InlineData("markdown")]
	public async Task TokenBudgetOmitsOnlySkippedSectionsFromHumanDocuments(string format)
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("A-large.txt", "large-marker-" + new string('a', 40));
		workspace.WriteFile("B-small.txt", "small-marker");
		var environment = new TestTerminalEnvironment();

		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(
				workspace,
				environment,
				format,
				maximumEstimatedTokens: 3,
				view: "content"));

		Assert.DoesNotContain("large-marker", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("small-marker", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("A-large.txt", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("included 1 files", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("skipped 1 files", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task DryRunReportsTheSameTokenBudgetForecastWithoutWritingDocument()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("A-large.txt", new string('a', 40));
		workspace.WriteFile("B-small.txt", "bbbb");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"text",
			maximumEstimatedTokens: 1,
			dryRun: true);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("Token budget 1:", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("included 1 files (1 estimated tokens)", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("skipped 1 files", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("A-large.txt", environment.StandardError, StringComparison.Ordinal);

		var actual = new TestTerminalEnvironment();
		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(
				workspace,
				actual,
				"text",
				maximumEstimatedTokens: 1));
		Assert.Equal(
			ExtractBudgetReport(environment.StandardError),
			ExtractBudgetReport(actual.StandardError));
	}

	[Fact]
	public async Task TokenBudgetRejectsValuesBelowOne()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("app.cs", "class App { }");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"text",
			maximumEstimatedTokens: 0);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("--max-tokens must be an integer", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task MarkdownUsesSafeDynamicFenceAndCodeSpanForSpecialPath()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("docs/[guide]`name`.md", "before\n````\ninside\n````\nafter");
		var environment = new TestTerminalEnvironment();

		Assert.Equal(CommandLineExitCodes.Success, await RunAsync(workspace, environment, "markdown"));

		Assert.Contains("## ``docs/[guide]`name`.md``", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("`````md", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("before\n````\ninside\n````\nafter", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("\u001b", environment.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task FileOutputUsesExistingParentAndDoesNotModifySourceFiles()
	{
		using var workspace = new TemporaryDirectory();
		var source = workspace.WriteFile("project/src/app.cs", "class App {}\n");
		var before = await HashAsync(source);
		var destination = Path.Combine(
			workspace.CreateDirectory("output/nested"),
			"context.json");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"json",
			projectPath: Path.Combine(workspace.Path, "project"),
			outputPath: destination);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.True(File.Exists(destination));
		using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
			destination,
			TestContext.Current.CancellationToken));
		Assert.Equal("devprojex-context", document.RootElement.GetProperty("kind").GetString());
		Assert.Equal(before, await HashAsync(source));
		Assert.Equal(Path.GetFullPath(destination) + Environment.NewLine, environment.StandardOutput);
		Assert.Empty(Directory.EnumerateFiles(
			Path.GetDirectoryName(destination)!,
			".*.tmp"));
	}

	[Fact]
	public async Task PreCanceledExportDoesNotCreateOutputOrStagingFile()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("app.cs", "class App {}");
		var destination = Path.Combine(
			workspace.CreateDirectory("output"),
			"context.md");
		var environment = new TestTerminalEnvironment();
		using var cancellationSource = new CancellationTokenSource();
		cancellationSource.Cancel();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(
			[
				"export", "context", workspace.Path,
				"--git-mode", "none",
				"--exclude", "none",
				"-o", destination
			],
				cancellationSource.Token);

		Assert.Equal(CommandLineExitCodes.Canceled, exitCode);
		Assert.False(File.Exists(destination));
		if (Directory.Exists(Path.GetDirectoryName(destination)!))
		{
			Assert.Empty(Directory.EnumerateFiles(
				Path.GetDirectoryName(destination)!,
				".*.tmp"));
		}
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-CLI-CANCELED", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task FileOutputInsideSourceIsRejectedBeforeCreatingOutputOrStaging()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/app.cs", "class App {}");
		var destination = Path.Combine(workspace.Path, "generated", "context.md");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"markdown",
			outputPath: destination);

		Assert.Equal(CommandLineExitCodes.PolicyFailure, exitCode);
		Assert.False(File.Exists(destination));
		Assert.Contains("DPX-EXPORT-UNSAFE-DESTINATION", environment.StandardError, StringComparison.Ordinal);
		if (Directory.Exists(Path.GetDirectoryName(destination)!))
		{
			Assert.Empty(Directory.EnumerateFiles(
				Path.GetDirectoryName(destination)!,
				".*.tmp"));
		}
	}

	private static Task<int> RunAsync(
		TemporaryDirectory workspace,
		TestTerminalEnvironment environment,
		string format,
		string? projectPath = null,
		string? outputPath = null,
		int? maximumEstimatedTokens = null,
		bool dryRun = false,
		string view = "tree-content")
	{
		var arguments = new List<string>
		{
			"--language", "en",
			"export", "context", projectPath ?? workspace.Path,
			"--view", view,
			"--format", format,
			"--git-mode", "none",
			"--exclude", "none",
			"-o", outputPath ?? "-"
		};
		if (maximumEstimatedTokens is not null)
		{
			arguments.Add("--max-tokens");
			arguments.Add(maximumEstimatedTokens.Value.ToString(CultureInfo.InvariantCulture));
		}
		if (dryRun)
			arguments.Add("--dry-run");

		return new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(arguments.ToArray(), TestContext.Current.CancellationToken);
	}

	private static async Task<string> HashAsync(string path)
	{
		await using var stream = File.OpenRead(path);
		return Convert.ToHexString(await SHA256.HashDataAsync(
			stream,
			TestContext.Current.CancellationToken));
	}

	private static string ExtractBudgetReport(string standardError)
	{
		var start = standardError.IndexOf("Token budget", StringComparison.Ordinal);
		Assert.True(start >= 0, standardError);
		return standardError[start..];
	}
}
