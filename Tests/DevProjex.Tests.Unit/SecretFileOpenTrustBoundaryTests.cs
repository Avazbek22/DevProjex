using System.Diagnostics;
using DevProjex.Application.Services;

namespace DevProjex.Tests.Unit;

public sealed class SecretFileOpenTrustBoundaryTests
{
	[Fact]
	public async Task UnixRegularFileReplacedByFifoBeforeOpenIsRejectedWithoutWaitingForWriter()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("FIFO trust-boundary coverage is Unix-only.");

		using var temporary = new TemporaryDirectory();
		var path = temporary.CreateFile("candidate.txt", "safe");
		Assert.True(UnixFileTypeInspector.IsRegularFile(path));
		var openTask = Task.Run(() => Record.Exception(() =>
		{
			using var stream = UnixFileTypeInspector.OpenRegularFileForSequentialRead(
				path,
				bufferSize: 1,
				FileShare.ReadWrite | FileShare.Delete,
				asynchronous: false,
				beforeOpen: () =>
				{
					File.Delete(path);
					CreateFifoOrSkip(path);
				});
		}));

		var completed = await Task.WhenAny(
			openTask,
			Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
		Assert.Same(openTask, completed);
		Assert.IsType<IOException>(await openTask);
	}

	[Fact]
	public async Task FileContentAnalyzerRejectsFifoWithoutWaitingForWriter()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("FIFO trust-boundary coverage is Unix-only.");

		using var temporary = new TemporaryDirectory();
		var path = Path.Combine(temporary.Path, "source.txt");
		CreateFifoOrSkip(path);
		var readTask = Task.Run(async () =>
			await new FileContentAnalyzer().ReadClassifiedAsync(
				path,
				1024,
				TestContext.Current.CancellationToken));

		var completed = await Task.WhenAny(
			readTask,
			Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
		Assert.Same(readTask, completed);
		Assert.Equal(FileContentClassification.Unreadable, (await readTask).Classification);
	}

	private static void CreateFifoOrSkip(string path)
	{
		var startInfo = new ProcessStartInfo("mkfifo")
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		startInfo.ArgumentList.Add(path);
		try
		{
			using var process = Process.Start(startInfo);
			if (process is null)
				Assert.Skip("mkfifo could not be started.");
			var output = process.StandardOutput.ReadToEnd();
			var error = process.StandardError.ReadToEnd();
			if (!process.WaitForExit(5_000))
			{
				process.Kill(entireProcessTree: true);
				Assert.Skip("mkfifo did not complete within five seconds.");
			}
			if (process.ExitCode != 0)
				Assert.Skip($"mkfifo is unavailable: {error}{output}");
		}
		catch (System.ComponentModel.Win32Exception)
		{
			Assert.Skip("mkfifo is not available in this environment.");
		}
	}
}
