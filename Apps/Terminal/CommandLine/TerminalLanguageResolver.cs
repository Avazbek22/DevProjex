namespace DevProjex.Terminal.CommandLine;

internal static class TerminalLanguageResolver
{
	public static AppLanguage Resolve(
		IReadOnlyList<string> arguments,
		IReadOnlyDictionary<string, string?>? variables = null)
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

		if (variables is not null &&
		    variables.TryGetValue("DEVPROJEX_LANGUAGE", out var configuredLanguage) &&
		    configuredLanguage is not null &&
		    CliChoiceSets.Language.TryParse(configuredLanguage, out var environmentLanguage))
		{
			return environmentLanguage;
		}

		return AppLanguageUtility.DetectSystemLanguage();
	}

	public static bool TryFindUnsupportedExplicitLanguage(
		IReadOnlyList<string> arguments,
		out string value)
	{
		for (var index = 0; index < arguments.Count; index++)
		{
			var token = arguments[index];
			string? candidate = null;
			if (token.StartsWith("--language=", StringComparison.Ordinal))
				candidate = token["--language=".Length..];
			else if (token == "--language" && index + 1 < arguments.Count)
				candidate = arguments[index + 1];
			else if (token == "--")
				break;
			if (candidate is null)
				continue;
			if (CliChoiceSets.Language.TryParse(candidate, out _))
				break;
			value = candidate;
			return true;
		}

		value = string.Empty;
		return false;
	}

	public static AppLanguage ParseOrDefault(string? value) =>
		value is not null &&
		CliChoiceSets.Language.TryParse(value, out var language)
			? language
			: AppLanguage.En;
}
