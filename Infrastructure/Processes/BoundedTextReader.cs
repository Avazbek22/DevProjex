using System.Buffers;

namespace DevProjex.Infrastructure.Processes;

public static class BoundedTextReader
{
	public static async Task<BoundedTextReadResult> ReadAsync(
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
				? new BoundedTextReadResult(string.Empty, ExceededLimit: true)
				: new BoundedTextReadResult(output.ToString(), ExceededLimit: false);
		}
		finally
		{
			ArrayPool<char>.Shared.Return(buffer, clearArray: true);
		}
	}

	public static async Task ObserveCompletionAsync(params Task[] readers)
	{
		try
		{
			await Task.WhenAll(readers).ConfigureAwait(false);
		}
		catch
		{
			// Process termination remains the primary failure after redirected pipes close.
		}
	}
}

public readonly record struct BoundedTextReadResult(string Text, bool ExceededLimit);
