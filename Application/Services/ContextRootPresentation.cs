namespace DevProjex.Application.Services;

public static class ContextRootPresentation
{
	public const string Prefix = "Root: ";

	public static string FormatLine(string path) =>
		Prefix + SingleLineTextEscaping.Escape(path);

	public static string FormatMarkdownLine(string path) =>
		Prefix + MarkdownInlineLiteralEncoder.Encode(path);
}
