namespace DevProjex.Terminal.CommandLine;

internal static class TerminalLanguageResolver
{
	public static AppLanguage Resolve(IReadOnlyList<string> arguments)
	{
		for (var index = 0; index < arguments.Count; index++)
		{
			var token = arguments[index];
			if (token.StartsWith("--language=", StringComparison.Ordinal))
				return ParseOrDefault(token["--language=".Length..]);
			if (token == "--language" && index + 1 < arguments.Count)
				return ParseOrDefault(arguments[index + 1]);
			if (token == "--")
				break;
		}

		return AppLanguageUtility.DetectSystemLanguage();
	}

	public static AppLanguage ParseOrDefault(string? value) =>
		value?.ToLowerInvariant() switch
		{
			"ru" => AppLanguage.Ru,
			"de" => AppLanguage.De,
			"fr" => AppLanguage.Fr,
			"it" => AppLanguage.It,
			"es" => AppLanguage.Es,
			"pt" => AppLanguage.Pt,
			"pt-pt" => AppLanguage.PtPt,
			"kk" => AppLanguage.Kk,
			"tg" => AppLanguage.Tg,
			"uz" => AppLanguage.Uz,
			_ => AppLanguage.En
		};
}
