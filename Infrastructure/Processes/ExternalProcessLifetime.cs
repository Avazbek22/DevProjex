using System.ComponentModel;
using System.Diagnostics;

namespace DevProjex.Infrastructure.Processes;

public static class ExternalProcessLifetime
{
	private static readonly TimeSpan TerminationWaitTimeout = TimeSpan.FromSeconds(5);
	private const int TerminationFallbackWaitMilliseconds = 1_000;

	public static async Task WaitForExitOrTerminateAsync(
		Process process,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(process);

		try
		{
			await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			TryKill(process, entireProcessTree: true);
			await WaitForKilledProcessExitAsync(process).ConfigureAwait(false);
			throw;
		}
	}

	private static async Task WaitForKilledProcessExitAsync(Process process)
	{
		using var terminationTimeout = new CancellationTokenSource(TerminationWaitTimeout);
		try
		{
			await process.WaitForExitAsync(terminationTimeout.Token).ConfigureAwait(false);
			return;
		}
		catch (OperationCanceledException) when (terminationTimeout.IsCancellationRequested)
		{
			// Fall through to one final bounded direct-process termination attempt.
		}
		catch (InvalidOperationException)
		{
			return;
		}
		catch (Win32Exception)
		{
			// A final direct kill can still recover from a transient process-handle race.
		}

		TryKill(process, entireProcessTree: false);
		TryWaitForExit(process);
	}

	private static void TryKill(Process process, bool entireProcessTree)
	{
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree);
		}
		catch (Exception exception) when (exception is
			       InvalidOperationException or
			       NotSupportedException or
			       Win32Exception)
		{
			// Exit can race the check, and some platforms cannot terminate descendants directly.
		}
	}

	private static void TryWaitForExit(Process process)
	{
		try
		{
			process.WaitForExit(TerminationFallbackWaitMilliseconds);
		}
		catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
		{
			// Preserve the original cancellation when the process handle is already unavailable.
		}
	}
}
