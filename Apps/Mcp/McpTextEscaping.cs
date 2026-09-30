namespace DevProjex.Mcp;

internal static class McpTextEscaping
{
	public static string EscapeSingleLine(string value) => SingleLineTextEscaping.Escape(value);

	/// <summary>
	/// Escapes one quoted source line of a search result. Tabs stay tabs so the quoted code copies
	/// back exactly; line breaks and other control characters are still escaped so each result line
	/// stays one physical line.
	/// </summary>
	public static string EscapeSourceLine(string value) =>
		SingleLineTextEscaping.Escape(value, preserveTabs: true);
}
