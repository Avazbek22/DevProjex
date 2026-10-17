namespace DevProjex.Infrastructure.FileSystem;

public sealed class FlatpakHostPathResolver(Func<string, CancellationToken, Task<string?>>? query = null)
{
	private readonly Func<string, CancellationToken, Task<string?>> _query = query ?? ((_, _) => Task.FromResult<string?>(null));

	public async Task<string> ResolveAsync(string path, CancellationToken cancellationToken = default)
	{
		if (!FlatpakEnvironment.Current.IsActive || !TryGetDocument(path, out var id, out var suffix))
			return path;
		try
		{
			var host = await _query(id, cancellationToken).ConfigureAwait(false);
			return host is not null && host.StartsWith('/') && host.IndexOf('\0') < 0
				? host.TrimEnd('/') + suffix
				: path;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception) when (exception is IOException or
			TimeoutException or InvalidOperationException or UnauthorizedAccessException)
		{
			return path;
		}
	}

	public static bool TryGetDocument(string path, out string id, out string suffix)
	{
		id = suffix = string.Empty;
		var parts = path.Split('/');
		if (parts.Length < 7 || parts[1] != "run" || parts[2] != "user" ||
			parts[3].Length == 0 || !parts[3].All(char.IsAsciiDigit) || parts[4] != "doc" || parts[5].Length == 0 ||
			parts.Any(static part => part is "." or ".."))
			return false;
		id = parts[5];
		// GetHostPaths names the exported entry, not the mount's directory containing that entry.
		suffix = parts.Length > 7 ? "/" + string.Join('/', parts.Skip(7)) : string.Empty;
		return true;
	}

}
