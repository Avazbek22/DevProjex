namespace DevProjex.Mcp;

internal static class McpTextEscaping
{
	public static string EscapeSingleLine(string value) => SingleLineTextEscaping.Escape(value);
}
