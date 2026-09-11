namespace DevProjex.Mcp;

/// <summary>
/// Path shapes that can make the operating system reach a remote provider when the path is opened.
/// </summary>
/// <remarks>
/// Recognising them is pure string work: nothing here opens, probes, resolves, or canonicalises a
/// path. That matters because the answer decides whether a client-supplied string is allowed to
/// reach the filesystem at all, and a check that itself touched the filesystem would be the very
/// thing it exists to prevent.
/// <para>
/// Four shapes qualify. <c>\\server\share</c> and <c>//server/share</c> name a host directly. The
/// Win32 device namespace <c>\\?\</c> and <c>\\.\</c> addresses this machine, so
/// <c>\\?\C:\project</c> is an ordinary local directory written the long way and
/// <c>\\.\pipe\name</c> is local machinery; only <c>UNC</c> inside it leaves the machine. The NT
/// object namespace <c>\??\</c> begins with a single separator and is refused whole: it reaches the
/// redirector through <c>\??\UNC\</c> and again through <c>\??\GLOBALROOT\Device\Mup\</c>, and it
/// admits further aliases that no spelling check can enumerate, while nothing legitimate addresses
/// a project that way. Finally the autofs host maps <c>/net/host/…</c> and
/// <c>/Network/Servers/host/…</c> mount on first access, so naming one contacts that host.
/// </para>
/// <para>
/// Both separators count on every platform, because the string arrives from a client that may have
/// written it for another operating system and the answer has to be the same wherever the server
/// runs.
/// </para>
/// <para>
/// What this cannot see: a drive letter or mount point that an operator has already bound to a
/// remote share is indistinguishable from a local one by its spelling, and so is a current
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

		// Leading whitespace is trimmed before the shape is read, so that a padded spelling of a
		// remote form cannot present itself as an ordinary relative path.
		var candidate = path.AsSpan().Trim();
		if (candidate.Length < 2 || !IsSeparator(candidate[0]))
			return false;

		return IsSeparator(candidate[1])
			? ReachesThroughTwoSeparators(candidate[2..])
			: IsNtObjectNamespace(candidate[1..]) || IsAutomountHostMap(candidate[1..]);
	}

	/// <summary>
	/// What two leading separators introduce: the Win32 device namespace, whose only remote entry is
	/// the UNC provider, or otherwise a host name.
	/// </summary>
	private static bool ReachesThroughTwoSeparators(ReadOnlySpan<char> rest) =>
		!IsWin32DeviceNamespace(rest) || NamesSegment(rest[2..], "UNC");

	private static bool IsWin32DeviceNamespace(ReadOnlySpan<char> rest) =>
		rest.Length >= 2 && rest[0] is '?' or '.' && IsSeparator(rest[1]);

	/// <summary>
	/// The NT object namespace, written with one separator: <c>\??\</c>.
	/// </summary>
	private static bool IsNtObjectNamespace(ReadOnlySpan<char> afterSeparator) =>
		afterSeparator.Length >= 3 &&
		afterSeparator[0] == '?' &&
		afterSeparator[1] == '?' &&
		IsSeparator(afterSeparator[2]);

	/// <summary>
	/// The automount host maps, which mount on first access and so contact the host named in the
	/// segment after them. The segment has to be present: the mount point itself is local.
	/// </summary>
	private static bool IsAutomountHostMap(ReadOnlySpan<char> afterSeparator) =>
		NamesSegment(afterSeparator, "net") ||
		(NamesSegment(afterSeparator, "Network") && NamesSegment(afterSeparator[8..], "Servers"));

	/// <summary>
	/// Whether the span begins with <paramref name="name"/> as a whole path segment followed by
	/// something, so that a directory called <c>UNCertain</c> or <c>network</c> is not mistaken for
	/// one of the names above.
	/// </summary>
	private static bool NamesSegment(ReadOnlySpan<char> value, string name) =>
		value.Length > name.Length &&
		value[..name.Length].Equals(name, StringComparison.OrdinalIgnoreCase) &&
		IsSeparator(value[name.Length]);

	private static bool IsSeparator(char character) => character is '/' or '\\';
}
