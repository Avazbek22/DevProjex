using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

internal static class TerminalTestProcess
{
	private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(2);
	private static readonly object ExecutionSync = new();

	public static TerminalTestProcessResult Run(
		ProcessStartInfo startInfo,
		TimeSpan? timeout = null)
	{
		ArgumentNullException.ThrowIfNull(startInfo);
		lock (ExecutionSync)
			return RunCore(startInfo, timeout ?? DefaultTimeout);
	}

	private static TerminalTestProcessResult RunCore(
		ProcessStartInfo startInfo,
		TimeSpan timeout)
	{
		if (startInfo.UseShellExecute ||
		    !startInfo.RedirectStandardOutput ||
		    !startInfo.RedirectStandardError)
		{
			throw new ArgumentException(
				"Test processes must disable shell execution and redirect standard output and error.",
				nameof(startInfo));
		}

		startInfo.RedirectStandardInput = true;
		using var process = new Process { StartInfo = startInfo };
		if (!process.Start())
			throw new InvalidOperationException($"Could not start '{startInfo.FileName}'.");
		process.StandardInput.Close();

		var standardOutput = ReadToEndOnDedicatedThread(process.StandardOutput);
		var standardError = ReadToEndOnDedicatedThread(process.StandardError);
		if (!process.WaitForExit(checked((int)timeout.TotalMilliseconds)))
		{
			TryKill(process);
			ObserveReaders(standardOutput, standardError);
			throw new TimeoutException(
				$"'{startInfo.FileName}' did not exit within {timeout.TotalSeconds:0} seconds.");
		}

		var redirectedOutput = Task.WhenAll(standardOutput, standardError);
		try
		{
			redirectedOutput.WaitAsync(OutputDrainTimeout).GetAwaiter().GetResult();
		}
		catch (TimeoutException)
		{
			process.StandardOutput.Dispose();
			process.StandardError.Dispose();
			ObserveReaders(standardOutput, standardError);
			throw new TimeoutException(
				$"'{startInfo.FileName}' exited, but its redirected output remained open for " +
				$"more than {OutputDrainTimeout.TotalSeconds:0} seconds.");
		}
		return new TerminalTestProcessResult(
			process.ExitCode,
			standardOutput.Result,
			standardError.Result);
	}

	private static Task<string> ReadToEndOnDedicatedThread(StreamReader reader) =>
		Task.Factory.StartNew(
			reader.ReadToEnd,
			CancellationToken.None,
			TaskCreationOptions.LongRunning,
			TaskScheduler.Default);

	private static void ObserveReaders(params Task<string>[] readers)
	{
		try
		{
			Task.WaitAll(readers, millisecondsTimeout: 5_000);
		}
		catch (AggregateException)
		{
		}
	}

	private static void TryKill(Process process)
	{
		try
		{
			process.Kill(entireProcessTree: true);
			process.WaitForExit(5_000);
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
		{
		}
	}
}

internal sealed record TerminalTestProcessResult(
	int ExitCode,
	string StandardOutput,
	string StandardError);
