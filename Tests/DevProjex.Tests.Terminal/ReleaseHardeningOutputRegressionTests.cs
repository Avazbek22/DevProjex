using DevProjex.Terminal.Rendering;

namespace DevProjex.Tests.Terminal;

public sealed class ReleaseHardeningOutputRegressionTests
{
	private static readonly char[] ForbiddenPlainCharacters =
	[
		'\u001b',
		'╭',
		'╮',
		'╰',
		'╯',
		'│',
		'─',
		'├',
		'└'
	];

	[Fact]
	public async Task AnalyzePlainTextIsIdenticalForStdoutAndFileOutput()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/app.cs", "class App {}\n");
		var destination = Path.Combine(workspace.Path, "output", "analysis.txt");
		var appData = workspace.CreateDirectory("app-data");

		var stdoutEnvironment = new TestTerminalEnvironment();
		var stdoutExitCode = await RunAsync(
			appData,
			stdoutEnvironment,
			"analyze", project,
			"--format", "text",
			"--plain",
			"--progress", "never",
			"--git-mode", "none",
			"--exclude", "none",
			"-o", "-");

		var fileEnvironment = new TestTerminalEnvironment();
		var fileExitCode = await RunAsync(
			appData,
			fileEnvironment,
			"analyze", project,
			"--format", "text",
			"--plain",
			"--progress", "never",
			"--git-mode", "none",
			"--exclude", "none",
			"-o", destination);

		Assert.Equal(CommandLineExitCodes.Success, stdoutExitCode);
		Assert.Equal(CommandLineExitCodes.Success, fileExitCode);
		Assert.True(File.Exists(destination));
		Assert.Equal(
			NormalizeFinalNewline(await File.ReadAllTextAsync(
				destination,
				TestContext.Current.CancellationToken)),
			NormalizeFinalNewline(stdoutEnvironment.StandardOutput));
		Assert.DoesNotContain(
			stdoutEnvironment.StandardOutput,
			static character => ForbiddenPlainCharacters.Contains(character));
		Assert.Equal(Path.GetFullPath(destination) + Environment.NewLine, fileEnvironment.StandardOutput);
		Assert.Empty(stdoutEnvironment.StandardError);
		Assert.Empty(fileEnvironment.StandardError);
	}

	[Fact]
	public async Task AnalyzeExistingDestinationReturnsConflictAndPreservesFile()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/app.cs", "class App {}\n");
		var destination = workspace.WriteFile("output/analysis.json", "sentinel");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace.CreateDirectory("app-data"),
			environment,
			"analyze", project,
			"--format", "json",
			"--git-mode", "none",
			"--exclude", "none",
			"-o", destination);

		Assert.Equal(CommandLineExitCodes.DestinationConflict, exitCode);
		Assert.Equal("sentinel", await File.ReadAllTextAsync(
			destination,
			TestContext.Current.CancellationToken));
		Assert.Empty(environment.StandardOutput);
		Assert.Contains(
			"DPX-EXPORT-DESTINATION-EXISTS",
			environment.StandardError,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task AnalyzeDestinationInsideSourceReturnsPolicyFailureWithoutEffects()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = workspace.WriteFile("project/app.cs", "class App {}\n");
		var destination = Path.Combine(project, "generated", "analysis.txt");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace.CreateDirectory("app-data"),
			environment,
			"analyze", project,
			"--format", "text",
			"--plain",
			"--git-mode", "none",
			"--exclude", "none",
			"-o", destination);

		Assert.Equal(CommandLineExitCodes.PolicyFailure, exitCode);
		Assert.Equal("class App {}\n", await File.ReadAllTextAsync(
			source,
			TestContext.Current.CancellationToken));
		Assert.False(File.Exists(destination));
		Assert.False(Directory.Exists(Path.GetDirectoryName(destination)!));
		Assert.Empty(environment.StandardOutput);
		Assert.Contains(
			"DPX-EXPORT-UNSAFE-DESTINATION",
			environment.StandardError,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task ContextFileDryRunWritesPlanOnlyToStderrAndCreatesNothing()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = workspace.WriteFile("project/app.cs", "class App {}\n");
		var destination = Path.Combine(workspace.Path, "output", "context.md");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace.CreateDirectory("app-data"),
			environment,
			"export", "context", project,
			"--format", "markdown",
			"--git-mode", "none",
			"--exclude", "none",
			"--dry-run",
			"-o", destination);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains(
			Path.GetFullPath(destination),
			environment.StandardError,
			StringComparison.Ordinal);
		Assert.False(File.Exists(destination));
		Assert.False(Directory.Exists(Path.GetDirectoryName(destination)!));
		Assert.Equal("class App {}\n", await File.ReadAllTextAsync(
			source,
			TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task ContextStdoutDryRunDoesNotGenerateDocument()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = workspace.WriteFile("project/app.cs", "class App {}\n");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace.CreateDirectory("app-data"),
			environment,
			"export", "context", project,
			"--format", "json",
			"--git-mode", "none",
			"--exclude", "none",
			"--dry-run",
			"-o", "-");

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.NotEmpty(environment.StandardError);
		Assert.Equal("class App {}\n", await File.ReadAllTextAsync(
			source,
			TestContext.Current.CancellationToken));
	}

	[Theory]
	[InlineData("folder", "submission")]
	[InlineData("zip", "submission.zip")]
	public async Task ProjectDryRunWritesPlanOnlyToStderrAndCreatesNothing(
		string outputKind,
		string destinationName)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = workspace.WriteFile("project/app.cs", "class App {}\n");
		var destination = Path.Combine(workspace.Path, "output", destinationName);
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace.CreateDirectory("app-data"),
			environment,
			"export", "project", project,
			"--as", outputKind,
			"--git-mode", "none",
			"--exclude", "none",
			"--dry-run",
			"-o", destination);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains(
			Path.GetFullPath(destination),
			environment.StandardError,
			StringComparison.Ordinal);
		Assert.False(File.Exists(destination));
		Assert.False(Directory.Exists(destination));
		Assert.False(Directory.Exists(Path.GetDirectoryName(destination)!));
		Assert.Equal("class App {}\n", await File.ReadAllTextAsync(
			source,
			TestContext.Current.CancellationToken));
	}

	[Theory]
	[InlineData("context")]
	[InlineData("project")]
	public async Task QuietDryRunKeepsOnlyTheRequestedPreflightPlan(string command)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/app.cs", "class App {}\n");
		var destination = command == "context"
			? Path.Combine(workspace.Path, "output", "context.md")
			: Path.Combine(workspace.Path, "output", "submission");
		var environment = new TestTerminalEnvironment();
		var arguments = command == "context"
			? new[]
			{
				"export", "context", project,
				"--format", "markdown",
				"--git-mode", "none",
				"--exclude", "none",
				"--dry-run",
				"--verbosity", "quiet",
				"--progress", "always",
				"-o", destination
			}
			: new[]
			{
				"export", "project", project,
				"--as", "folder",
				"--git-mode", "none",
				"--exclude", "none",
				"--dry-run",
				"--verbosity", "quiet",
				"--progress", "always",
				"-o", destination
			};

		var exitCode = await RunAsync(
			workspace.CreateDirectory("app-data"),
			environment,
			arguments);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardOutput);
		var lines = environment.StandardError
			.Split(
				["\r\n", "\n"],
				StringSplitOptions.RemoveEmptyEntries);
		var line = Assert.Single(lines);
		Assert.Contains(Path.GetFullPath(destination), line, StringComparison.Ordinal);
		Assert.False(File.Exists(destination));
		Assert.False(Directory.Exists(destination));
	}

	[Theory]
	[InlineData("normal", true)]
	[InlineData("quiet", false)]
	[InlineData("minimal", false)]
	public async Task PlainExplicitProgressUsesBoundedStaticStderrLines(
		string verbosity,
		bool expectProgress)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		for (var index = 0; index < 24; index++)
		{
			workspace.WriteFile(
				$"project/src/file-{index:D2}.txt",
				new string((char)('a' + index % 26), 16 * 1024));
		}
		var destination = Path.Combine(workspace.Path, "output", "submission");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace.CreateDirectory("app-data"),
			environment,
			"export", "project", project,
			"--as", "folder",
			"--git-mode", "none",
			"--exclude", "none",
			"--plain",
			"--progress", "always",
			"--verbosity", verbosity,
			"-o", destination);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(
			Path.GetFullPath(destination) + Environment.NewLine,
			environment.StandardOutput);
		if (!expectProgress)
		{
			Assert.Empty(environment.StandardError);
			return;
		}

		var lines = environment.StandardError.Split(
			["\r\n", "\n"],
			StringSplitOptions.RemoveEmptyEntries);
		Assert.InRange(lines.Length, 2, 13);
		Assert.Contains(lines, static line => line.Contains('%', StringComparison.Ordinal));
		Assert.All(
			lines,
			static line =>
			{
				Assert.DoesNotContain('\u001b', line);
				Assert.DoesNotContain(
					line,
					static character => ForbiddenPlainCharacters.Contains(character));
			});
	}

	[Theory]
	[InlineData("analyze", 1)]
	[InlineData("context", 2)]
	public async Task PlainExplicitProgressIsObservableForAnalysisAndContext(
		string command,
		int expectedStatusLines)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/app.cs", "class App {}\n");
		var environment = new TestTerminalEnvironment();
		var arguments = command == "analyze"
			? new[]
			{
				"analyze", project,
				"--format", "json",
				"--git-mode", "none",
				"--exclude", "none",
				"--plain",
				"--progress", "always",
				"-o", "-"
			}
			: new[]
			{
				"export", "context", project,
				"--format", "json",
				"--git-mode", "none",
				"--exclude", "none",
				"--plain",
				"--progress", "always",
				"-o", "-"
			};

		var exitCode = await RunAsync(
			workspace.CreateDirectory("app-data"),
			environment,
			arguments);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
		var lines = environment.StandardError.Split(
			["\r\n", "\n"],
			StringSplitOptions.RemoveEmptyEntries);
		Assert.Equal(expectedStatusLines, lines.Length);
		Assert.All(
			lines,
			static line =>
			{
				Assert.DoesNotContain('\u001b', line);
				Assert.DoesNotContain(
					line,
					static character => ForbiddenPlainCharacters.Contains(character));
			});
	}

	[Fact]
	public async Task ContextForceIsRejectedForStdoutDestination()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/app.cs", "class App {}\n");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace.CreateDirectory("app-data"),
			environment,
			"export", "context", project,
			"--format", "json",
			"--git-mode", "none",
			"--exclude", "none",
			"--force",
			"-o", "-");

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("error[DPX-", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task PlainHumanAnalysisContainsAsciiOnly()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/app.cs", "class App {}\n");
		var environment = new TestTerminalEnvironment
		{
			IsOutputInteractive = true,
			IsErrorInteractive = true
		};

		var exitCode = await RunAsync(
			workspace.CreateDirectory("app-data"),
			environment,
			"analyze", project,
			"--format", "text",
			"--plain",
			"--git-mode", "none",
			"--exclude", "none",
			"-o", "-");

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.NotEmpty(environment.StandardOutput);
		Assert.DoesNotContain(
			environment.StandardOutput,
			static character => ForbiddenPlainCharacters.Contains(character));
		Assert.Empty(environment.StandardError);
	}

	[Theory]
	[InlineData("text", false)]
	[InlineData("text", true)]
	[InlineData("markdown", false)]
	[InlineData("markdown", true)]
	public async Task ContextPlainTreeUsesStrictAsciiForStdoutAndFile(
		string format,
		bool writeToFile)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/nested/app.cs", "class App {}\n");
		var destination = Path.Combine(
			workspace.Path,
			"output",
			format == "text" ? "context.txt" : "context.md");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace.CreateDirectory("app-data"),
			environment,
			"export", "context", project,
			"--view", "tree-content",
			"--format", format,
			"--plain",
			"--progress", "never",
			"--git-mode", "none",
			"--exclude", "none",
			"-o", writeToFile ? destination : "-");

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		var payload = writeToFile
			? await File.ReadAllTextAsync(
				destination,
				TestContext.Current.CancellationToken)
			: environment.StandardOutput;
		Assert.Contains("|--", payload, StringComparison.Ordinal);
		Assert.DoesNotContain(
			payload,
			static character => ForbiddenPlainCharacters.Contains(character));
		Assert.DoesNotContain('\u001b', payload);
		Assert.Empty(environment.StandardError);
		if (writeToFile)
		{
			Assert.Equal(
				Path.GetFullPath(destination) + Environment.NewLine,
				environment.StandardOutput);
		}
	}

	[Fact]
	public void ExplicitColorAlwaysOverridesNoColorEnvironmentDefault()
	{
		var environment = new TestTerminalEnvironment
		{
			IsNoColor = true,
			IsOutputInteractive = false
		};

		var capabilities = TerminalCapabilities.Resolve(
			environment,
			new TerminalOutputOptions(Color: TerminalColorMode.Always),
			forStandardError: false);

		Assert.True(capabilities.UseAnsi);
	}

	[Fact]
	public async Task PlainAndColorAlwaysAreRejectedAsConflictingOptions()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/app.cs", "class App {}\n");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace.CreateDirectory("app-data"),
			environment,
			"analyze", project,
			"--format", "text",
			"--plain",
			"--color", "always",
			"--git-mode", "none",
			"--exclude", "none",
			"-o", "-");

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("error[DPX-", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ContextJsonUsesIntegerSchemaVersion()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/app.cs", "class App {}\n");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace.CreateDirectory("app-data"),
			environment,
			"export", "context", project,
			"--format", "json",
			"--plain",
			"--git-mode", "none",
			"--exclude", "none",
			"-o", "-");

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		var schemaVersion = document.RootElement.GetProperty("schemaVersion");
		Assert.Equal(JsonValueKind.Number, schemaVersion.ValueKind);
		Assert.Equal(1, schemaVersion.GetInt32());
		Assert.Empty(environment.StandardError);
	}

	private static Task<int> RunAsync(
		string appData,
		TestTerminalEnvironment environment,
		params string[] arguments) =>
		new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => appData))
			.RunAsync(arguments, TestContext.Current.CancellationToken);

	private static string NormalizeFinalNewline(string value) =>
		value.TrimEnd('\r', '\n');
}
