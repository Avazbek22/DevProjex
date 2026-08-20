using System.Buffers;
using System.Text;

namespace DevProjex.Infrastructure.Git;

internal static class GitProcessOutputReader
{
	internal const int MaximumOutputCharacters = 1024 * 1024;

	internal static async Task<GitProcessOutput> ReadAsync(
		TextReader reader,
		int maximumCharacters,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(reader);
		if (maximumCharacters <= 0)
			throw new ArgumentOutOfRangeException(nameof(maximumCharacters));

		var buffer = ArrayPool<char>.Shared.Rent(Math.Min(4096, maximumCharacters));
		var output = new StringBuilder(Math.Min(4096, maximumCharacters));
		var exceededLimit = false;
		try
		{
			while (true)
			{
				var read = await reader
					.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
					.ConfigureAwait(false);
				if (read == 0)
					break;

				var remaining = maximumCharacters - output.Length;
				if (remaining > 0)
					output.Append(buffer, 0, Math.Min(read, remaining));
				if (read > remaining)
					exceededLimit = true;
			}

			return exceededLimit
				? new GitProcessOutput(string.Empty, ExceededLimit: true)
				: new GitProcessOutput(output.ToString(), ExceededLimit: false);
		}
		finally
		{
			ArrayPool<char>.Shared.Return(buffer, clearArray: true);
		}
	}

	internal static async Task ObserveCompletionAsync(params Task[] readers)
	{
		try
		{
			await Task.WhenAll(readers).ConfigureAwait(false);
		}
		catch
		{
			// The process cancellation remains the primary failure after both pipes release resources.
		}
	}
}

internal readonly record struct GitProcessOutput(string Text, bool ExceededLimit);
