using System.Diagnostics;
using DevProjex.Infrastructure.Processes;

namespace DevProjex.Infrastructure.Git;

internal static class GitProcessOutputReader
{
	internal const int MaximumOutputCharacters = 1024 * 1024;
	private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(2);

	internal static async Task<GitProcessOutput> ReadAsync(
		TextReader reader,
		int maximumCharacters,
		CancellationToken cancellationToken)
	{
		var result = await BoundedTextReader
			.ReadAsync(reader, maximumCharacters, cancellationToken)
			.ConfigureAwait(false);
		return new GitProcessOutput(result.Text, result.ExceededLimit);
	}

	internal static async Task ObserveCompletionAsync(params Task[] readers)
	{
		await BoundedTextReader.ObserveCompletionAsync(readers).ConfigureAwait(false);
	}

	internal static Task<bool> WaitForCompletionAfterExitAsync(
		Process process,
		params Task[] readers) =>
		WaitForCompletionAfterExitAsync(
			() => CloseRedirectedReaders(process),
			OutputDrainTimeout,
			readers);

	internal static Task ObserveAfterTerminationAsync(
		Process process,
		params Task[] readers) =>
		ObserveAfterTerminationAsync(
			() => CloseRedirectedReaders(process),
			OutputDrainTimeout,
			readers);

	internal static async Task<bool> WaitForCompletionAfterExitAsync(
		Action closeReaders,
		TimeSpan timeout,
		params Task[] readers)
	{
		ArgumentNullException.ThrowIfNull(closeReaders);
		if (timeout <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(timeout));

		try
		{
			await Task.WhenAll(readers).WaitAsync(timeout).ConfigureAwait(false);
			return true;
		}
		catch (TimeoutException)
		{
			closeReaders();
			await ObserveBoundedAsync(timeout, readers).ConfigureAwait(false);
			return false;
		}
	}

	internal static async Task ObserveAfterTerminationAsync(
		Action closeReaders,
		TimeSpan timeout,
		params Task[] readers)
	{
		ArgumentNullException.ThrowIfNull(closeReaders);
		if (timeout <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(timeout));

		closeReaders();
		await ObserveBoundedAsync(timeout, readers).ConfigureAwait(false);
	}

	private static async Task ObserveBoundedAsync(TimeSpan timeout, Task[] readers)
	{
		try
		{
			await ObserveCompletionAsync(readers).WaitAsync(timeout).ConfigureAwait(false);
		}
		catch (TimeoutException)
		{
			// The primary process is already gone. A descendant may still own a copied pipe
			// handle, so cleanup must not retain a repository lock indefinitely.
		}
	}

	private static void CloseRedirectedReaders(Process process)
	{
		ArgumentNullException.ThrowIfNull(process);
		TryDispose(process.StandardOutput);
		TryDispose(process.StandardError);
	}

	private static void TryDispose(IDisposable reader)
	{
		try
		{
			reader.Dispose();
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException)
		{
		}
	}
}

internal readonly record struct GitProcessOutput(string Text, bool ExceededLimit);
