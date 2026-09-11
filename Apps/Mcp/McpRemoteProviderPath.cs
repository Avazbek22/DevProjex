namespace DevProjex.Mcp;

/// <summary>
/// Path shapes that make the operating system reach a remote provider when the path is opened.
/// </summary>
/// <remarks>
/// Recognising them is pure string work: nothing here opens, probes, resolves, or canonicalises a
/// path. That matters because the answer decides whether a client-supplied string is allowed to
/// reach the filesystem at all, and a check that itself touched the filesystem would be the very
/// thing it exists to prevent.
/// <para>
/// On Windows every remote form and every device form begins with two path separators:
/// <c>\\server\share</c>, <c>//server/share</c>, <c>\\?\UNC\server\share</c>, <c>\\.\pipe\name</c>,
/// and a bare <c>\\server</c>. Both separators are treated as separators on every platform, because
/// the string arrives from a client that may have written it for another operating system and the
/// answer has to be the same wherever the server runs.
/// </para>
/// <para>
/// What this cannot see: a drive letter or mount point that an operator has already bound to a
/// remote share is indistinguishable from a local one by its spelling. That binding is the
/// operator's configuration rather than something a client chose, and it is outside what a
/// syntactic check can decide.
/// </para>
/// </remarks>
internal static class McpRemoteProviderPath
{
	/// <summary>
	/// Whether opening <paramref name="path"/> could make the operating system contact a host.
	/// </summary>
	internal static bool ReachesRemoteProvider(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return false;

		// Leading whitespace is trimmed before the shape is read, so that a padded spelling of a
		// remote form cannot present itself as an ordinary relative path.
		var candidate = path.AsSpan().Trim();
		return candidate.Length >= 2 && IsSeparator(candidate[0]) && IsSeparator(candidate[1]);
	}

	private static bool IsSeparator(char character) => character is '/' or '\\';
}
