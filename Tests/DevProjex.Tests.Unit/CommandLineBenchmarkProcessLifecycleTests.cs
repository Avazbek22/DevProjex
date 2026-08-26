using System.Diagnostics;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit;

public sealed class CommandLineBenchmarkProcessLifecycleTests
{
	private static readonly TimeSpan ProcessTreeStartupTimeout = TimeSpan.FromSeconds(30);

	[Fact]
	public async Task RunAsync_CancellationWaitsForProcessTreeAndReleasesRedirectedStreams()
	{
		using var temp = new TemporaryDirectory();
		var lockPath = Path.Combine(temp.Path, "benchmark-process.lock");
		var readyPath = Path.Combine(temp.Path, "benchmark-process.ready");
		var childProcessIdPath = GitCancellationProcessHost.GetChildProcessIdPath(readyPath);
		int? childProcessId = null;
		var startInfo = GitCancellationProcessHost.CreateStartInfo(
			GitCancellationProcessHost.ParentRole,
			lockPath,
			readyPath,
			redirectOutput: true);
		var request = new CommandLineBenchmarkProcessRequest(
			startInfo.FileName,
			startInfo.ArgumentList.ToArray(),
			Directory.GetCurrentDirectory(),
			"benchmark process lifecycle test",
			new Dictionary<string, string?>
			{
				[GitCancellationProcessHost.LockPathEnvironmentVariable] = lockPath,
				[GitCancellationProcessHost.ReadyPathEnvironmentVariable] = readyPath,
				[GitCancellationProcessHost.RoleEnvironmentVariable] =
					GitCancellationProcessHost.ParentRole
			});
		var runner = new DefaultCommandLineBenchmarkProcessRunner();
		using var cancellation = new CancellationTokenSource();
		var run = runner.RunAsync(request, index: 1, isWarmup: false, cancellation.Token);

		try
		{
			await WaitForFileAsync(readyPath, ProcessTreeStartupTimeout);
			childProcessId = int.Parse(
				await File.ReadAllTextAsync(readyPath, TestContext.Current.CancellationToken),
				NumberStyles.None,
				CultureInfo.InvariantCulture);

			cancellation.Cancel();

			await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run);
			using var releasedHandle = new FileStream(
				lockPath,
				FileMode.Open,
				FileAccess.ReadWrite,
				FileShare.None);
		}
		finally
		{
			childProcessId ??= TryReadProcessId(childProcessIdPath);
			if (childProcessId is { } processId)
				await TerminateTestProcessAsync(processId);
		}
	}

	private static async Task WaitForFileAsync(string path, TimeSpan timeout)
	{
		var startedAt = Stopwatch.StartNew();
		while (!File.Exists(path))
		{
			if (startedAt.Elapsed >= timeout)
				throw new TimeoutException($"The process readiness file was not created: {path}");

			await Task.Delay(TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
		}
	}

	private static int? TryReadProcessId(string path) =>
		File.Exists(path) && int.TryParse(
			File.ReadAllText(path),
			NumberStyles.None,
			CultureInfo.InvariantCulture,
			out var processId)
			? processId
			: null;

	private static async Task TerminateTestProcessAsync(int processId)
	{
		try
		{
			using var process = Process.GetProcessById(processId);
			if (process.HasExited)
				return;

			process.Kill(entireProcessTree: true);
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			await process.WaitForExitAsync(timeout.Token);
		}
		catch (ArgumentException)
		{
			// The helper already exited.
		}
	}
}
