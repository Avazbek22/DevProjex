namespace DevProjex.Terminal.Tui;

internal static class TerminalProfileSourcePresentation
{
	public static string? Format(
		ProjectProfileReference? source,
		string savedSettingsLabel,
		string projectSettingsLabel,
		string fileSettingsLabel,
		int maxColumns,
		bool useUnicode)
	{
		var value = source?.Kind switch
		{
			ProjectProfileSourceKind.Local => projectSettingsLabel,
			ProjectProfileSourceKind.Portable =>
				$"{fileSettingsLabel}: {Path.GetFileName(source.Path) ?? source.Path}",
			_ => null
		};
		return value is null
			? null
			: TerminalParameterRow.FitLabel(
				$"{savedSettingsLabel}: {value}",
				maxColumns,
				useUnicode);
	}
}
