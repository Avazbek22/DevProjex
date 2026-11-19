namespace DevProjex.Tests.Terminal;

public sealed partial class CliInputContractProcessTests
{
	public static TheoryData<string> GitScopedCommands => new()
	{
		"tree|{project}",
		"search|needle|{project}",
		"related|src/App.cs|--project|{project}",
		"diff|--project|{project}",
		"export|context|{project}",
		"export|project|{project}|--as|zip|-o|{workspace}/copy.zip",
		"analyze|{project}"
	};

	[Theory]
	[MemberData(nameof(GitScopedCommands))]
	public void UnavailableGitStateStaysAPolicyFailure(string invocation)
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);
		var arguments = invocation
			.Replace("{project}", project, StringComparison.Ordinal)
			.Replace("{workspace}", workspace.Path, StringComparison.Ordinal)
			.Split('|');

		var result = RunCli(workspace, [.. arguments, "--git-mode", "staged"]);

		Assert.True(
			result.ExitCode == CommandLineExitCodes.PolicyFailure,
			$"exit {result.ExitCode}: {result.StandardError}");
		Assert.Contains("DPX-GIT-STATE-UNAVAILABLE", result.StandardError, StringComparison.Ordinal);
		Assert.False(File.Exists(Path.Combine(workspace.Path, "copy.zip")));
	}

	[Theory]
	[InlineData("tree")]
	[InlineData("export|context")]
	[InlineData("analyze")]
	public void UnreadableProjectRootIsAUsageError(string command)
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("This check removes Unix read permission from the project root.");
			return;
		}

		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);
		var originalMode = File.GetUnixFileMode(project);
		File.SetUnixFileMode(project, UnixFileMode.None);
		try
		{
			if (CanEnumerate(project))
				Assert.Skip("The test process can read a directory without permissions (for example, as root).");

			var result = RunCli(
				workspace,
				[.. command.Split('|'), project, "--git-mode", "none", "--format", "json"]);

			Assert.True(
				result.ExitCode == CommandLineExitCodes.UsageError,
				$"exit {result.ExitCode}: {result.StandardError}");
			Assert.Matches(
				"DPX-PROJECT-(ROOT-ACCESS-DENIED|NOT-FOUND)",
				result.StandardError);
		}
		finally
		{
			File.SetUnixFileMode(project, originalMode);
		}
	}

	private static bool CanEnumerate(string directory)
	{
		try
		{
			_ = Directory.EnumerateFileSystemEntries(directory).FirstOrDefault();
			return true;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}
}
