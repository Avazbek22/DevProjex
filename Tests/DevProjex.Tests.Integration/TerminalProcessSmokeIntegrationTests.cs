using System.IO.Compression;
using System.Xml.Linq;

namespace DevProjex.Tests.Integration;

public sealed class TerminalProcessSmokeIntegrationTests
{
	[Fact]
	public async Task HelpVersionAndLegacyMigrationRunThroughTheRealEntryPoint()
	{
		var help = await RunAsync(["--help", "--language", "en"]);
		Assert.Equal(CommandLineExitCodes.Success, help.ExitCode);
		Assert.Contains("USAGE", help.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(help.StandardError);

		var version = await RunAsync(["--version"]);
		Assert.Equal(CommandLineExitCodes.Success, version.ExitCode);
		Assert.Matches(@"^\d+\.\d+(?:\.\d+)?$", version.StandardOutput.Trim());
		Assert.Empty(version.StandardError);

		var shortVersion = await RunAsync(["-v"]);
		Assert.Equal(CommandLineExitCodes.Success, shortVersion.ExitCode);
		Assert.Equal(version.StandardOutput, shortVersion.StandardOutput);
		Assert.Empty(shortVersion.StandardError);

		var malformedVersion = await RunAsync(["-version"]);
		Assert.Equal(CommandLineExitCodes.UsageError, malformedVersion.ExitCode);
		Assert.Empty(malformedVersion.StandardOutput);
		Assert.Equal(
			2,
			malformedVersion.StandardError
				.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
				.Length);
		Assert.Contains(
			"error[DPX-CLI-UNKNOWN-OPTION]",
			malformedVersion.StandardError,
			StringComparison.Ordinal);
		Assert.Contains(
			"devprojex --version",
			malformedVersion.StandardError,
			StringComparison.Ordinal);

		var legacy = await RunAsync(["--path", ".", "--report", "-"]);
		Assert.Equal(CommandLineExitCodes.UsageError, legacy.ExitCode);
		Assert.Empty(legacy.StandardOutput);
		Assert.Contains("DPX-CLI-LEGACY-SYNTAX", legacy.StandardError, StringComparison.Ordinal);
		Assert.Equal(
			["devprojex", "analyze", ".", "--format", "json", "-o", "-"],
			ReadArgumentVector(legacy.StandardError));
	}

	private static string[] ReadArgumentVector(string output) =>
		output
			.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
			.Select(static line => line.Trim())
			.Where(static line => line.StartsWith("argv[", StringComparison.Ordinal))
			.Select(static line =>
			{
				var separator = line.IndexOf(" = ", StringComparison.Ordinal);
				Assert.True(separator > 0, $"Malformed argument-vector line: {line}");
				return JsonSerializer.Deserialize<string>(line[(separator + 3)..])!;
			})
			.ToArray();

	[Fact]
	public async Task AnalyzeAndContextStdoutRemainPureMachineDocuments()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("source");
		Directory.CreateDirectory(Path.Combine(project, "src"));
		File.WriteAllText(
			Path.Combine(project, "src", "App.cs"),
			"class App {}\n",
			new UTF8Encoding(false));

		var analysis = await RunAsync(
		[
			"analyze",
			project,
			"--format",
			"json",
			"--git-mode",
			"none",
			"--exclude",
			"none",
			"--language",
			"en"
		]);
		Assert.Equal(CommandLineExitCodes.Success, analysis.ExitCode);
		using (var document = JsonDocument.Parse(analysis.StandardOutput))
		{
			Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
			Assert.Equal(1, document.RootElement.GetProperty("inventory").GetProperty("files").GetInt32());
		}
		Assert.DoesNotContain("\u001b", analysis.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(analysis.StandardError);

		var context = await RunAsync(
		[
			"export",
			"context",
			project,
			"--view",
			"tree-content",
			"--format",
			"json",
			"--output",
			"-",
			"--git-mode",
			"none",
			"--exclude",
			"none",
			"--progress",
			"never",
			"--language",
			"en"
		]);
		Assert.Equal(CommandLineExitCodes.Success, context.ExitCode);
		using (var document = JsonDocument.Parse(context.StandardOutput))
		{
			Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
			Assert.Equal("devprojex-context", document.RootElement.GetProperty("kind").GetString());
			Assert.Contains(
				document.RootElement.GetProperty("files").EnumerateArray(),
				static file => file.GetProperty("path").GetString() == "src/App.cs");
		}
		Assert.DoesNotContain("\u001b", context.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(context.StandardError);
	}

	[Fact]
	public async Task ContextTokenBudgetRunsThroughTheRealEntryPoint()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("token-budget-source");
		File.WriteAllText(Path.Combine(project, "A-large.txt"), new string('a', 100));
		File.WriteAllText(Path.Combine(project, "B-small.txt"), "b");

		var context = await RunAsync(
		[
			"export", "context", project,
			"--view", "tree-content",
			"--format", "json",
			"--output", "-",
			"--max-tokens", "1",
			"--git-mode", "none",
			"--exclude", "none",
			"--progress", "never",
			"--language", "en"
		]);

		Assert.Equal(CommandLineExitCodes.Success, context.ExitCode);
		using var document = JsonDocument.Parse(context.StandardOutput);
		var file = Assert.Single(document.RootElement.GetProperty("files").EnumerateArray());
		Assert.Equal("B-small.txt", file.GetProperty("path").GetString());
		var budget = document.RootElement.GetProperty("tokenBudget");
		Assert.Equal(1, budget.GetProperty("includedFiles").GetInt32());
		Assert.Equal(1, budget.GetProperty("skippedFiles").GetInt32());
		Assert.Contains("Estimated token budget: 1.", context.StandardError, StringComparison.Ordinal);
		Assert.Contains("A-large.txt", context.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain("\u001b", context.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("\u001b", context.StandardError, StringComparison.Ordinal);

		var outputPath = Path.Combine(workspace.Path, "budget.xml");
		var fileContext = await RunAsync(
		[
			"export", "context", project,
			"--view", "content",
			"--format", "xml",
			"--output", outputPath,
			"--max-tokens", "1",
			"--git-mode", "none",
			"--exclude", "none",
			"--progress", "never",
			"--language", "en"
		]);

		Assert.Equal(CommandLineExitCodes.Success, fileContext.ExitCode);
		Assert.Equal(outputPath, fileContext.StandardOutput.TrimEnd('\r', '\n'));
		var fileDocument = XDocument.Load(outputPath);
		var fileEntry = Assert.Single(fileDocument.Root!.Element("files")!.Elements("file"));
		Assert.Equal(
			PathUtility.NormalizeSeparators(Path.Combine(project, "B-small.txt")),
			fileEntry.Attribute("path")?.Value);
		var tokenBudget = fileDocument.Root.Element("tokenBudget")!;
		Assert.Equal("1", tokenBudget.Element("includedFiles")?.Value);
		Assert.Equal(
			PathUtility.NormalizeSeparators(Path.Combine(project, "A-large.txt")),
			tokenBudget.Element("largestSkippedFiles")?.Element("file")?.Attribute("path")?.Value);
		Assert.Contains("Estimated token budget: 1.", fileContext.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain("\u001b", fileContext.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ContextExportReportsFifoAsUnreadableWithoutBlocking()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("Windows does not expose POSIX FIFO entries.");

		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("fifo-source");
		var fifoPath = Path.Combine(project, "events.txt");
		await CreateFifoAsync(fifoPath);

		var context = await RunAsync(
		[
			"export", "context", project,
			"--view", "content",
			"--format", "json",
			"--output", "-",
			"--git-mode", "none",
			"--exclude", "none",
			"--hide-secrets",
			"--progress", "never",
			"--language", "en"
		]);

		Assert.Equal(CommandLineExitCodes.Success, context.ExitCode);
		using var document = JsonDocument.Parse(context.StandardOutput);
		var file = Assert.Single(document.RootElement.GetProperty("files").EnumerateArray());
		Assert.Equal(fifoPath, file.GetProperty("path").GetString());
		Assert.Equal("unreadable", file.GetProperty("classification").GetString());
		Assert.Equal(JsonValueKind.Null, file.GetProperty("content").ValueKind);
		Assert.Contains("File could not be read.", context.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ProjectExportRejectsFifoWithoutBlocking()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("Windows does not expose POSIX FIFO entries.");

		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("fifo-copy-source");
		await CreateFifoAsync(Path.Combine(project, "events.txt"));
		var destination = Path.Combine(workspace.Path, "copy");

		var context = await RunAsync(
		[
			"export", "project", project,
			"--as", "folder",
			"--output", destination,
			"--git-mode", "none",
			"--exclude", "none",
			"--progress", "never",
			"--language", "en"
		]);

		Assert.Equal(CommandLineExitCodes.RuntimeError, context.ExitCode);
		Assert.Contains("DPX-EXPORT-SOURCE-UNAVAILABLE", context.StandardError, StringComparison.Ordinal);
		Assert.False(Path.Exists(destination));
	}

	[Fact]
	public async Task ContextExportDoesNotOpenFifoGitIgnore()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("Windows does not expose POSIX FIFO entries.");

		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("fifo-gitignore-source");
		await CreateFifoAsync(Path.Combine(project, ".gitignore"));
		workspace.CreateFile("fifo-gitignore-source/App.cs", "class App {}");

		var context = await RunAsync(
		[
			"export", "context", project,
			"--view", "content",
			"--format", "json",
			"--output", "-",
			"--git-mode", "gitignore",
			"--exclude", "none",
			"--progress", "never",
			"--language", "en"
		]);

		Assert.Equal(CommandLineExitCodes.Success, context.ExitCode);
		using var document = JsonDocument.Parse(context.StandardOutput);
		Assert.Equal("devprojex-context", document.RootElement.GetProperty("kind").GetString());
		Assert.Contains("DPX-PROJECT-PARTIAL-ACCESS", context.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ParseFailuresKeepSpecificMachineCategoriesThroughTheRealEntryPoint()
	{
		var unknownCommand = await RunAsync(["analze", "--language", "en"]);
		Assert.Equal(CommandLineExitCodes.UsageError, unknownCommand.ExitCode);
		Assert.Empty(unknownCommand.StandardOutput);
		Assert.Contains(
			"error[DPX-CLI-UNKNOWN-COMMAND]",
			unknownCommand.StandardError,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"DPX-CLI-INVALID-SYNTAX",
			unknownCommand.StandardError,
			StringComparison.Ordinal);

		var unknownOption = await RunAsync(
			["analyze", ".", "--formt", "json", "--language", "en"]);
		Assert.Equal(CommandLineExitCodes.UsageError, unknownOption.ExitCode);
		Assert.Empty(unknownOption.StandardOutput);
		Assert.Equal(
			2,
			unknownOption.StandardError
				.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
				.Length);
		Assert.Contains(
			"error[DPX-CLI-UNKNOWN-OPTION]",
			unknownOption.StandardError,
			StringComparison.Ordinal);

		var delimiterData = await RunAsync(
			["--language", "en", "analyze", ".", "--", "--fornat"]);
		Assert.Equal(CommandLineExitCodes.UsageError, delimiterData.ExitCode);
		Assert.Empty(delimiterData.StandardOutput);
		Assert.Contains(
			"error[DPX-CLI-INVALID-SYNTAX]",
			delimiterData.StandardError,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"DPX-CLI-UNKNOWN-OPTION",
			delimiterData.StandardError,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"devprojex analyze --format",
			delimiterData.StandardError,
			StringComparison.Ordinal);

		var missingValue = await RunAsync(
			["export", "project", "--as", "--language", "en"]);
		Assert.Equal(CommandLineExitCodes.UsageError, missingValue.ExitCode);
		Assert.Empty(missingValue.StandardOutput);
		Assert.Contains(
			"error[DPX-CLI-MISSING-VALUE]",
			missingValue.StandardError,
			StringComparison.Ordinal);

		var invalidValue = await RunAsync(
			["analyze", ".", "--format", "yaml", "--language", "en"]);
		Assert.Equal(CommandLineExitCodes.UsageError, invalidValue.ExitCode);
		Assert.Empty(invalidValue.StandardOutput);
		Assert.Contains(
			"error[DPX-CLI-INVALID-VALUE]",
			invalidValue.StandardError,
			StringComparison.Ordinal);

		var conflict = await RunAsync(
		[
			"analyze",
			".",
			"--exclude",
			"none",
			"--exclude",
			"smart-ignore",
			"--language",
			"en"
		]);
		Assert.Equal(CommandLineExitCodes.UsageError, conflict.ExitCode);
		Assert.Empty(conflict.StandardOutput);
		Assert.Contains(
			"error[DPX-CLI-INVALID-VALUE]",
			conflict.StandardError,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task EmptyInlineAssignmentCannotConsumeHelpOrCreateAnArtifact()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("source");
		File.WriteAllText(
			Path.Combine(project, "App.cs"),
			"class App {}\n",
			new UTF8Encoding(false));
		var accidentalArtifact = Path.Combine(workspace.Path, "--help");

		var result = await RunAsync(
			[
				"profile", "export", project,
				"--profile", "standard",
				"--output=",
				"--help",
				"--language", "en"
			],
			workspace.Path);

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Empty(result.StandardOutput);
		Assert.Contains(
			"error[DPX-CLI-MISSING-VALUE]",
			result.StandardError,
			StringComparison.Ordinal);
		Assert.False(File.Exists(accidentalArtifact));
		Assert.Equal(
			["App.cs"],
			Directory.EnumerateFiles(project, "*", SearchOption.AllDirectories)
				.Select(path => Path.GetRelativePath(project, path))
				.ToArray());

		var explicitEmptyValue = await RunAsync(
			[
				"profile", "export", project,
				"--profile", "standard",
				"--output", "",
				"--help",
				"--language", "en"
			],
			workspace.Path);
		Assert.Equal(CommandLineExitCodes.UsageError, explicitEmptyValue.ExitCode);
		Assert.Empty(explicitEmptyValue.StandardOutput);
		Assert.Contains(
			"error[DPX-CLI-MISSING-VALUE]",
			explicitEmptyValue.StandardError,
			StringComparison.Ordinal);

		var emptyFlagAssignment = await RunAsync(
			[
				"analyze", project,
				"--plain=",
				"--help",
				"--language", "en"
			],
			workspace.Path);
		Assert.Equal(CommandLineExitCodes.UsageError, emptyFlagAssignment.ExitCode);
		Assert.Empty(emptyFlagAssignment.StandardOutput);
		Assert.Contains(
			"error[DPX-CLI-INVALID-SYNTAX]",
			emptyFlagAssignment.StandardError,
			StringComparison.Ordinal);

		var emptyProjectArgument = await RunAsync(
			["analyze", "", "--help", "--language", "en"],
			workspace.Path);
		Assert.Equal(CommandLineExitCodes.UsageError, emptyProjectArgument.ExitCode);
		Assert.Empty(emptyProjectArgument.StandardOutput);
		Assert.Contains(
			"error[DPX-CLI-INVALID-SYNTAX]",
			emptyProjectArgument.StandardError,
			StringComparison.Ordinal);

		Assert.False(File.Exists(accidentalArtifact));
		Assert.Equal(
			["App.cs"],
			Directory.EnumerateFiles(project, "*", SearchOption.AllDirectories)
				.Select(path => Path.GetRelativePath(project, path))
				.ToArray());
	}

	[Fact]
	public async Task FileFolderAndZipOutputsReturnOneExactAbsolutePath()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("source");
		Directory.CreateDirectory(Path.Combine(project, "src"));
		File.WriteAllText(
			Path.Combine(project, "src", "App.cs"),
			"class App {}\n",
			new UTF8Encoding(false));
		var contextPath = Path.Combine(workspace.Path, "context.md");
		var folderPath = Path.Combine(workspace.Path, "submission");
		var zipPath = Path.Combine(workspace.Path, "submission.zip");

		var context = await RunAsync(
		[
			"export", "context", project,
			"--format", "markdown",
			"--output", contextPath,
			"--git-mode", "none",
			"--exclude", "none",
			"--progress", "never",
			"--language", "en"
		]);
		AssertSuccessfulPath(context, contextPath);
		Assert.Contains("class App", File.ReadAllText(contextPath), StringComparison.Ordinal);

		var folder = await RunAsync(
		[
			"export", "project", project,
			"--as", "folder",
			"--output", folderPath,
			"--git-mode", "none",
			"--exclude", "none",
			"--progress", "never",
			"--language", "en"
		]);
		AssertSuccessfulPath(folder, folderPath);
		Assert.True(File.Exists(Path.Combine(folderPath, "src", "App.cs")));

		var zip = await RunAsync(
		[
			"export", "project", project,
			"--as", "zip",
			"--output", zipPath,
			"--git-mode", "none",
			"--exclude", "none",
			"--progress", "never",
			"--language", "en"
		]);
		AssertSuccessfulPath(zip, zipPath);
		using var archive = ZipFile.OpenRead(zipPath);
		Assert.Contains(
			archive.Entries,
			static entry => entry.FullName.EndsWith("/src/App.cs", StringComparison.Ordinal));
	}

	private static void AssertSuccessfulPath(ProcessResult result, string expectedPath)
	{
		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(Path.GetFullPath(expectedPath), result.StandardOutput.Trim());
		Assert.Single(result.StandardOutput.Split(
			['\r', '\n'],
			StringSplitOptions.RemoveEmptyEntries));
		Assert.Empty(result.StandardError);
	}

	private static async Task<ProcessResult> RunAsync(
		IReadOnlyList<string> arguments,
		string? workingDirectory = null)
	{
		var executableAssembly = ResolveExecutableAssembly();
		var startInfo = new ProcessStartInfo
		{
			FileName = "dotnet",
			WorkingDirectory = workingDirectory ?? FindRepositoryRoot(),
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		startInfo.ArgumentList.Add(executableAssembly);
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		startInfo.Environment[InvocationEnvironment.TerminalHostVariable] = "1";
		startInfo.Environment["DOTNET_NOLOGO"] = "1";
		startInfo.Environment["NO_COLOR"] = "1";

		using var process = new Process { StartInfo = startInfo };
		Assert.True(process.Start(), "Failed to start the DevProjex process.");
		var outputTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(30));
		try
		{
			await process.WaitForExitAsync(timeout.Token);
		}
		catch
		{
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch
			{
				// The process may have exited between timeout and cleanup.
			}
			throw;
		}

		return new ProcessResult(
			process.ExitCode,
			await outputTask,
			await errorTask);
	}

	private static async Task CreateFifoAsync(string path)
	{
		var startInfo = new ProcessStartInfo("mkfifo")
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		startInfo.ArgumentList.Add(path);
		using var process = new Process { StartInfo = startInfo };
		Assert.True(process.Start(), "Failed to start mkfifo.");
		var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		await process.WaitForExitAsync(TestContext.Current.CancellationToken);
		Assert.True(
			process.ExitCode == 0,
			$"mkfifo failed: {await output} {await error}");
	}

	private static string ResolveExecutableAssembly()
	{
		var root = FindRepositoryRoot();
#if DEBUG
		const string configuration = "Debug";
#else
		const string configuration = "Release";
#endif
		var path = Path.Combine(
			root,
			"Apps",
			"Avalonia",
			"bin",
			configuration,
			"net10.0",
			"DevProjex.dll");
		Assert.True(File.Exists(path), $"DevProjex entry assembly was not built: {path}");
		Assert.True(
			File.Exists(Path.ChangeExtension(path, ".runtimeconfig.json")),
			$"DevProjex runtime config was not built next to: {path}");
		return path;
	}

	private static string FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "DevProjex.sln")))
				return directory.FullName;
			directory = directory.Parent;
		}

		throw new InvalidOperationException("Repository root not found.");
	}

	private sealed record ProcessResult(
		int ExitCode,
		string StandardOutput,
		string StandardError);
}
