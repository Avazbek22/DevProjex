namespace DevProjex.Mcp;

/// <summary>
/// Whether a client-supplied path may be handed to the filesystem, decided from its spelling.
/// </summary>
/// <remarks>
/// Deciding this is pure string work: nothing here opens, probes, resolves, or canonicalises a
/// path. That matters because the answer decides whether a client-supplied string is allowed to
/// reach the filesystem at all, and a check that itself touched the filesystem would be the very
/// thing it exists to prevent.
/// <para>
/// The question is answered by listing what is accepted rather than what is refused. Windows keeps
/// more than one spelling for the same object: <c>\\server\share</c> reaches a host directly,
/// <c>\\?\UNC\server\share</c> reaches it through the Win32 device namespace, and
/// <c>\\?\GLOBALROOT\Device\Mup\server\share</c> reaches the same redirector through the object
/// namespace that <c>\\?\</c> and <c>\??\</c> both open onto. That namespace holds symbolic links
/// and aliases of aliases, so no list of refused spellings can be finished. A list of accepted
/// ones can: a drive, a volume, and a named pipe are the only device forms a project path needs.
/// </para>
/// <para>
/// Outside the device namespace the accepted set is everything ordinary — a relative path, a
/// drive-rooted path, a POSIX absolute path. Refused there are the object namespace <c>\??\</c>,
/// which is the same door under one separator, and the automount host maps <c>/net/host/…</c> and
/// <c>/Network/Servers/host/…</c>, which mount on first access and so contact the host named in
/// them. Both separators count on every platform, and so do the host maps, because the string
/// arrives from a client that may have written it for another operating system and the answer has
/// to be the same wherever the server runs. That is deliberately conservative: on Windows those two
/// maps are ordinary drive-relative directories, and a server that has one as a root can still
/// address it, because a listed root is exempt under any spelling of itself.
/// </para>
/// <para>
/// What this cannot see: a drive letter or mount point that an operator has already bound to a
/// remote share is indistinguishable from a local one by its spelling, and so is a working
/// directory that sits on one. Those are the operator's configuration rather than something a
/// client chose, and they are outside what a syntactic check can decide.
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

		// Leading whitespace is trimmed before the shape is read, so that a padded spelling cannot
		// present itself as an ordinary relative path.
		var candidate = path.AsSpan().Trim();
		if (candidate.Length < 2 || !IsSeparator(candidate[0]))
			return false;

		if (IsSeparator(candidate[1]))
		{
			var rest = candidate[2..];
			return !IsDeviceNamespace(rest) || !NamesLocalDevice(rest[2..]);
		}

		var afterSeparator = candidate[1..];
		return IsObjectNamespace(afterSeparator) || IsAutomountHostMap(afterSeparator);
	}

	/// <summary>
	/// The Win32 device namespace, which the third character marks and the fourth confirms:
	/// <c>?\</c> or <c>.\</c>.
	/// </summary>
	private static bool IsDeviceNamespace(ReadOnlySpan<char> rest) =>
		rest.Length >= 2 && rest[0] is '?' or '.' && IsSeparator(rest[1]);

	/// <summary>
	/// The device forms a project path is allowed to use, all of which address this machine: a
	/// drive, a volume, and a named pipe. Everything else in that namespace is refused, including
	/// <c>UNC</c> and <c>GLOBALROOT</c>, because it either names a host or can be made to.
	/// </summary>
	private static bool NamesLocalDevice(ReadOnlySpan<char> afterPrefix) =>
		NamesDrive(afterPrefix) ||
		afterPrefix.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase) ||
		NamesSegment(afterPrefix, "pipe");

	private static bool NamesDrive(ReadOnlySpan<char> value) =>
		value.Length >= 2 &&
		char.IsAsciiLetter(value[0]) &&
		value[1] == ':' &&
		(value.Length == 2 || IsSeparator(value[2]));

	/// <summary>
	/// The NT object namespace, written with one separator: <c>\??\</c>. It opens onto the same
	/// aliases as <c>\\?\GLOBALROOT\</c>, so it is refused whole.
	/// </summary>
	private static bool IsObjectNamespace(ReadOnlySpan<char> afterSeparator) =>
		afterSeparator.Length >= 3 &&
		afterSeparator[0] == '?' &&
		afterSeparator[1] == '?' &&
		IsSeparator(afterSeparator[2]);

	/// <summary>
	/// The automount host maps, which mount on first access and so contact the host named in the
	/// segment after them. That segment has to be present: the mount point itself is local.
	/// </summary>
	private static bool IsAutomountHostMap(ReadOnlySpan<char> afterSeparator) =>
		NamesSegment(afterSeparator, "net") ||
		(NamesSegment(afterSeparator, "Network") && NamesSegment(afterSeparator[8..], "Servers"));

	/// <summary>
	/// Whether the span begins with <paramref name="name"/> as a whole path segment followed by
	/// something, so that a directory called <c>network</c> or a device called <c>pipex</c> is not
	/// mistaken for one of the names above.
	/// </summary>
	private static bool NamesSegment(ReadOnlySpan<char> value, string name) =>
		value.Length > name.Length &&
		value[..name.Length].Equals(name, StringComparison.OrdinalIgnoreCase) &&
		IsSeparator(value[name.Length]);

	private static bool IsSeparator(char character) => character is '/' or '\\';
}
