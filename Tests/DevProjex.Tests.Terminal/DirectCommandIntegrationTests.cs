namespace DevProjex.Tests.Terminal;

public sealed class DirectCommandIntegrationTests
{
	private const string CompressibleSource = """
		namespace Sample;

		public sealed class Widget
		{
			public int Compute(int left, int right)
			{
				var valueThatMustDisappear = left + right;
				for (var index = 0; index < 8; index++)
					valueThatMustDisappear += index * left - right;
				return valueThatMustDisappear;
			}
		}
		""";

	private const string CommentedSource = """
		// file documentation that must disappear
		namespace Sample;

		public sealed class CommentedWidget
		{
			public string Value() // trailing note that must disappear
			{
				var marker = "// string content must remain";
				return marker; /* inline note that must disappear */
			}
		}
		""";

	private const string BlankLineSource = """"
		namespace Sample;

		public sealed class BlankLineWidget
		{

			private const string Text = """
		first

		second
		""";

			public string Value => Text;
		}
		"""";

	private const string RubyCompressibleSource = """
		class Service
		  def initialize(root)
		    @root = root
		  end

		  def run(value)
		    ruby_direct_cli_marker = File.join(@root, value)
		    ruby_direct_cli_marker
		  end
		end
		""";

	private const string PhpCompressibleSource = """
		<?php
		final class Service
		{
		    public function __construct(private string $root)
		    {
		        $this->root = trim($root);
		    }

		    public function run(string $value): string
		    {
		        $php_direct_cli_marker = $this->root.'/'.$value;
		        return $php_direct_cli_marker;
		    }
		}
		""";

	private const string ScalaCompressibleSource = """
		object Service {
		  val normalize: String => String = value => value.trim

		  def run(value: String): String = {
		    val scala_direct_cli_marker = normalize(value)
		    scala_direct_cli_marker
		  }
		}
		""";

	private const string KotlinCompressibleSource = """
		class Service(
		    val root: String,
		) {
		    val normalize: (String) -> String = { value -> value.trim() }

		    fun run(value: String): String {
		        val kotlin_direct_cli_marker = normalize(value)
		        return kotlin_direct_cli_marker
		    }
		}
		""";

	[Fact]
	public async Task TreeDoesNotReadSelectedFileContents()
	{
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		workspace.WriteFile("src/app.cs", "tree-read-probe\n");
		workspace.WriteFile("README.md", "# Tree read probe\n");
		var environment = new TestTerminalEnvironment();
		using var measurement = DevProjex.Application.Diagnostics.ContentPipelineDiagnostics.BeginMeasurement();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => appData.Path))
			.RunAsync(
			[
				"tree", workspace.Path,
				"--git-mode", "none",
				"--exclude", "none"
			],
				TestContext.Current.CancellationToken);

		var diagnostics = measurement.Capture();
		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains("app.cs", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("README.md", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Equal(0, diagnostics.FullFileReads);
		Assert.Equal(0, diagnostics.FullFileReadBytes);
	}

	[Fact]
	public async Task AnalyzeJsonWritesStableMachineDocumentOnlyToStdout()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/app.cs", "class App {}\n");
		workspace.WriteFile("README.md", "# App\n");
		var environment = new TestTerminalEnvironment();
		var factory = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"));

		var exitCode = await new TerminalApplication(environment, factory).RunAsync(
		[
			"analyze",
			workspace.Path,
			"--format=json",
			"--git-mode",
			"none",
			"--exclude",
			"none",
			"-o",
			"-"
		],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var json = JsonDocument.Parse(environment.StandardOutput);
		Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
		Assert.Equal("devprojex-analysis", json.RootElement.GetProperty("kind").GetString());
		Assert.Equal("none", json.RootElement.GetProperty("selection").GetProperty("gitMode").GetString());
		Assert.False(json.RootElement.TryGetProperty("topFiles", out _));
		var expectedBytes = Directory.EnumerateFiles(
				workspace.Path,
				"*",
				SearchOption.AllDirectories)
			.Where(static path => !path.Contains(
				$"{Path.DirectorySeparatorChar}app-data{Path.DirectorySeparatorChar}",
				StringComparison.Ordinal))
			.Sum(static path => new FileInfo(path).Length);
		Assert.Equal(
			expectedBytes,
			json.RootElement.GetProperty("metrics").GetProperty("bytes").GetInt64());
		Assert.Empty(environment.StandardError);
		Assert.DoesNotContain("\u001b", environment.StandardOutput, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task AnalyzeTopFilesRanksEffectiveContentOnlyWhenRequested(bool compressCode)
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/Large.cs", CompressibleSource + CompressibleSource);
		workspace.WriteFile("src/Small.cs", "internal sealed class Small {}\n");
		var environment = new TestTerminalEnvironment();
		var arguments = new List<string>
		{
			"analyze", workspace.Path,
			"--format", "json",
			"--top-files", "1",
			"--git-mode", "none",
			"--exclude", "none"
		};
		if (compressCode)
			arguments.Add("--compress-code");

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(arguments.ToArray(), TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		var topFile = Assert.Single(
			document.RootElement.GetProperty("topFiles").EnumerateArray());
		Assert.Equal("src/Large.cs", topFile.GetProperty("path").GetString());
		Assert.True(topFile.GetProperty("tokens").GetInt64() > 0);
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task AnalyzeTopFilesAddsLocalizedTextSection()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/Large.cs", CompressibleSource);
		workspace.WriteFile("src/Small.cs", "class Small {}\n");
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(
				[
					"analyze", workspace.Path,
					"--top-files", "1",
					"--git-mode", "none",
					"--exclude", "none",
					"--language", "en"
				],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains("Largest files:", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("src/Large.cs", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task MaximumFileBytesNarrowsAnalyzeTreeAndContextExport()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Small.txt", "small-marker\n");
		workspace.WriteFile("project/Exact.txt", new string('e', 64));
		workspace.WriteFile(
			"project/Large.txt",
			"oversized-marker\n" + new string('x', 128));
		var factory = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"));

		var analysisEnvironment = new TestTerminalEnvironment();
		var analysisExit = await new TerminalApplication(analysisEnvironment, factory).RunAsync(
			[
				"analyze", project,
				"--format", "json",
				"--max-file-bytes", "64",
				"--git-mode", "none",
				"--exclude", "none"
			],
			TestContext.Current.CancellationToken);
		using var analysis = JsonDocument.Parse(analysisEnvironment.StandardOutput);

		var textEnvironment = new TestTerminalEnvironment();
		var textExit = await new TerminalApplication(textEnvironment, factory).RunAsync(
			[
				"analyze", project,
				"--max-file-bytes", "64",
				"--git-mode", "none",
				"--exclude", "none",
				"--language", "en"
			],
			TestContext.Current.CancellationToken);

		var treeEnvironment = new TestTerminalEnvironment();
		var treeExit = await new TerminalApplication(treeEnvironment, factory).RunAsync(
			[
				"tree", project,
				"--max-file-bytes", "64",
				"--git-mode", "none",
				"--exclude", "none"
			],
			TestContext.Current.CancellationToken);

		var output = Path.Combine(workspace.Path, "context.md");
		var contextEnvironment = new TestTerminalEnvironment();
		var contextExit = await new TerminalApplication(contextEnvironment, factory).RunAsync(
			[
				"export", "context", project,
				"--view", "content",
				"--max-file-bytes", "64",
				"--git-mode", "none",
				"--exclude", "none",
				"-o", output
			],
			TestContext.Current.CancellationToken);

		var dryRunEnvironment = new TestTerminalEnvironment();
		var dryRunExit = await new TerminalApplication(dryRunEnvironment, factory).RunAsync(
			[
				"export", "context", project,
				"--max-file-bytes", "64",
				"--git-mode", "none",
				"--exclude", "none",
				"--dry-run",
				"--language", "en",
				"-o", Path.Combine(workspace.Path, "dry-run.md")
			],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, analysisExit);
		Assert.Equal(2, analysis.RootElement.GetProperty("inventory").GetProperty("files").GetInt32());
		Assert.Equal(77, analysis.RootElement.GetProperty("metrics").GetProperty("bytes").GetInt64());
		Assert.Empty(analysisEnvironment.StandardError);
		Assert.Equal(CommandLineExitCodes.Success, textExit);
		Assert.Contains("Size filter:", textEnvironment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("excluded 1 files", textEnvironment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(textEnvironment.StandardError);
		Assert.Equal(CommandLineExitCodes.Success, treeExit);
		Assert.Contains("Small.txt", treeEnvironment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("Exact.txt", treeEnvironment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("Large.txt", treeEnvironment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(treeEnvironment.StandardError);
		Assert.Equal(CommandLineExitCodes.Success, contextExit);
		var context = File.ReadAllText(output);
		Assert.Contains("small-marker", context, StringComparison.Ordinal);
		Assert.Contains("Exact.txt", context, StringComparison.Ordinal);
		Assert.DoesNotContain("Large.txt", context, StringComparison.Ordinal);
		Assert.Empty(contextEnvironment.StandardError);
		Assert.Equal(CommandLineExitCodes.Success, dryRunExit);
		Assert.False(File.Exists(Path.Combine(workspace.Path, "dry-run.md")));
		Assert.Contains("Size filter: up to 64 B; excluded 1 files", dryRunEnvironment.StandardError, StringComparison.Ordinal);
		Assert.Empty(dryRunEnvironment.StandardOutput);
	}

	[Fact]
	public async Task AnalyzeWithCompressionReportsMetricsForTheTransformedContent()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/Widget.cs", CompressibleSource);
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(
			[
				"analyze", workspace.Path,
				"--format", "json",
				"--compress-code",
				"--git-mode", "none",
				"--exclude", "none"
			],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		var root = document.RootElement;
		Assert.True(root.GetProperty("selection").GetProperty("compressCode").GetBoolean());
		var compression = root.GetProperty("compression");
		Assert.Equal(1, compression.GetProperty("compressedFiles").GetInt32());
		Assert.True(
			compression.GetProperty("transformedCharacters").GetInt64() <
			compression.GetProperty("sourceCharacters").GetInt64());
		Assert.True(
			root.GetProperty("metrics").GetProperty("content").GetProperty("chars").GetInt64() <
			CompressibleSource.Length);
		Assert.Empty(environment.StandardError);
	}

	[Theory]
	[InlineData(false, false, false, false, false, false)]
	[InlineData(true, false, false, false, false, true)]
	[InlineData(false, true, false, false, false, true)]
	[InlineData(false, false, true, false, false, true)]
	[InlineData(false, false, false, true, false, true)]
	[InlineData(false, false, false, false, true, true)]
	public void AnalyzeTransformationDetectionCoversEveryContentTransformation(
		bool hideSecrets,
		bool hidePrivateData,
		bool compressCode,
		bool stripComments,
		bool stripBlankLines,
		bool expected)
	{
		var selection = ProjectSelectionSpec.Standard with
		{
			HideSecrets = hideSecrets,
			HidePrivateData = hidePrivateData,
			CompressCode = compressCode,
			StripComments = stripComments,
			StripBlankLines = stripBlankLines
		};

		Assert.Equal(expected, AnalyzeCommandHandler.HasContentTransformations(selection));
	}

	[Fact]
	public async Task ContextExportWithCompressionWritesSignaturesInsteadOfMethodBodies()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/Widget.cs", CompressibleSource);
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(
			[
				"export", "context", workspace.Path,
				"--view", "content",
				"--format", "markdown",
				"--compress-code",
				"--git-mode", "none",
				"--exclude", "none",
				"-o", "-"
			],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains("public int Compute(int left, int right)", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("valueThatMustDisappear", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task AnalyzeWithStripCommentsReportsIndependentModeAndCounters()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/CommentedWidget.cs", CommentedSource);
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(
			[
				"analyze", workspace.Path,
				"--format", "json",
				"--strip-comments",
				"--git-mode", "none",
				"--exclude", "none",
				"--language", "en"
			],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		var root = document.RootElement;
		var selection = root.GetProperty("selection");
		Assert.False(selection.GetProperty("compressCode").GetBoolean());
		Assert.True(selection.GetProperty("stripComments").GetBoolean());
		var compression = root.GetProperty("compression");
		Assert.Equal(1, compression.GetProperty("compressedFiles").GetInt32());
		Assert.Equal(0, compression.GetProperty("bodyTransformedFiles").GetInt32());
		Assert.Equal(1, compression.GetProperty("commentTransformedFiles").GetInt32());
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task AnalyzeWithStripBlankLinesReportsIndependentModeAndCounters()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/BlankLineWidget.cs", BlankLineSource);
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(
			[
				"analyze", workspace.Path,
				"--format", "json",
				"--strip-blank-lines",
				"--git-mode", "none",
				"--exclude", "none",
				"--language", "en"
			],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		var root = document.RootElement;
		var selection = root.GetProperty("selection");
		Assert.False(selection.GetProperty("compressCode").GetBoolean());
		Assert.False(selection.GetProperty("stripComments").GetBoolean());
		Assert.True(selection.GetProperty("stripBlankLines").GetBoolean());
		var compression = root.GetProperty("compression");
		Assert.Equal(1, compression.GetProperty("compressedFiles").GetInt32());
		Assert.Equal(0, compression.GetProperty("bodyTransformedFiles").GetInt32());
		Assert.Equal(0, compression.GetProperty("commentTransformedFiles").GetInt32());
		Assert.Equal(1, compression.GetProperty("blankLineTransformedFiles").GetInt32());
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task AnalyzeTextKeepsBodyAndCommentFileCountersIndependent()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile(
			"src/BodyOnly.cs",
			"public static class BodyOnly { public static int Run() { var value = 40; return value + 2; } }");
		workspace.WriteFile(
			"src/CommentOnly.cs",
			"// documentation that is deliberately long enough to shrink\n" +
			"public static class CommentOnly { public const string Value = \"kept\"; }");
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(
			[
				"analyze", workspace.Path,
				"--compress-code",
				"--strip-comments",
				"--git-mode", "none",
				"--exclude", "none",
				"--language", "en"
			],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains("Compressed 1 of 2 files.", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("Removed comments from 1 of 2 files.", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ContextExportWithStripCommentsPreservesCodeAndOptionallyCompressesBodies(
		bool compress)
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/CommentedWidget.cs", CommentedSource);
		var environment = new TestTerminalEnvironment();
		var arguments = new List<string>
		{
			"export", "context", workspace.Path,
			"--view", "content",
			"--format", "markdown",
			"--strip-comments",
			"--git-mode", "none",
			"--exclude", "none",
			"-o", "-"
		};
		if (compress)
			arguments.Add("--compress-code");

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(arguments, TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.DoesNotContain("must disappear", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("public string Value()", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Equal(
			!compress,
			environment.StandardOutput.Contains(
				"// string content must remain",
				StringComparison.Ordinal));
		Assert.Equal(!compress, environment.StandardOutput.Contains("return marker;", StringComparison.Ordinal));
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task ContextExportWithStripCommentsTransformsMixedCommentOnlyLanguages()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile(
			"web/page.html",
			"<!-- html_remove -->\n<main>Ready</main>\n<script>// raw_js_keep\nwindow.ready = true;</script>\n");
		workspace.WriteFile(
			"web/site.css",
			"/* css_remove */\n.card { content: \"/* css_string_keep */\"; }\n");
		workspace.WriteFile(
			"config/app.toml",
			"# toml_remove\nname = \"api#toml_string_keep\"\n");
		workspace.WriteFile(
			"scripts/deploy.sh",
			"#!/usr/bin/env bash\n# bash_remove\nname='# bash_string_keep'\n");
		workspace.WriteFile(
			"markup/view.axaml",
			"<!-- xml_remove -->\n<Panel xmlns=\"https://github.com/avaloniaui\"><![CDATA[<!-- xml_cdata_keep -->]]></Panel>\n");
		workspace.WriteFile(
			"config/deployment.yaml",
			"# yaml_remove\nname: \"api#yaml_string_keep\"\nscript: |\n  # yaml_scalar_keep\n");
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(
			[
				"export", "context", workspace.Path,
				"--view", "content",
				"--format", "markdown",
				"--strip-comments",
				"--git-mode", "none",
				"--exclude", "none",
				"-o", "-"
			],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.DoesNotContain("html_remove", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("css_remove", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("toml_remove", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("bash_remove", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("xml_remove", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("yaml_remove", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("raw_js_keep", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("css_string_keep", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("toml_string_keep", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("#!/usr/bin/env bash", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("bash_string_keep", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("xml_cdata_keep", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("yaml_string_keep", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("yaml_scalar_keep", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task ContextExportWithStripBlankLinesPreservesMultilineLeafContent()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/BlankLineWidget.cs", BlankLineSource);
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(
			[
				"export", "context", workspace.Path,
				"--view", "content",
				"--format", "markdown",
				"--strip-blank-lines",
				"--git-mode", "none",
				"--exclude", "none",
				"-o", "-"
			],
				TestContext.Current.CancellationToken);

		var output = environment.StandardOutput.ReplaceLineEndings("\n");
		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.DoesNotContain("namespace Sample;\n\npublic sealed", output, StringComparison.Ordinal);
		Assert.DoesNotContain("BlankLineWidget\n{\n\n", output, StringComparison.Ordinal);
		Assert.Contains("first\n\nsecond", output, StringComparison.Ordinal);
		Assert.Contains("public string Value => Text;", output, StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Theory]
	[InlineData("src/service.rb", RubyCompressibleSource, "@root = root", "ruby_direct_cli_marker")]
	[InlineData("src/Service.php", PhpCompressibleSource, "$this->root = trim($root);", "$php_direct_cli_marker")]
	[InlineData("src/Service.scala", ScalaCompressibleSource, "val normalize: String => String", "scala_direct_cli_marker")]
	[InlineData("src/Service.kt", KotlinCompressibleSource, "val normalize: (String) -> String", "kotlin_direct_cli_marker")]
	public async Task ContextExportWithCompressionAppliesLanguageStateContracts(
		string relativePath,
		string source,
		string preservedState,
		string removedImplementation)
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile(relativePath, source);
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(
			[
				"export", "context", workspace.Path,
				"--view", "content",
				"--format", "markdown",
				"--compress-code",
				"--git-mode", "none",
				"--exclude", "none",
				"-o", "-"
			],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains(preservedState, environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain(removedImplementation, environment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Theory]
	[InlineData("folder")]
	[InlineData("zip")]
	public async Task ProjectExportWithCompressionWritesTransformedSource(string kind)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/Widget.cs", CompressibleSource);
		var destination = kind == "zip"
			? Path.Combine(workspace.Path, "compressed.zip")
			: Path.Combine(workspace.Path, "compressed");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunProjectExportAsync(
			project,
			destination,
			kind,
			environment,
			compress: true);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		string exported;
		if (kind == "zip")
		{
			using var archive = System.IO.Compression.ZipFile.OpenRead(destination);
			var entry = Assert.Single(archive.Entries, static candidate =>
				candidate.FullName.EndsWith("Widget.cs", StringComparison.Ordinal));
			using var reader = new StreamReader(entry.Open());
			exported = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
		}
		else
		{
			exported = await File.ReadAllTextAsync(
				Path.Combine(destination, "src", "Widget.cs"),
				TestContext.Current.CancellationToken);
		}

		Assert.Contains("public int Compute(int left, int right)", exported, StringComparison.Ordinal);
		Assert.DoesNotContain("valueThatMustDisappear", exported, StringComparison.Ordinal);
		Assert.Equal(Path.GetFullPath(destination) + Environment.NewLine, environment.StandardOutput);
	}

	[Theory]
	[InlineData("folder")]
	[InlineData("zip")]
	public async Task ProjectExportWithStripCommentsWritesTheSameTransformedBytes(string kind)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/CommentedWidget.cs", CommentedSource);
		var destination = kind == "zip"
			? Path.Combine(workspace.Path, "comments.zip")
			: Path.Combine(workspace.Path, "comments");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunProjectExportAsync(
			project,
			destination,
			kind,
			environment,
			stripComments: true);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		string exported;
		if (kind == "zip")
		{
			using var archive = System.IO.Compression.ZipFile.OpenRead(destination);
			var entry = Assert.Single(archive.Entries, static candidate =>
				candidate.FullName.EndsWith("CommentedWidget.cs", StringComparison.Ordinal));
			using var reader = new StreamReader(entry.Open());
			exported = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
		}
		else
		{
			exported = await File.ReadAllTextAsync(
				Path.Combine(destination, "src", "CommentedWidget.cs"),
				TestContext.Current.CancellationToken);
		}

		Assert.DoesNotContain("must disappear", exported, StringComparison.Ordinal);
		Assert.Contains("return marker;", exported, StringComparison.Ordinal);
		Assert.Contains("// string content must remain", exported, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("folder")]
	[InlineData("zip")]
	public async Task ProjectExportWithStripBlankLinesWritesTheSameTransformedBytes(string kind)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/BlankLineWidget.cs", BlankLineSource);
		var destination = kind == "zip"
			? Path.Combine(workspace.Path, "blank-lines.zip")
			: Path.Combine(workspace.Path, "blank-lines");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunProjectExportAsync(
			project,
			destination,
			kind,
			environment,
			stripBlankLines: true);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		string exported;
		if (kind == "zip")
		{
			using var archive = System.IO.Compression.ZipFile.OpenRead(destination);
			var entry = Assert.Single(archive.Entries, static candidate =>
				candidate.FullName.EndsWith("BlankLineWidget.cs", StringComparison.Ordinal));
			using var reader = new StreamReader(entry.Open());
			exported = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
		}
		else
		{
			exported = await File.ReadAllTextAsync(
				Path.Combine(destination, "src", "BlankLineWidget.cs"),
				TestContext.Current.CancellationToken);
		}

		exported = exported.ReplaceLineEndings("\n");
		Assert.DoesNotContain("namespace Sample;\n\npublic sealed", exported, StringComparison.Ordinal);
		Assert.Contains("first\n\nsecond", exported, StringComparison.Ordinal);
	}

	[Fact]
	public async Task StrictAnalyzeSucceedsForCleanProject()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/app.cs", "class App {}\n");
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(
			[
				"analyze", workspace.Path,
				"--format", "json",
				"--strict",
				"--git-mode", "none",
				"--exclude", "none"
			],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.Empty(document.RootElement.GetProperty("diagnostics").EnumerateArray());
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task StrictAnalyzeWritesReportBeforeReturningPolicyFailure()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/missing.cs", "class App {}\n");
		workspace.WriteFile("project/README.md", "# Project\n");
		var reportPath = Path.Combine(
			workspace.CreateDirectory("output"),
			"analysis.json");
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(
			[
				"analyze", project,
				"--format", "json",
				"--strict",
				"--select", "src/missing.cs",
				"--extension", "md",
				"--git-mode", "none",
				"--exclude", "none",
				"-o", reportPath
			],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.PolicyFailure, exitCode);
		Assert.True(File.Exists(reportPath));
		using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
		Assert.Contains(
			document.RootElement.GetProperty("diagnostics").EnumerateArray(),
			static diagnostic =>
				diagnostic.GetProperty("code").GetString() == "DPX-SELECTION-PATH-MISSING");
		Assert.Equal(Path.GetFullPath(reportPath), environment.StandardOutput.Trim());
		Assert.Contains("DPX-SELECTION-PATH-MISSING", environment.StandardError, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("tree", "text")]
	[InlineData("tree", "markdown")]
	[InlineData("tree", "json")]
	[InlineData("tree", "xml")]
	[InlineData("content", "text")]
	[InlineData("content", "markdown")]
	[InlineData("content", "json")]
	[InlineData("content", "xml")]
	[InlineData("tree-content", "text")]
	[InlineData("tree-content", "markdown")]
	[InlineData("tree-content", "json")]
	[InlineData("tree-content", "xml")]
	public async Task ExportContextSupportsEveryViewAndFormat(string view, string format)
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/app.cs", "class App {}\n");
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(
			[
				"export", "context", workspace.Path,
				"--view", view,
				"--format", format,
				"--git-mode", "none",
				"--exclude", "none",
				"-o", "-"
			],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardError);
		Assert.DoesNotContain("\u001b", environment.StandardOutput, StringComparison.Ordinal);
		if (format == "json")
		{
			using var json = JsonDocument.Parse(environment.StandardOutput);
			Assert.Equal("devprojex-context", json.RootElement.GetProperty("kind").GetString());
		}
		else if (format == "xml")
		{
			var document = System.Xml.Linq.XDocument.Parse(environment.StandardOutput);
			Assert.Equal("devprojexContext", document.Root!.Name.LocalName);
		}
	}

	[Fact]
	public async Task ExportContextFileConflictsWithoutForceAndReplacesWithForce()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/app.cs", "class App {}\n");
		var destination = workspace.WriteFile("outside/context.md", "old");

		var conflictEnvironment = new TestTerminalEnvironment();
		var conflict = await RunContextExportAsync(
			workspace,
			project,
			destination,
			conflictEnvironment,
			force: false);
		Assert.Equal(CommandLineExitCodes.DestinationConflict, conflict);
		Assert.Equal("old", File.ReadAllText(destination));

		var forceEnvironment = new TestTerminalEnvironment();
		var success = await RunContextExportAsync(
			workspace,
			project,
			destination,
			forceEnvironment,
			force: true);
		Assert.Equal(CommandLineExitCodes.Success, success);
		Assert.StartsWith("# ", File.ReadAllText(destination), StringComparison.Ordinal);
		Assert.Equal(System.IO.Path.GetFullPath(destination) + Environment.NewLine, forceEnvironment.StandardOutput);
	}

	[Fact]
	public async Task ExactProjectFolderAndZipDestinationsAreCreatedWithoutAutomaticSuffix()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(System.IO.Path.Combine(project, "app.bin"), "\0binary");
		var outputParent = workspace.CreateDirectory("output");
		var folder = System.IO.Path.Combine(outputParent, "submission");
		var zip = System.IO.Path.Combine(outputParent, "submission.zip");

		var folderEnvironment = new TestTerminalEnvironment();
		var folderExit = await RunProjectExportAsync(project, folder, "folder", folderEnvironment);
		Assert.Equal(CommandLineExitCodes.Success, folderExit);
		Assert.True(File.Exists(System.IO.Path.Combine(folder, "app.bin")));
		Assert.False(Directory.Exists(folder + "-copy"));

		var zipEnvironment = new TestTerminalEnvironment();
		var zipExit = await RunProjectExportAsync(project, zip, "zip", zipEnvironment);
		Assert.Equal(CommandLineExitCodes.Success, zipExit);
		Assert.True(File.Exists(zip));
		Assert.Equal(System.IO.Path.GetFullPath(zip) + Environment.NewLine, zipEnvironment.StandardOutput);
	}

	[Fact]
	public async Task ExistingExactProjectDestinationReturnsExitCodeFour()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(System.IO.Path.Combine(project, "app.txt"), "content");
		var destination = workspace.CreateDirectory("submission");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunProjectExportAsync(project, destination, "folder", environment);

		Assert.Equal(CommandLineExitCodes.DestinationConflict, exitCode);
		Assert.Contains("DPX-EXPORT-DESTINATION-EXISTS", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AnalyzeDryRunIsRejectedWithoutCreatingDestination()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("app.cs", "class App {}\n");
		var destination = System.IO.Path.Combine(workspace.Path, "reports", "analysis.json");
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(
			[
				"analyze", workspace.Path,
				"--format", "json",
				"--git-mode", "none",
				"--exclude", "none",
				"--dry-run",
				"-o", destination
			],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.False(File.Exists(destination));
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("--dry-run", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ContextDryRunResolvesDestinationWithoutCreatingIt()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/app.cs", "class App {}\n");
		var outputParent = workspace.CreateDirectory("output");
		var destination = System.IO.Path.Combine(outputParent, "context.md");
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(
			[
				"export", "context", project,
				"--git-mode", "none",
				"--exclude", "none",
				"--dry-run",
				"-o", destination
			],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.False(File.Exists(destination));
		Assert.Empty(environment.StandardOutput);
		Assert.Contains(
			System.IO.Path.GetFullPath(destination),
			environment.StandardError,
			StringComparison.Ordinal);
		Assert.Empty(Directory.EnumerateFileSystemEntries(outputParent));
	}

	[Fact]
	public async Task ContextDryRunRejectsExistingAndUnsafeDestinationsBeforeWriting()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/app.cs", "class App {}\n");
		var existing = workspace.WriteFile("output/context.md", "existing");

		var conflictEnvironment = new TestTerminalEnvironment();
		var conflictExit = await new TerminalApplication(
				conflictEnvironment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(
			[
				"export", "context", project,
				"--git-mode", "none",
				"--exclude", "none",
				"--dry-run",
				"-o", existing
			],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.DestinationConflict, conflictExit);
		Assert.Equal("existing", File.ReadAllText(existing));
		Assert.Empty(conflictEnvironment.StandardOutput);

		var unsafeDestination = Path.Combine(project, "context.md");
		var unsafeEnvironment = new TestTerminalEnvironment();
		var unsafeExit = await new TerminalApplication(
				unsafeEnvironment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(
			[
				"export", "context", project,
				"--git-mode", "none",
				"--exclude", "none",
				"--dry-run",
				"-o", unsafeDestination
			],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.PolicyFailure, unsafeExit);
		Assert.Contains(
			"DPX-EXPORT-UNSAFE-DESTINATION",
			unsafeEnvironment.StandardError,
			StringComparison.Ordinal);
		Assert.False(File.Exists(unsafeDestination));
		Assert.Empty(unsafeEnvironment.StandardOutput);
	}

	private static async Task<int> RunContextExportAsync(
		TemporaryDirectory workspace,
		string project,
		string destination,
		TestTerminalEnvironment environment,
		bool force)
	{
		var arguments = new List<string>
		{
			"export", "context", project,
			"--git-mode", "none",
			"--exclude", "none",
			"-o", destination
		};
		if (force)
			arguments.Add("--force");
		return await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(arguments, TestContext.Current.CancellationToken);
	}

	private static Task<int> RunProjectExportAsync(
		string project,
		string output,
		string kind,
		TestTerminalEnvironment environment,
		bool compress = false,
		bool stripComments = false,
		bool stripBlankLines = false)
	{
		var arguments = new List<string>
		{
				"export", "project", project,
				"--as", kind,
				"-o", output,
				"--git-mode", "none",
				"--exclude", "none",
				"--progress", "never"
		};
		if (compress)
			arguments.Add("--compress-code");
		if (stripComments)
			arguments.Add("--strip-comments");
		if (stripBlankLines)
			arguments.Add("--strip-blank-lines");
		return new TerminalApplication(environment, new TerminalServiceFactory())
			.RunAsync(arguments, TestContext.Current.CancellationToken);
	}
}
