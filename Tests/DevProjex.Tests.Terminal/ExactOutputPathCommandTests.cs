using DevProjex.Infrastructure.ResourceStore;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Tests.Terminal;

public sealed class ExactOutputPathCommandTests
{
	private const string TrailingSeparatorMessage =
		"The output path ends with a folder separator. Pass a file path.";

	private static readonly LocalizationService English =
		new(new JsonLocalizationCatalog(), AppLanguage.En);

	public static TheoryData<string> SingleFileCommands => new()
	{
		"tree|.",
		"analyze|.",
		"search|needle|.",
		"related|src/App.cs",
		"diff",
		"export|context|.",
		"export|project|.|--as|zip",
		"profile|export|.",
		"mcp|log"
	};

	[Theory]
	[MemberData(nameof(SingleFileCommands))]
	public async Task EverySingleFileCommandRejectsATrailingSlashBeforeRunning(string command)
	{
		var result = await RunAsync([.. command.Split('|'), "-o", "out/"]);

		AssertInvalidValue(result, TrailingSeparatorMessage);
	}

	[Theory]
	[MemberData(nameof(SingleFileCommands))]
	public async Task EverySingleFileCommandRejectsASpacesOnlyPath(string command)
	{
		var result = await RunAsync([.. command.Split('|'), "--output", "   "]);

		AssertInvalidValue(result, "The output path is empty.");
	}

	[Theory]
	[InlineData("export|context|.|-o|out\\")]
	[InlineData("tree|.|--output=C:\\")]
	[InlineData("analyze|.|-o:out\\")]
	public async Task WindowsRejectsATrailingBackslash(string invocation)
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("A trailing backslash is an ordinary file-name character on Unix.");

		var result = await RunAsync(invocation.Split('|'));

		AssertInvalidValue(result, TrailingSeparatorMessage);
	}

	[Theory]
	[InlineData("NUL")]
	[InlineData("nul.md")]
	[InlineData("Con.txt")]
	[InlineData("COM1")]
	[InlineData("lpt9.json")]
	public async Task WindowsRejectsDeviceNamesWithTheDryRunHint(string path)
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("Reserved device names exist only on Windows.");

		var result = await RunAsync(["export", "context", ".", "-o", path]);

		AssertInvalidValue(
			result,
			$"The output path names a device, not a file: {path}. Use --dry-run to check without writing.");
	}

	[Fact]
	public async Task WindowsRejectsADeviceNamedZipDestination()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("Reserved device names exist only on Windows.");

		var result = await RunAsync(["export", "project", ".", "--as", "zip", "-o", "NUL.zip"]);

		AssertInvalidValue(result, "The output path names a device, not a file: NUL.zip.");
	}

	[Theory]
	[InlineData("out|file.md")]
	[InlineData("out?.md")]
	[InlineData("dir/out:stream.md")]
	public async Task WindowsRejectsInvalidFileNameCharacters(string path)
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("These characters are valid in Unix file names.");

		var result = await RunAsync(["tree", ".", "-o", path]);

		AssertInvalidValue(result, $"The output path is not a valid file path: {path}");
	}

	[Fact]
	public async Task ANulCharacterIsAnInvalidPathOnEveryPlatform()
	{
		var result = await RunAsync(["tree", ".", "-o", "out\0.md"]);

		AssertInvalidValue(result, "The output path is not a valid file path: out");
	}

	[Fact]
	public async Task UnixKeepsDeviceNamesAndBackslashesAsOrdinaryFileNames()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("Windows reserves these names.");

		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);
		var output = workspace.CreateDirectory("output");

		foreach (var name in new[] { "NUL", "con.md", "name\\" })
		{
			var destination = Path.Combine(output, name);
			var result = await RunAsync(
				["tree", project, "--git-mode", "none", "--exclude", "none", "-o", destination],
				workspace);

			Assert.True(result.ExitCode == CommandLineExitCodes.Success, result.Error);
			Assert.True(File.Exists(destination), destination);
		}
	}

	[Fact]
	public async Task StandardOutputDashStillWritesToStdout()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);

		var result = await RunAsync(
			["tree", project, "--git-mode", "none", "--exclude", "none", "-o", "-"],
			workspace);

		Assert.True(result.ExitCode == CommandLineExitCodes.Success, result.Error);
		Assert.Contains("App.cs", result.Output, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExistingFolderWithoutSeparatorKeepsTheDestinationConflict()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);
		var folder = workspace.CreateDirectory("existing");

		var result = await RunAsync(
			["export", "context", project, "--git-mode", "none", "--exclude", "none", "-o", folder],
			workspace);

		Assert.Equal(CommandLineExitCodes.DestinationConflict, result.ExitCode);
		Assert.Contains("[DPX-EXPORT-DESTINATION-EXISTS]", result.Error, StringComparison.Ordinal);
		Assert.DoesNotContain("DPX-CLI-INVALID-VALUE", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task FolderExportStillAcceptsATrailingSeparator()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);
		var destination = Path.Combine(workspace.Path, "copy") + Path.DirectorySeparatorChar;

		var result = await RunAsync(
			[
				"export", "project", project, "--as", "folder",
				"--git-mode", "none", "--exclude", "none", "-o", destination
			],
			workspace);

		Assert.True(result.ExitCode == CommandLineExitCodes.Success, result.Error);
		Assert.True(File.Exists(Path.Combine(workspace.Path, "copy", "src", "App.cs")));
	}

	[Fact]
	public void DestinationInsideTheProjectNamesTheProject()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);

		var exception = Assert.Throws<ProjectCopyExportException>(() =>
			ExactFileOutputDestinationPolicy.Resolve(
				project,
				Path.Combine(project, "out.md"),
				overwrite: false));
		var error = ProjectCopyTerminalErrorMapper.Map(exception, English);

		Assert.Equal("DPX-EXPORT-UNSAFE-DESTINATION", error.Code);
		Assert.Equal(CommandLineExitCodes.PolicyFailure, error.ExitCode);
		Assert.Equal(English["Terminal.Error.UnsafeDestination"], error.Message);
	}

	[Fact]
	public void DestinationLinkedIntoTheProjectNamesTheProject()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);
		var link = Path.Combine(workspace.Path, "linked");
		if (!TryCreateDirectoryLink(link, Path.Combine(project, "src")))
			Assert.Skip("Directory symbolic links are unavailable in this test environment.");

		var exception = Assert.Throws<ProjectCopyExportException>(() =>
			ExactFileOutputDestinationPolicy.Resolve(
				project,
				Path.Combine(link, "out.md"),
				overwrite: false));
		var error = ProjectCopyTerminalErrorMapper.Map(exception, English);

		Assert.True(exception.ResolvesInsideSource);
		Assert.Equal("DPX-EXPORT-UNSAFE-DESTINATION", error.Code);
		Assert.Equal(CommandLineExitCodes.PolicyFailure, error.ExitCode);
		Assert.Equal(English["Terminal.Error.UnsafeDestination"], error.Message);
	}

	[Fact]
	public void DestinationThatCannotBeResolvedIsRefusedWithoutClaimingItIsInsideTheProject()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);
		var first = Path.Combine(workspace.Path, "first");
		var second = Path.Combine(workspace.Path, "second");
		if (!TryCreateDirectoryLink(first, second) || !TryCreateDirectoryLink(second, first))
			Assert.Skip("Directory symbolic links are unavailable in this test environment.");

		var exception = Assert.Throws<ProjectCopyExportException>(() =>
			ExactFileOutputDestinationPolicy.Resolve(
				project,
				Path.Combine(first, "out.md"),
				overwrite: false));
		var error = ProjectCopyTerminalErrorMapper.Map(exception, English);

		Assert.Equal(ProjectCopyExportError.UnsafeDestinationPath, exception.Error);
		Assert.False(exception.ResolvesInsideSource);
		Assert.Equal("DPX-EXPORT-UNSAFE-DESTINATION", error.Code);
		Assert.Equal(CommandLineExitCodes.PolicyFailure, error.ExitCode);
		Assert.Equal(English["Terminal.Error.UnsafeDestinationUnverified"], error.Message);
		Assert.DoesNotContain("inside the source project", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void UnverifiedUnsafeDestinationKeepsThePolicyExitCode()
	{
		var error = ProjectCopyTerminalErrorMapper.Map(
			new ProjectCopyExportException(ProjectCopyExportError.UnsafeDestinationPath, "raw"),
			English);
		var inside = ProjectCopyTerminalErrorMapper.Map(
			new ProjectCopyExportException(ProjectCopyExportError.DestinationInsideSource, "raw"),
			English);

		Assert.Equal("DPX-EXPORT-UNSAFE-DESTINATION", error.Code);
		Assert.Equal(CommandLineExitCodes.PolicyFailure, error.ExitCode);
		Assert.Equal("The destination could not be checked safely.", error.Message);
		Assert.Equal(English["Terminal.Error.UnsafeDestination"], inside.Message);
	}

	private static string CreateProject(TemporaryDirectory workspace)
	{
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/App.cs", "namespace Demo;\npublic sealed class App { }\n");
		return project;
	}

	private static bool TryCreateDirectoryLink(string path, string target)
	{
		try
		{
			Directory.CreateSymbolicLink(path, target);
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			return false;
		}
	}

	private static void AssertInvalidValue(CommandRun result, string expectedMessage)
	{
		Assert.True(
			result.ExitCode == CommandLineExitCodes.UsageError,
			$"exit {result.ExitCode}: {result.Error}");
		Assert.Empty(result.Output);
		Assert.Contains("error[DPX-CLI-INVALID-VALUE]", result.Error, StringComparison.Ordinal);
		Assert.Contains(expectedMessage, result.Error, StringComparison.Ordinal);
	}

	private static async Task<CommandRun> RunAsync(
		string[] arguments,
		TemporaryDirectory? workspace = null)
	{
		using var ownedWorkspace = workspace is null ? new TemporaryDirectory() : null;
		var dataWorkspace = workspace ?? ownedWorkspace!;
		var environment = new TestTerminalEnvironment();
		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => dataWorkspace.CreateDirectory("data")))
			.RunAsync(
				[.. arguments, "--language", "en"],
				TestContext.Current.CancellationToken);
		return new CommandRun(exitCode, environment.StandardOutput, environment.StandardError);
	}

	private sealed record CommandRun(int ExitCode, string Output, string Error);
}
