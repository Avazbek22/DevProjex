using System.Diagnostics;
using System.Text;

namespace DevProjex.RankingEval;

internal sealed record BoundedProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal static class BoundedProcessRunner
{
	internal static async Task<BoundedProcessResult> RunAsync(
		ProcessStartInfo startInfo,
		TimeSpan deadline,
		int maximumOutputCharacters,
		CancellationToken cancellationToken,
		Action<int>? processStarted = null)
	{
		ArgumentNullException.ThrowIfNull(startInfo);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deadline, TimeSpan.Zero);
		ArgumentOutOfRangeException.ThrowIfLessThan(maximumOutputCharacters, 1);
		using var process = Process.Start(startInfo) ??
		                    throw new InvalidOperationException($"Could not start '{startInfo.FileName}'.");
		processStarted?.Invoke(process.Id);
		var outputTask = ReadBoundedAsync(process.StandardOutput, maximumOutputCharacters);
		var errorTask = ReadBoundedAsync(process.StandardError, maximumOutputCharacters);
		using var timeout = new CancellationTokenSource(deadline);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
		try
		{
			await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || timeout.IsCancellationRequested)
		{
			await KillAndReapAsync(process).ConfigureAwait(false);
			await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			throw new TimeoutException($"Process '{startInfo.FileName}' exceeded its {deadline} deadline.");
		}

		var output = await outputTask.ConfigureAwait(false);
		var error = await errorTask.ConfigureAwait(false);
		if (output.Truncated || error.Truncated)
		{
			throw new InvalidDataException(
				$"Process '{startInfo.FileName}' exceeded the {maximumOutputCharacters} character output limit.");
		}
		return new BoundedProcessResult(process.ExitCode, output.Text, error.Text);
	}

	private static async Task KillAndReapAsync(Process process)
	{
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
		}
		catch (InvalidOperationException)
		{
			// The process exited between the status check and Kill.
		}
		await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
	}

	private static async Task<BoundedText> ReadBoundedAsync(StreamReader reader, int maximumCharacters)
	{
		var builder = new StringBuilder(Math.Min(maximumCharacters, 16 * 1024));
		var buffer = new char[4096];
		var truncated = false;
		while (true)
		{
			var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
			if (read == 0)
				break;
			var remaining = maximumCharacters - builder.Length;
			if (remaining > 0)
				builder.Append(buffer, 0, Math.Min(read, remaining));
			if (read > remaining)
				truncated = true;
		}
		return new BoundedText(builder.ToString(), truncated);
	}

	private readonly record struct BoundedText(string Text, bool Truncated);
}
