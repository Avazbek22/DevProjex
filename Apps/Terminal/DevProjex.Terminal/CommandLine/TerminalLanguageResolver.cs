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
		value is not null &&
		CliChoiceSets.Language.TryParse(value, out var language)
			? language
			: AppLanguage.En;
}
