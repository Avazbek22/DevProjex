namespace DevProjex.Terminal.Rendering;

/// <summary>
/// The first line of every human-readable command error: the localized "error" label followed by
/// the language-independent DPX code, so parser and domain failures read the same way.
/// </summary>
internal static class TerminalErrorHeader
{
	public static string Format(LocalizationService localization, string code)
	{
		ArgumentNullException.ThrowIfNull(localization);
		return $"{localization["Terminal.Label.Error"]}[{code}]:";
	}
}
