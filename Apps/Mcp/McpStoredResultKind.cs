namespace DevProjex.Mcp;

/// <summary>
/// Which tool produced a stored result. The store itself is content-agnostic; this exists only so
/// that a caller who returns with an id the session no longer holds is told how to obtain that kind
/// of result again.
/// </summary>
internal enum McpStoredResultKind
{
	Pack,
	Search
}
