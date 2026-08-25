namespace DevProjex.Terminal.Rendering;

internal static class TerminalTextEscaping
{
	public static string EscapeSingleLine(string value) => SingleLineTextEscaping.Escape(value);
}
