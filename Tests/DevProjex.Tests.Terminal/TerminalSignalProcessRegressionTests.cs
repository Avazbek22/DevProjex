using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalSignalProcessRegressionTests
{
	[Theory]
	[InlineData(1, "SIGHUP")]
	[InlineData(2, "SIGINT")]
	[InlineData(15, "SIGTERM")]
	public async Task PosixSignalCancelsActiveStreamingCommand(
		int signal,
		string signalName)
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("POSIX process signals are validated on Linux and macOS runners.");

		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var sourceFile = Path.Combine(project, "large.txt");
		await WriteLargeFileAsync(
			sourceFile,
			32 * 1024 * 1024,
			TestContext.Current.CancellationToken);
		var sourceLength = new FileInfo(sourceFile).Length;
		var application = PublishedApplicationLocator.FindExecutable();
		var dataRoot = workspace.CreateDirectory("app-data");

		using var process = CreateBlockedStreamingProcess(
			application,
			project,
			dataRoot);
		Assert.True(process.Start());

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(30));
		try
		{
			var standardErrorTask =
				process.StandardError.ReadToEndAsync(timeout.Token);
			var standardOutputPrefix = await WaitForStreamingPayloadAsync(
				process,
				timeout.Token);
			Assert.False(
				process.HasExited,
				$"The streaming command exited before {signalName} could be delivered.");
			var standardOutputDrain = DrainRemainingOutputAsync(
				process,
				standardOutputPrefix.Length,
				timeout.Token);
			Assert.Equal(0, SendSignal(process.Id, signal));

			await process.WaitForExitAsync(timeout.Token);
			var standardOutputLength = await standardOutputDrain;
			var standardError = await standardErrorTask;

			Assert.Equal(CommandLineExitCodes.Canceled, process.ExitCode);
			Assert.True(standardOutputLength > 0);
			Assert.Contains("DPX-CLI-CANCELED", standardError, StringComparison.Ordinal);
			Assert.DoesNotContain("DPX-CLI-UNEXPECTED", standardError, StringComparison.Ordinal);
			Assert.DoesNotContain("stack trace", standardError, StringComparison.OrdinalIgnoreCase);
			Assert.Equal(sourceLength, new FileInfo(sourceFile).Length);
			Assert.Equal(
				["large.txt"],
				Directory.EnumerateFiles(project, "*", SearchOption.AllDirectories)
					.Select(path => Path.GetRelativePath(project, path))
					.Order(StringComparer.Ordinal)
					.ToArray());
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

	[Fact]
	public async Task RepeatedPosixInterruptTerminatesAfterCancellationWasObserved()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("POSIX process signals are validated on Linux and macOS runners.");

		var checkpointHost =
			PublishedApplicationLocator.FindProgressCheckpointHostExecutable();
		using var process = CreateSignalCheckpointProcess(checkpointHost);
		Assert.True(process.Start());

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(30));
		var standardErrorTask =
			process.StandardError.ReadToEndAsync(timeout.Token);
		try
		{
			Assert.Equal(
				TerminalSignalCheckpointProtocol.Ready,
				await process.StandardOutput.ReadLineAsync(timeout.Token));

			Assert.Equal(0, SendSignal(process.Id, signal: 2));
			Assert.Equal(
				TerminalSignalCheckpointProtocol.CancellationObserved,
				await process.StandardOutput.ReadLineAsync(timeout.Token));
			Assert.False(
				process.HasExited,
				"The process exited before the repeated interrupt could be delivered.");

			Assert.Equal(0, SendSignal(process.Id, signal: 2));
			await process.WaitForExitAsync(timeout.Token);
			var remainingOutput =
				await process.StandardOutput.ReadToEndAsync(timeout.Token);
			var standardError = await standardErrorTask;

			Assert.Equal(CommandLineExitCodes.Canceled, process.ExitCode);
			Assert.Empty(remainingOutput);
			Assert.DoesNotContain(
				"DPX-CLI-UNEXPECTED",
				standardError,
				StringComparison.Ordinal);
			Assert.DoesNotContain(
				"stack trace",
				standardError,
				StringComparison.OrdinalIgnoreCase);
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

	private static Process CreateBlockedStreamingProcess(
		string application,
		string project,
		string dataRoot)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = application,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		foreach (var argument in new[]
		         {
			         "export", "context", project,
			         "--view", "content",
			         "--format", "text",
			         "--git-mode", "none",
			         "--exclude", "none",
			         "--plain",
			         "--progress", "never",
			         "-o", "-"
		         })
		{
			startInfo.ArgumentList.Add(argument);
		}

		startInfo.Environment[InvocationEnvironment.TerminalHostVariable] = "1";
		startInfo.Environment[InvocationEnvironment.InternalDataRootVariable] = dataRoot;
		startInfo.Environment["DOTNET_NOLOGO"] = "1";
		return new Process { StartInfo = startInfo };
	}

	private static Process CreateSignalCheckpointProcess(string checkpointHost)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = checkpointHost,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		startInfo.Environment[
			TerminalSignalCheckpointProtocol.EnabledVariable] = "1";
		startInfo.Environment["DOTNET_NOLOGO"] = "1";
		return new Process { StartInfo = startInfo };
	}

	private static async Task<string> WaitForStreamingPayloadAsync(
		Process process,
		CancellationToken cancellationToken)
	{
		// The product registers its signal coordinator before command execution,
		// so the first payload character is an observable readiness boundary for
		// both the framework apphost and a published single-file executable.
		var firstCharacter = new char[1];
		var charactersRead = await process.StandardOutput.ReadAsync(
			firstCharacter,
			cancellationToken);
		Assert.Equal(1, charactersRead);
		return new string(firstCharacter);
	}

	private static async Task<long> DrainRemainingOutputAsync(
		Process process,
		long charactersRead,
		CancellationToken cancellationToken)
	{
		var buffer = new char[16 * 1024];
		while (true)
		{
			var count = await process.StandardOutput.ReadAsync(
				buffer,
				cancellationToken);
			if (count == 0)
				return charactersRead;

			charactersRead += count;
		}
	}

	private static async Task WriteLargeFileAsync(
		string path,
		int byteCount,
		CancellationToken cancellationToken)
	{
		var buffer = new byte[64 * 1024];
		Array.Fill(buffer, (byte)'x');
		await using var destination = new FileStream(
			path,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None,
			buffer.Length,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		for (var remaining = byteCount; remaining > 0;)
		{
			var count = Math.Min(remaining, buffer.Length);
			await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
			remaining -= count;
		}
	}

	[DllImport("libc", EntryPoint = "kill", SetLastError = true)]
	private static extern int SendSignal(int processId, int signal);
}
