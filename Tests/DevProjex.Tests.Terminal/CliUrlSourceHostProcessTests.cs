using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

/// <summary>
/// Clone-and-export journeys for a URL source, driven through a real child process.
/// </summary>
/// <remarks>
/// A synthetic origin can only be reached over the local file transport, which the shipped
/// application refuses. These journeys therefore run on the terminal test host, which hosts the
/// same DevProjex.Terminal library and grants that transport for the child process only.
/// </remarks>
[Collection(TerminalProcessCollection.Name)]
public sealed class CliUrlSourceHostProcessTests
{
	[Fact]
	public async Task UrlSourcesExportThroughARealProcess()
	{
		if (!IsGitAvailable())
			Assert.Skip("Git is unavailable on this test host.");

		using var workspace = new TemporaryDirectory();
		var source = workspace.CreateDirectory("url/source");
		RunGit(source, "init", "--initial-branch=main");
		RunGit(source, "config", "user.email", "terminal-tests@devprojex.local");
		RunGit(source, "config", "user.name", "DevProjex Terminal Tests");
		workspace.WriteFile("url/source/src/remote.cs", "internal sealed class PublishedRemoteMarker {}\n");
		RunGit(source, "add", ".");
		RunGit(source, "commit", "-m", "initial");
		var bare = Path.Combine(workspace.Path, "url", "origin.git");
		RunGit(workspace.Path, "clone", "--bare", source, bare);
		var repositoryUrl = new Uri(bare + Path.DirectorySeparatorChar).AbsoluteUri;
		var dataRoot = workspace.CreateDirectory("url/data");

		var context = await RunAsync(
			dataRoot,
			workspace.Path,
			[
				"export", "context", repositoryUrl,
				"--git-mode", "none", "--view", "content", "--format", "text", "-o", "-", "--plain",
				"--language", "en"
			]);
		Assert.Equal(CommandLineExitCodes.Success, context.ExitCode);
		Assert.Contains("PublishedRemoteMarker", context.StandardOutput, StringComparison.Ordinal);
		var progressLines = context.StandardError
			.ReplaceLineEndings("\n")
			.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		Assert.InRange(progressLines.Length, 2, 6);
		Assert.StartsWith("Cloning ", progressLines[0], StringComparison.Ordinal);
		Assert.Equal("Clone completed.", progressLines[^1]);

		var quietContext = await RunAsync(
			dataRoot,
			workspace.Path,
			[
				"export", "context", repositoryUrl,
				"--git-mode", "none", "--view", "content", "--format", "text", "-o", "-", "--plain",
				"--language", "en", "--progress", "never"
			]);
		Assert.Equal(CommandLineExitCodes.Success, quietContext.ExitCode);
		Assert.Equal(context.StandardOutput, quietContext.StandardOutput);
		Assert.Empty(quietContext.StandardError);

		var destination = Path.Combine(workspace.Path, "url", "exported");
		var project = await RunAsync(
			dataRoot,
			workspace.Path,
			[
				"export", "project", repositoryUrl,
				"--git-mode", "none", "--as", "folder", "-o", destination, "--plain"
			]);
		Assert.Equal(CommandLineExitCodes.Success, project.ExitCode);
		Assert.Equal(
			"internal sealed class PublishedRemoteMarker {}\n",
			File.ReadAllText(Path.Combine(destination, "src", "remote.cs")).ReplaceLineEndings("\n"));
		Assert.Empty(project.StandardError);
	}

	[Fact]
	public async Task TheTestHostStillRefusesALocalFileUrlWhenTheTransportIsNotRequested()
	{
		using var workspace = new TemporaryDirectory();
		var origin = workspace.CreateDirectory("origin.git");
		var dataRoot = workspace.CreateDirectory("data");

		var result = await RunAsync(
			dataRoot,
			workspace.Path,
			["analyze", new Uri(origin).AbsoluteUri, "--format", "json"],
			allowLocalFileTransport: false);

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Empty(result.StandardOutput);
		Assert.Contains("DPX-CLI-GIT-URL-INVALID", result.StandardError, StringComparison.Ordinal);
	}

	private static async Task<ProcessResult> RunAsync(
		string dataRoot,
		string workingDirectory,
		IReadOnlyList<string> arguments,
		bool allowLocalFileTransport = true)
	{
		using var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = "dotnet",
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = new UTF8Encoding(false),
				StandardErrorEncoding = new UTF8Encoding(false),
				WorkingDirectory = workingDirectory
			}
		};
		process.StartInfo.ArgumentList.Add(PublishedApplicationLocator.FindTerminalTestHostAssembly());
		process.StartInfo.ArgumentList.Add(TerminalTransportPolicyProtocol.TerminalCommandArgument);
		foreach (var argument in arguments)
			process.StartInfo.ArgumentList.Add(argument);
		process.StartInfo.Environment[InvocationEnvironment.TerminalHostVariable] = "1";
		process.StartInfo.Environment[InvocationEnvironment.InternalDataRootVariable] = dataRoot;
		process.StartInfo.Environment["DOTNET_NOLOGO"] = "1";
		if (allowLocalFileTransport)
		{
			process.StartInfo.Environment[TerminalTransportPolicyProtocol.AllowLocalFileTransportVariable] =
				TerminalTransportPolicyProtocol.Enabled;
		}

		Assert.True(process.Start());
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(90));
		var standardOutputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
		var standardErrorTask = process.StandardError.ReadToEndAsync(timeout.Token);
		try
		{
			await process.WaitForExitAsync(timeout.Token);
			return new ProcessResult(
				process.ExitCode,
				await standardOutputTask,
				await standardErrorTask);
		}
		finally
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
				await process.WaitForExitAsync(CancellationToken.None);
			}
		}
	}

	private static bool IsGitAvailable()
	{
		try
		{
			using var process = Process.Start(CreateGitStartInfo(null, ["--version"]));
			process?.WaitForExit(5_000);
			return process is { HasExited: true, ExitCode: 0 };
		}
		catch
		{
			return false;
		}
	}

	private static void RunGit(string workingDirectory, params string[] arguments)
	{
		var result = TerminalTestProcess.Run(CreateGitStartInfo(workingDirectory, arguments));
		Assert.True(
			result.ExitCode == 0,
			$"git {string.Join(' ', arguments)} failed: {result.StandardOutput}{result.StandardError}");
	}

	private static ProcessStartInfo CreateGitStartInfo(
		string? workingDirectory,
		IReadOnlyList<string> arguments)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = "git",
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		if (workingDirectory is not null)
			startInfo.WorkingDirectory = workingDirectory;
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		return startInfo;
	}

	private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
