using DevProjex.Infrastructure.Processes;

namespace DevProjex.Infrastructure.Git;

internal static class GitProcessOutputReader
{
	internal const int MaximumOutputCharacters = 1024 * 1024;

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
}

internal readonly record struct GitProcessOutput(string Text, bool ExceededLimit);
