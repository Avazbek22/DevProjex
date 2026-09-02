namespace DevProjex.Infrastructure.FileSystem;

public static class DesktopShortcutTextFormatter
{
	public static string Format(string text, DesktopPlatform platform)
	{
		ArgumentNullException.ThrowIfNull(text);

		var tokens = platform switch
		{
			DesktopPlatform.MacOS => new ShortcutTokens("⌘", "⇧", "⌥", "⌘⇧E"),
			DesktopPlatform.Windows or DesktopPlatform.Linux =>
				new ShortcutTokens("Ctrl+", "Shift+", "Alt+", "Ctrl+W"),
			_ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null)
		};

		return text
			.Replace("{mod}", tokens.Primary, StringComparison.Ordinal)
			.Replace("{shift}", tokens.Shift, StringComparison.Ordinal)
			.Replace("{alt}", tokens.Alt, StringComparison.Ordinal)
			.Replace("{collapseAll}", tokens.CollapseAll, StringComparison.Ordinal);
	}

	private readonly record struct ShortcutTokens(
		string Primary,
		string Shift,
		string Alt,
		string CollapseAll);
}
