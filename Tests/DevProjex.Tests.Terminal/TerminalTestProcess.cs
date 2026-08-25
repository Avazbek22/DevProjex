using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

internal static class TerminalTestProcess
{
	private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

	public static TerminalTestProcessResult Run(
		ProcessStartInfo startInfo,
		TimeSpan? timeout = null)
	{
		ArgumentNullException.ThrowIfNull(startInfo);
		using var process = new Process { StartInfo = startInfo };
		if (!process.Start())
			throw new InvalidOperationException($"Could not start '{startInfo.FileName}'.");

		var standardOutput = process.StandardOutput.ReadToEndAsync();
		var standardError = process.StandardError.ReadToEndAsync();
		var effectiveTimeout = timeout ?? DefaultTimeout;
		if (!process.WaitForExit(checked((int)effectiveTimeout.TotalMilliseconds)))
		{
			TryKill(process);
			throw new TimeoutException(
				$"'{startInfo.FileName}' did not exit within {effectiveTimeout.TotalSeconds:0} seconds.");
		}

		Task.WhenAll(standardOutput, standardError).GetAwaiter().GetResult();
		return new TerminalTestProcessResult(
			process.ExitCode,
			standardOutput.Result,
			standardError.Result);
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
