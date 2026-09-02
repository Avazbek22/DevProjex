namespace DevProjex.Terminal.Rendering;

internal static class TerminalTextEscaping
{
	public static string EscapeSingleLine(string value) => SingleLineTextEscaping.Escape(value);

	public static void WriteSingleLine(TextWriter writer, string value)
	{
		ArgumentNullException.ThrowIfNull(writer);
		writer.WriteLine(EscapeSingleLine(value));
	}
}
