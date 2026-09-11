using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Xml.Linq;
using DevProjex.Application.Diagnostics;
using DevProjex.Application.Secrets;
using DevProjex.Application.Services;

namespace DevProjex.Tests.Terminal;

public sealed class ExportContextDocumentContractTests
{
	[Theory]
	[InlineData("text")]
	[InlineData("markdown")]
	public void ContentExportProcessPrintsTheRootOnceAndUsesRelativeHeaders(string format)
	{
		using var workspace = new TemporaryDirectory();
		using var data = new TemporaryDirectory();
		workspace.WriteFile("docs/Guide.md", "guide-content\n");
		workspace.WriteFile("README.md", "readme-content\n");

		var result = RunProcess(
			data.Path,
			"export", "context", workspace.Path,
			"--view", "content",
			"--format", format,
			"--git-mode", "none",
			"--exclude", "none",
			"--plain",
			"--progress", "never",
			"-o", "-");

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Empty(result.StandardError);
		var displayRoot = format == "markdown"
			? PathUtility.NormalizeSeparators(workspace.Path)
			: workspace.Path;
		var rootLine = format == "markdown"
			? ContextRootPresentation.FormatMarkdownLine(displayRoot)
			: ContextRootPresentation.FormatLine(displayRoot);
		Assert.Contains(rootLine, result.StandardOutput, StringComparison.Ordinal);
		Assert.Equal(1, CountOccurrences(result.StandardOutput, rootLine));
		Assert.Contains("docs/Guide.md", result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("README.md", result.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain(
			Path.Combine(workspace.Path, "docs", "Guide.md"),
			result.StandardOutput,
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(false, "└── docs")]
	[InlineData(true, "`-- docs")]
	public void TextTreeProcessStartsAtTheFirstRealChild(bool plain, string childLine)
	{
		using var workspace = new TemporaryDirectory();
		using var data = new TemporaryDirectory();
		workspace.WriteFile("docs/Guide.md", "guide-content\n");
		var arguments = new List<string>
		{
			"tree", workspace.Path,
			"--format", "text",
			"--git-mode", "none",
			"--exclude", "none",
			"--progress", "never",
			"-o", "-"
		};
		if (plain)
			arguments.Add("--plain");

		var result = RunProcess(data.Path, arguments.ToArray());
		var lines = result.StandardOutput
			.Replace("\r\n", "\n", StringComparison.Ordinal)
			.Split('\n');

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Empty(result.StandardError);
		Assert.Equal(workspace.Path + ":", lines[0]);
		Assert.Equal(childLine, lines[1]);
		Assert.DoesNotContain(lines, line => line.EndsWith(
			Path.GetFileName(workspace.Path),
			StringComparison.Ordinal));
	}

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

		Assert.Contains("Estimated token budget: 2.", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("A-large.txt", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("--compress-code", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task TokenBudgetSelectsTheSameOrderedFilesAcrossAllFormats()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("A-large.txt", "large-content-marker-" + new string('a', 40));
		workspace.WriteFile("B-small.txt", "b");
		workspace.WriteFile("C-small.txt", "c");
		var reports = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var format in new[] { "text", "markdown", "json", "xml" })
		{
			var environment = new TestTerminalEnvironment();
			Assert.Equal(
				CommandLineExitCodes.Success,
				await RunAsync(
					workspace,
					environment,
					format,
					maximumEstimatedTokens: 2,
					view: "content"));

			Assert.DoesNotContain("large-content-marker", environment.StandardOutput, StringComparison.Ordinal);
			var first = environment.StandardOutput.IndexOf("B-small.txt", StringComparison.Ordinal);
			var second = environment.StandardOutput.IndexOf("C-small.txt", StringComparison.Ordinal);
			Assert.True(first >= 0 && second > first, environment.StandardOutput);
			reports.Add(format, ExtractBudgetReport(environment.StandardError));
		}

		Assert.Equal(reports["text"], reports["markdown"]);
		Assert.Equal(reports["json"], reports["xml"]);
		Assert.Contains("  A-large.txt (16 estimated tokens)", reports["text"], StringComparison.Ordinal);
		Assert.Contains(
			$"  {Path.Combine(workspace.Path, "A-large.txt")} (16 estimated tokens)",
			reports["json"],
			StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("text")]
	[InlineData("markdown")]
	public async Task TreeContentDocumentKeepsOneRootAndRelativeContentHeaders(string format)
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("docs/Guide.md", "guide-content\n");
		var environment = new TestTerminalEnvironment();

		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(workspace, environment, format, view: "tree-content"));

		Assert.Equal(1, CountOccurrences(environment.StandardOutput, workspace.Path));
		Assert.DoesNotContain(
			"└── " + Path.GetFileName(workspace.Path),
			environment.StandardOutput,
			StringComparison.Ordinal);
		Assert.Contains("└── docs", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("docs/Guide.md", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain(
			Path.Combine(workspace.Path, "docs", "Guide.md"),
			environment.StandardOutput,
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Theory]
	[InlineData(ProjectContextDocumentFormat.Json)]
	[InlineData(ProjectContextDocumentFormat.Xml)]
	public async Task RemoteMachineDiagnosticsUseSourceAddressInsteadOfCheckoutPath(
		ProjectContextDocumentFormat format)
	{
		using var workspace = new TemporaryDirectory();
		var checkout = workspace.CreateDirectory("internal-cache/checkout");
		File.WriteAllText(Path.Combine(checkout, "App.cs"), "internal sealed class App { }");
		var repositoryUrl = new Uri(workspace.CreateDirectory("origin/repository.git")).AbsoluteUri;
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			checkout,
			new ProjectSelectionSpec(GitMode: GitFilteringMode.None, Exclusions: []),
			new ProjectSourceIdentity(
				"repository",
				ProjectSourceType.GitClone,
				repositoryUrl,
				repositoryUrl,
				IsCachedRepository: true),
			TestContext.Current.CancellationToken);
		plan = plan with
		{
			Diagnostics =
			[
				new ContextDiagnostic(
					"DPX-TEST-CACHE-PATH",
					ContextDiagnosticSeverity.Warning,
					"Diagnostic path presentation probe.",
					checkout)
			]
		};
		using var destination = new MemoryStream();

		await services.ContextDocumentService.WriteCompleteAsync(
			plan,
			ProjectContextView.Content,
			format,
			destination,
			TestContext.Current.CancellationToken,
			useSourceMappedStructuredPaths: true);
		var output = Encoding.UTF8.GetString(destination.ToArray());
		var expected = RepositoryWebPathPresentationService.NormalizeForDisplay(repositoryUrl);
		string? diagnosticPath;
		if (format == ProjectContextDocumentFormat.Json)
		{
			using var document = JsonDocument.Parse(output);
			diagnosticPath = document.RootElement.GetProperty("diagnostics")[0]
				.GetProperty("path").GetString();
		}
		else
		{
			diagnosticPath = XDocument.Parse(output).Root!.Element("diagnostics")!
				.Element("diagnostic")!.Attribute("path")?.Value;
		}

		Assert.Equal(expected, diagnosticPath);
		Assert.DoesNotContain(checkout, output, StringComparison.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData(ProjectContextDocumentFormat.Json)]
	[InlineData(ProjectContextDocumentFormat.Xml)]
	public async Task LocalMachineDiagnosticsPreserveAbsolutePaths(
		ProjectContextDocumentFormat format)
	{
		using var workspace = new TemporaryDirectory();
		var source = workspace.WriteFile("project/App.cs", "internal sealed class App { }");
		var project = Path.GetDirectoryName(source)!;
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			new ProjectSelectionSpec(GitMode: GitFilteringMode.None, Exclusions: []),
			cancellationToken: TestContext.Current.CancellationToken);
		plan = plan with
		{
			Diagnostics =
			[
				new ContextDiagnostic(
					"DPX-TEST-LOCAL-PATH",
					ContextDiagnosticSeverity.Warning,
					"Local diagnostic path presentation probe.",
					source)
			]
		};
		using var destination = new MemoryStream();

		await services.ContextDocumentService.WriteCompleteAsync(
			plan,
			ProjectContextView.Content,
			format,
			destination,
			TestContext.Current.CancellationToken,
			useSourceMappedStructuredPaths: true);
		var output = Encoding.UTF8.GetString(destination.ToArray());
		string? diagnosticPath;
		string? serializedFilePath;
		if (format == ProjectContextDocumentFormat.Json)
		{
			using var document = JsonDocument.Parse(output);
			diagnosticPath = document.RootElement.GetProperty("diagnostics")[0]
				.GetProperty("path").GetString();
			serializedFilePath = document.RootElement.GetProperty("files")[0]
				.GetProperty("path").GetString();
		}
		else
		{
			var document = XDocument.Parse(output);
			diagnosticPath = document.Root!.Element("diagnostics")!
				.Element("diagnostic")!.Attribute("path")?.Value;
			serializedFilePath = document.Root.Element("files")!
				.Element("file")!.Attribute("path")?.Value;
		}

		var expectedPath = PathUtility.NormalizeSeparators(Path.GetFullPath(source));
		Assert.Equal(expectedPath, diagnosticPath);
		Assert.Equal(expectedPath, serializedFilePath);
	}

	[Theory]
	[InlineData("json")]
	[InlineData("xml")]
	public async Task MachineMetricsReflectTransformedContentBeforeTokenBudget(string format)
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile(
			"App.cs",
			"internal static class App { public static int Run() { " +
			string.Join(' ', Enumerable.Repeat("var value = 12345;", 100)) +
			" return 1; } }");
		var full = new TestTerminalEnvironment();
		var compressed = new TestTerminalEnvironment();

		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(workspace, full, format, maximumEstimatedTokens: 10_000));
		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(
				workspace,
				compressed,
				format,
				maximumEstimatedTokens: 10_000,
				compressCode: true));

		var fullTokens = ReadEstimatedTokens(format, full.StandardOutput);
		var compressedTokens = ReadEstimatedTokens(format, compressed.StandardOutput);
		Assert.True(compressedTokens < fullTokens, $"Expected {compressedTokens} < {fullTokens}.");
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
		Assert.Contains("Included files: 1", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("Skipped files: 1", environment.StandardError, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("text")]
	[InlineData("markdown")]
	public async Task AllFitTokenBudgetPreservesHumanDocumentBytes(string format)
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("alpha.txt", "alpha\r\n");
		workspace.WriteFile("Unicode/данные.txt", "emoji 😀\n");
		var unlimited = new TestTerminalEnvironment();
		var budgeted = new TestTerminalEnvironment();

		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(workspace, unlimited, format, view: "content"));
		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(
				workspace,
				budgeted,
				format,
				maximumEstimatedTokens: 10_000,
				view: "content"));

		Assert.Equal(unlimited.StandardOutput, budgeted.StandardOutput);
		Assert.Empty(unlimited.StandardError);
		Assert.Contains("Skipped files: 0", budgeted.StandardError, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("tree-content")]
	[InlineData("content")]
	public async Task DryRunReportsTheSameTokenBudgetForecastWithoutWritingDocument(string view)
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
			dryRun: true,
			view: view);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("Estimated token budget: 1.", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("Included files: 1; estimated tokens: 1", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("Skipped files: 1", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("A-large.txt", environment.StandardError, StringComparison.Ordinal);

		var actual = new TestTerminalEnvironment();
		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(
				workspace,
				actual,
				"text",
				maximumEstimatedTokens: 1,
				view: view));
		Assert.Equal(
			ExtractBudgetReport(environment.StandardError),
			ExtractBudgetReport(actual.StandardError));
	}

	[Fact]
	public async Task RankedCompressedDryRunUsesMeasuredBudgetWithoutMaterializingOrSerializing()
	{
		using var workspace = new TemporaryDirectory();
		var firstPath = workspace.WriteFile("A.cs", "public sealed class A { public int Value => 1; }\n");
		var secondPath = workspace.WriteFile("B.cs", "public sealed class B { private readonly A dependency = new(); }\n");
		var dryRun = new TestTerminalEnvironment();
		using var measurement = DevProjex.Application.Diagnostics.ContentPipelineDiagnostics.BeginMeasurement();

		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(
				workspace,
				dryRun,
				"markdown",
				maximumEstimatedTokens: 8,
				dryRun: true,
				view: "content",
				compressCode: true,
				rank: true));
		var diagnostics = measurement.Capture();

		var actual = new TestTerminalEnvironment();
		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(
				workspace,
				actual,
				"markdown",
				maximumEstimatedTokens: 8,
				view: "content",
				compressCode: true,
				rank: true));

		Assert.Equal(ExtractBudgetReport(dryRun.StandardError), ExtractBudgetReport(actual.StandardError));
		Assert.Empty(dryRun.StandardOutput);
		Assert.Equal(0, diagnostics.PreparedFilesMaterialized);
		Assert.Equal(0, diagnostics.PreparedWriteBytes);
		Assert.Equal(0, diagnostics.DocumentWriteBytes);
		// One content pass inside ranking and one at write time; ranking no longer reads the
		// selection a second time to compare it with itself.
		Assert.Equal(4, diagnostics.SourceVersionHashPasses);
		Assert.Equal(
			2 * (new FileInfo(firstPath).Length + new FileInfo(secondPath).Length),
			diagnostics.SourceVersionHashBytes);
	}

	[Fact]
	public async Task TokenBudgetedExportMaterializesOnlyAdmittedFiles()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("A.txt", "aaaa");
		workspace.WriteFile("B.txt", "bbbb");
		workspace.WriteFile("C.txt", "cccc");
		var environment = new TestTerminalEnvironment();
		using var measurement = ContentPipelineDiagnostics.BeginMeasurement();

		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(
				workspace,
				environment,
				"text",
				maximumEstimatedTokens: 1,
				view: "content",
				hideSecrets: true));
		var diagnostics = measurement.Capture();

		Assert.Contains("Included files: 1", environment.StandardError, StringComparison.Ordinal);
		Assert.Equal(1, diagnostics.PreparedFilesMaterialized);
	}

	[Theory]
	[InlineData("utf8-crlf-below", -1L)]
	[InlineData("utf8-nonascii-exact", 0L)]
	[InlineData("utf8-nonascii-above", 2L)]
	[InlineData("utf16-crlf-above", 4L)]
	public async Task CompressedDryRunAndExportAgreeOnExactLargePassThroughBudget(
		string variant,
		long sizeDelta)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = Path.Combine(project, "large.txt");
		var expectedCharacters = await WriteLargeTextVariantAsync(source, variant, sizeDelta);
		var maximumTokens = checked((expectedCharacters + 3L) / 4L);
		var destination = Path.Combine(workspace.Path, "context.txt");
		var dryRun = new TestTerminalEnvironment();

		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(
				workspace,
				dryRun,
				"text",
				projectPath: project,
				outputPath: destination,
				maximumEstimatedTokens: maximumTokens,
				dryRun: true,
				view: "content",
				compressCode: true));

		var actual = new TestTerminalEnvironment();
		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(
				workspace,
				actual,
				"text",
				projectPath: project,
				outputPath: destination,
				maximumEstimatedTokens: maximumTokens,
				view: "content",
				compressCode: true));

		Assert.Equal(ExtractBudgetReport(dryRun.StandardError), ExtractBudgetReport(actual.StandardError));
		Assert.Contains("Included files: 1", actual.StandardError, StringComparison.Ordinal);
		Assert.Contains("Skipped files: 0", actual.StandardError, StringComparison.Ordinal);
		var expectedWrittenCharacters = variant.Contains("crlf", StringComparison.Ordinal)
			? expectedCharacters - 2
			: expectedCharacters;
		Assert.Equal(expectedWrittenCharacters, await CountExportedTextCharactersAsync(destination));
	}

	[Theory]
	[InlineData("json")]
	[InlineData("xml")]
	public async Task StructuredLargeUtf16PassThroughUsesExactPreparedMetrics(string format)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = Path.Combine(project, "large.txt");
		var expectedCharacters = await WriteLargeTextVariantAsync(source, "utf16-crlf-above", 4);
		var destination = Path.Combine(workspace.Path, $"context.{format}");
		var environment = new TestTerminalEnvironment();
		using var measurement = ContentPipelineDiagnostics.BeginMeasurement();

		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(
				workspace,
				environment,
				format,
				projectPath: project,
				outputPath: destination,
				view: "content",
				compressCode: true));
		var diagnostics = measurement.Capture();
		var document = await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken);
		var totalBytes = SecretRedactionOutputPreparer.MaximumScannableFileBytes + 4;
		var crLfPairs = checked((int)((totalBytes - 2) / 6));
		var expectedMetricCharacters = ExportOutputMetricsCalculator.FromOrderedContentFiles([
			new ContentFileMetrics(
				source,
				totalBytes,
				LineCount: crLfPairs + 1,
				CharCount: checked((int)expectedCharacters),
				IsEmpty: false,
				IsWhitespaceOnly: false,
				IsEstimated: false,
				CrLfPairCount: crLfPairs,
				TrailingNewlineChars: 2,
				TrailingNewlineLineBreaks: 1)
		]).Chars;
		var characters = format == "json"
			? JsonDocument.Parse(document).RootElement.GetProperty("metrics").GetProperty("characters").GetInt64()
			: long.Parse(
				XDocument.Parse(document).Root!.Element("metrics")!.Element("characters")!.Value,
				CultureInfo.InvariantCulture);

		Assert.Equal(expectedMetricCharacters, characters);
		Assert.Equal(0, diagnostics.PreparedReadBytes);
	}

	[Theory]
	[InlineData("markdown")]
	[InlineData("text")]
	public async Task HumanLargePassThroughDoesNotRunASeparatePreparationMetricsPass(string format)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = Path.Combine(project, "large.txt");
		await WriteLargeTextVariantAsync(source, "utf8-nonascii-above", 2);
		var destination = Path.Combine(workspace.Path, $"context.{format}");
		var environment = new TestTerminalEnvironment();
		using var measurement = ContentPipelineDiagnostics.BeginMeasurement();

		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(
				workspace,
				environment,
				format,
				projectPath: project,
				outputPath: destination,
				view: "content",
				compressCode: true));
		var diagnostics = measurement.Capture();

		Assert.Equal(2, diagnostics.FullFileReads);
		Assert.Equal(0, diagnostics.PreparedReadBytes);
	}

	[Fact]
	public async Task RankedSourceBackedExportHashesEachSourceTwice()
	{
		using var workspace = new TemporaryDirectory();
		var firstPath = workspace.WriteFile("A.cs", "public sealed class A { }\n");
		var secondPath = workspace.WriteFile("B.cs", "public sealed class B { private readonly A value = new(); }\n");
		var environment = new TestTerminalEnvironment();
		using var measurement = DevProjex.Application.Diagnostics.ContentPipelineDiagnostics.BeginMeasurement();

		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(
				workspace,
				environment,
				"markdown",
				view: "content",
				rank: true));
		var diagnostics = measurement.Capture();

		// One content pass inside ranking and one at write time; ranking no longer reads the
		// selection a second time to compare it with itself.
		Assert.Equal(4, diagnostics.SourceVersionHashPasses);
		Assert.Equal(
			2 * (new FileInfo(firstPath).Length + new FileInfo(secondPath).Length),
			diagnostics.SourceVersionHashBytes);
	}

	[Theory]
	[InlineData("json")]
	[InlineData("xml")]
	public async Task StructuredCompressedExportReusesPreparationMetricsWithoutRereadingPreparedText(string format)
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile(
			"App.cs",
			"public sealed class App { private int Hidden() { return 42; } }\n");
		var environment = new TestTerminalEnvironment();
		using var measurement = DevProjex.Application.Diagnostics.ContentPipelineDiagnostics.BeginMeasurement();

		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(
				workspace,
				environment,
				format,
				view: "content",
				compressCode: true));
		var diagnostics = measurement.Capture();
		var documentFormat = format == "json"
			? ProjectContextDocumentFormat.Json
			: ProjectContextDocumentFormat.Xml;
		using var referenceData = new TemporaryDirectory();
		using var referenceServices = new TerminalServiceFactory(
				() => referenceData.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var referencePlan = await referenceServices.ContextFactory.BuildAsync(
			workspace.Path,
			new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: [],
				CompressCode: true),
			cancellationToken: TestContext.Current.CancellationToken);
		var transformation = DevProjex.Application.Compression.ContentTransformationContext.For(
			new DevProjex.Application.Compression.CodeCompressionContext(
				referencePlan.SourceRoot,
				referenceServices.CodeCompressionSession,
				DevProjex.Application.Compression.CodeTransformKinds.Bodies),
			redaction: null)!;
		await using var referencePrepared = await referenceServices.SecretRedactionOutputPreparer
			.PrepareAsync(transformation, referencePlan.IncludedFiles, TestContext.Current.CancellationToken);
		using var referenceDestination = new MemoryStream();
		await referenceServices.ContextDocumentService.WritePreparedCompleteAsync(
			referencePlan,
			ProjectContextView.Content,
			documentFormat,
			referenceDestination,
			referencePrepared,
			TestContext.Current.CancellationToken,
			useSourceMappedStructuredPaths: true);
		var referenceOutput = Encoding.UTF8.GetString(referenceDestination.ToArray());

		Assert.Equal(referenceOutput + Environment.NewLine, environment.StandardOutput);
		Assert.True(diagnostics.PreparedFilesMaterialized > 0, diagnostics.ToString());
		Assert.Equal(diagnostics.PreparedWriteBytes, diagnostics.PreparedReadBytes);
		Assert.True(diagnostics.DocumentWriteBytes > 0, diagnostics.ToString());
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
	public async Task TokenBudgetAcceptsPositiveValuesAboveInt32Range()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("app.cs", "class App { }");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"text",
			maximumEstimatedTokens: (long)int.MaxValue + 1);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains("class App", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("Estimated token budget: 2147483648.", environment.StandardError, StringComparison.Ordinal);
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
		long? maximumEstimatedTokens = null,
		bool dryRun = false,
		string view = "tree-content",
		bool compressCode = false,
		bool rank = false,
		bool hideSecrets = false,
		IReadOnlyList<string>? detailFor = null)
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
		if (compressCode)
			arguments.Add("--compress-code");
		if (hideSecrets)
			arguments.Add("--hide-secrets");
		if (rank)
		{
			arguments.Add("--rank");
			arguments.Add("importance");
		}
		foreach (var value in detailFor ?? [])
		{
			arguments.Add("--detail-for");
			arguments.Add(value);
		}

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

	private static async Task<long> WriteLargeTextVariantAsync(
		string path,
		string variant,
		long sizeDelta)
	{
		var totalBytes = SecretRedactionOutputPreparer.MaximumScannableFileBytes + sizeDelta;
		byte[] prefix;
		byte[] pattern;
		long expectedCharacters;
		switch (variant)
		{
			case "utf8-crlf-below":
				prefix = [];
				pattern = "a\r\n"u8.ToArray();
				expectedCharacters = totalBytes;
				break;
			case "utf8-nonascii-exact":
			case "utf8-nonascii-above":
				prefix = [];
				pattern = "é"u8.ToArray();
				expectedCharacters = totalBytes / pattern.Length;
				break;
			case "utf16-crlf-above":
				prefix = [0xff, 0xfe];
				pattern = [0x61, 0x00, 0x0d, 0x00, 0x0a, 0x00];
				expectedCharacters = (totalBytes - prefix.Length) / sizeof(char);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(variant));
		}
		Assert.Equal(0, (totalBytes - prefix.Length) % pattern.Length);

		await using var stream = new FileStream(
			path,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None,
			bufferSize: 64 * 1024,
			useAsync: true);
		await stream.WriteAsync(prefix, TestContext.Current.CancellationToken);
		var buffer = new byte[64 * 1024 - (64 * 1024 % pattern.Length)];
		for (var offset = 0; offset < buffer.Length; offset += pattern.Length)
			pattern.CopyTo(buffer, offset);
		var remaining = totalBytes - prefix.Length;
		while (remaining > 0)
		{
			var count = (int)Math.Min(buffer.Length, remaining);
			await stream.WriteAsync(buffer.AsMemory(0, count), TestContext.Current.CancellationToken);
			remaining -= count;
		}
		return expectedCharacters;
	}

	private static async Task<long> CountExportedTextCharactersAsync(string path)
	{
		var content = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
		var marker = $"{Environment.NewLine}{Environment.NewLine}large.txt:{Environment.NewLine}{Environment.NewLine}";
		var start = content.IndexOf(marker, StringComparison.Ordinal);
		Assert.True(start >= 0, content[..Math.Min(content.Length, 256)]);
		return content.Length - start - marker.Length;
	}

	private static string ExtractBudgetReport(string standardError)
	{
		var start = standardError.IndexOf("Estimated token budget", StringComparison.Ordinal);
		Assert.True(start >= 0, standardError);
		return standardError[start..];
	}

	private static TerminalTestProcessResult RunProcess(string dataRoot, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		startInfo.ArgumentList.Add(PublishedApplicationLocator.FindApplicationAssembly());
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		startInfo.Environment[InvocationEnvironment.TerminalHostVariable] = "1";
		startInfo.Environment[InvocationEnvironment.InternalDataRootVariable] = dataRoot;
		startInfo.Environment["DOTNET_NOLOGO"] = "1";
		return TerminalTestProcess.Run(startInfo);
	}

	private static int CountOccurrences(string value, string fragment)
	{
		var count = 0;
		for (var offset = 0;;)
		{
			var index = value.IndexOf(fragment, offset, StringComparison.Ordinal);
			if (index < 0)
				return count;
			count++;
			offset = index + fragment.Length;
		}
	}

	private static long ReadEstimatedTokens(string format, string output)
	{
		if (format == "json")
		{
			using var document = JsonDocument.Parse(output);
			return document.RootElement.GetProperty("metrics").GetProperty("estimatedTokens").GetInt64();
		}

		return long.Parse(
			XDocument.Parse(output).Root!.Element("metrics")!.Element("estimatedTokens")!.Value,
			CultureInfo.InvariantCulture);
	}
}
