using System.IO.Compression;

namespace DevProjex.Tests.Terminal;

public sealed class ExportProjectCommandContractTests
{
	[Fact]
	public async Task FolderExportPreservesBinaryBytesEmptyDirectoriesUnicodeAndTimestamp()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("Проект");
		var binary = Path.Combine(project, "assets", "данные.bin");
		Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
		await File.WriteAllBytesAsync(
			binary,
			[0x00, 0x01, 0xFF, 0x7F],
			TestContext.Current.CancellationToken);
		var expectedTimestamp = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);
		File.SetLastWriteTimeUtc(binary, expectedTimestamp);
		Directory.CreateDirectory(Path.Combine(project, "empty", "nested"));
		var output = Path.Combine(workspace.CreateDirectory("output"), "submission");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(project, output, "folder", environment);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(
			[0x00, 0x01, 0xFF, 0x7F],
			await File.ReadAllBytesAsync(
				Path.Combine(output, "assets", "данные.bin"),
				TestContext.Current.CancellationToken));
		Assert.True(Directory.Exists(Path.Combine(output, "empty", "nested")));
		Assert.InRange(
			File.GetLastWriteTimeUtc(Path.Combine(output, "assets", "данные.bin")),
			expectedTimestamp.AddSeconds(-2),
			expectedTimestamp.AddSeconds(2));
		Assert.Equal(Path.GetFullPath(output) + Environment.NewLine, environment.StandardOutput);
		Assert.Empty(Directory.EnumerateFileSystemEntries(
			Path.GetDirectoryName(output)!,
			".devprojex-*.tmp"));
	}

	[Fact]
	public async Task NestedSelectionCopiesOnlySelectedEffectiveSubtree()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/selected/a.cs", "a");
		workspace.WriteFile("project/src/selected/b.cs", "b");
		workspace.WriteFile("project/src/other.cs", "other");
		workspace.WriteFile("project/tests/test.cs", "test");
		var output = Path.Combine(workspace.CreateDirectory("output"), "submission");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			project,
			output,
			"folder",
			environment,
			"--select", "src/selected");

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.True(File.Exists(Path.Combine(output, "src", "selected", "a.cs")));
		Assert.True(File.Exists(Path.Combine(output, "src", "selected", "b.cs")));
		Assert.False(File.Exists(Path.Combine(output, "src", "other.cs")));
		Assert.False(Directory.Exists(Path.Combine(output, "tests")));
	}

	[Fact]
	public async Task ZipUsesOneProjectRootAndCanReplaceExistingFileAtomically()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/app.cs", "class App {}");
		var output = workspace.WriteFile("output/submission.zip", "old zip bytes");
		var conflictEnvironment = new TestTerminalEnvironment();

		var conflict = await RunAsync(project, output, "zip", conflictEnvironment);
		Assert.Equal(CommandLineExitCodes.DestinationConflict, conflict);
		Assert.Equal("old zip bytes", await File.ReadAllTextAsync(
			output,
			TestContext.Current.CancellationToken));
		Assert.Contains(Path.GetFullPath(output), conflictEnvironment.StandardError, StringComparison.Ordinal);

		var forceEnvironment = new TestTerminalEnvironment();
		var success = await RunAsync(project, output, "zip", forceEnvironment, "--force");
		Assert.Equal(CommandLineExitCodes.Success, success);
		using var archive = ZipFile.OpenRead(output);
		Assert.Contains(archive.Entries, static entry => entry.FullName == "project/src/app.cs");
		Assert.DoesNotContain(
			archive.Entries,
			static entry => !entry.FullName.StartsWith("project/", StringComparison.Ordinal));
		Assert.Empty(Directory.EnumerateFiles(
			Path.GetDirectoryName(output)!,
			$".{Path.GetFileName(output)}.*.tmp"));
	}

	[Theory]
	[InlineData("folder", "--force", "DPX-CLI-FORCE-NOT-SUPPORTED")]
	[InlineData("zip", null, "DPX-CLI-ZIP-EXTENSION-REQUIRED")]
	public async Task InvalidKindSpecificOptionsFailBeforeWriting(
		string kind,
		string? extraOption,
		string expectedCode)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/app.cs", "class App {}");
		var output = Path.Combine(workspace.CreateDirectory("output"), "submission");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			project,
			output,
			kind,
			environment,
			extraOption is null ? [] : [extraOption]);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains(expectedCode, environment.StandardError, StringComparison.Ordinal);
		Assert.False(File.Exists(output));
		Assert.False(Directory.Exists(output));
	}

	[Fact]
	public async Task DryRunValidatesUnsafeAndConflictingDestinationsWithoutStaging()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/app.cs", "class App {}");

		var unsafeOutput = Path.Combine(project, "inside");
		var unsafeEnvironment = new TestTerminalEnvironment();
		var unsafeExit = await RunAsync(
			project,
			unsafeOutput,
			"folder",
			unsafeEnvironment,
			"--dry-run");
		Assert.Equal(CommandLineExitCodes.RuntimeError, unsafeExit);
		Assert.Contains("DPX-EXPORT-UNSAFE-DESTINATION", unsafeEnvironment.StandardError, StringComparison.Ordinal);
		Assert.False(Directory.Exists(unsafeOutput));

		var conflictOutput = workspace.CreateDirectory("output/existing");
		var conflictEnvironment = new TestTerminalEnvironment();
		var conflictExit = await RunAsync(
			project,
			conflictOutput,
			"folder",
			conflictEnvironment,
			"--dry-run");
		Assert.Equal(CommandLineExitCodes.DestinationConflict, conflictExit);
		Assert.Contains("DPX-EXPORT-DESTINATION-EXISTS", conflictEnvironment.StandardError, StringComparison.Ordinal);
		Assert.Empty(Directory.EnumerateFileSystemEntries(
			Path.GetDirectoryName(conflictOutput)!,
			".devprojex-*.tmp"));
	}

	[Fact]
	public async Task PreCanceledExportCreatesNoDestinationOrStaging()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/app.cs", "class App {}");
		var outputParent = workspace.CreateDirectory("output");
		var output = Path.Combine(outputParent, "submission.zip");
		var environment = new TestTerminalEnvironment();
		using var cancellationSource = new CancellationTokenSource();
		cancellationSource.Cancel();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(
			[
				"export", "project", project,
				"--as", "zip",
				"-o", output,
				"--git-mode", "none",
				"--exclude", "none",
				"--progress", "never"
			],
				cancellationSource.Token);

		Assert.Equal(CommandLineExitCodes.Canceled, exitCode);
		Assert.False(File.Exists(output));
		Assert.Empty(Directory.EnumerateFiles(outputParent, ".*.tmp"));
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-CLI-CANCELED", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ProgressChannelNeverPollutesSuccessfulStdoutPath()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/app.cs", "class App {}");
		var output = Path.Combine(workspace.CreateDirectory("output"), "submission");
		var environment = new TestTerminalEnvironment
		{
			IsErrorInteractive = true
		};

		var exitCode = await RunAsync(
			project,
			output,
			"folder",
			environment,
			"--progress", "always");

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(Path.GetFullPath(output) + Environment.NewLine, environment.StandardOutput);
		Assert.DoesNotContain("Exporting", environment.StandardOutput, StringComparison.Ordinal);
	}

	private static Task<int> RunAsync(
		string project,
		string output,
		string kind,
		TestTerminalEnvironment environment,
		params string[] additionalArguments)
	{
		var arguments = new List<string>
		{
			"export", "project", project,
			"--as", kind,
			"-o", output,
			"--git-mode", "none",
			"--exclude", "none"
		};
		if (!additionalArguments.Contains("--progress", StringComparer.Ordinal))
			arguments.AddRange(["--progress", "never"]);
		arguments.AddRange(additionalArguments);
		return new TerminalApplication(environment, new TerminalServiceFactory())
			.RunAsync(arguments, TestContext.Current.CancellationToken);
	}
}
