namespace DevProjex.Tests.Terminal;

public sealed partial class CliInputContractProcessTests
{
	[Theory]
	[InlineData("export|context", "json")]
	[InlineData("tree", "json")]
	[InlineData("analyze", "json")]
	public void TrailingSlashOutputFailsAsInvalidValueWithEmptyStdout(string command, string format)
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);
		var destination = Path.Combine(workspace.Path, "out") + "/";

		var result = RunCli(
			workspace,
			[.. command.Split('|'), project, "--format", format, "--git-mode", "none", "-o", destination]);

		AssertUsageFailure(
			result,
			"DPX-CLI-INVALID-VALUE",
			"The output path ends with a folder separator. Pass a file path.");
		Assert.False(Directory.Exists(Path.Combine(workspace.Path, "out")));
	}

	[Fact]
	public void SpacesOnlyOutputFailsAsInvalidValue()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);

		var result = RunCli(workspace, "export", "context", project, "--git-mode", "none", "-o", "   ");

		AssertUsageFailure(result, "DPX-CLI-INVALID-VALUE", "The output path is empty.");
	}

	[Fact]
	public void WindowsDeviceOutputFailsBeforeAnyWrite()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("Reserved device names exist only on Windows.");

		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);

		var result = RunCli(workspace, "tree", project, "--git-mode", "none", "-o", "nul.txt");

		AssertUsageFailure(
			result,
			"DPX-CLI-INVALID-VALUE",
			"The output path names a device, not a file: nul.txt. Use --dry-run to check without writing.");
	}

	[Fact]
	public void UnixDeviceNameIsAnOrdinaryOutputFile()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("Windows reserves this name.");

		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);
		var destination = Path.Combine(workspace.Path, "NUL.txt");

		var result = RunCli(workspace, "tree", project, "--git-mode", "none", "-o", destination);

		Assert.True(result.ExitCode == 0, result.StandardError);
		Assert.True(File.Exists(destination));
	}

	[Fact]
	public void OutputInsideTheProjectStaysAPolicyRefusalNamingTheProject()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);

		var result = RunCli(
			workspace,
			"export", "context", project, "--git-mode", "none", "-o", Path.Combine(project, "context.md"));

		Assert.Equal(CommandLineExitCodes.PolicyFailure, result.ExitCode);
		Assert.Empty(result.StandardOutput);
		Assert.Contains("[DPX-EXPORT-UNSAFE-DESTINATION]", result.StandardError, StringComparison.Ordinal);
		Assert.Contains("resolves inside the source project", result.StandardError, StringComparison.Ordinal);
	}
}
