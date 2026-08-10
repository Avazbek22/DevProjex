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
				"--compress",
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
				"--compress",
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

	[Theory]
	[InlineData("src/service.rb", RubyCompressibleSource, "@root = root", "ruby_direct_cli_marker")]
	[InlineData("src/Service.php", PhpCompressibleSource, "$this->root = trim($root);", "$php_direct_cli_marker")]
	public async Task ContextExportWithCompressionAppliesRubyAndPhpStateContracts(
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
				"--compress",
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
		workspace.WriteFile("project/src/app.cs", "class App {}\n");
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
				"--select", "missing.cs",
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
		bool compress = false)
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
			arguments.Add("--compress");
		return new TerminalApplication(environment, new TerminalServiceFactory())
			.RunAsync(arguments, TestContext.Current.CancellationToken);
	}
}
