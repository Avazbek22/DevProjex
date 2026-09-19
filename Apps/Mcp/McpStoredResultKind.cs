namespace DevProjex.Mcp;

/// <summary>
/// Which tool produced a stored result. The store itself is content-agnostic; this exists only so
/// that a caller who returns with an id the session no longer holds is told how to obtain that kind
/// of result again.
/// </summary>
internal enum McpStoredResultKind
{
	Pack,
	Search,
	Related
}

internal static class McpStoredResultAdvice
{
	public static string? RefreshTool(McpToolSet toolSet, McpStoredResultKind kind) => kind switch
	{
		McpStoredResultKind.Pack when toolSet == McpToolSet.Full => "pack_context",
		McpStoredResultKind.Search => "search_project",
		McpStoredResultKind.Related => "related_files",
		_ => null
	};

	public static string ResultName(McpStoredResultKind kind) => kind switch
	{
		McpStoredResultKind.Search => "search result",
		McpStoredResultKind.Related => "related-files result",
		_ => "pack"
	};
}
