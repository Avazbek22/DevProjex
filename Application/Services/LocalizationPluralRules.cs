namespace DevProjex.Application.Services;

/// <summary>
/// Selects the <c>One</c>, <c>Few</c>, <c>Many</c> or <c>Other</c> suffix of a counted localization key.
/// </summary>
public static class LocalizationPluralRules
{
	public static string ResolveKey(string key, AppLanguage language, long count) =>
		$"{key}.{ResolveCategory(language, count)}";

	public static string ResolveCategory(AppLanguage language, long count)
	{
		var absolute = count == long.MinValue ? long.MaxValue : Math.Abs(count);
		var modulo10 = absolute % 10;
		var modulo100 = absolute % 100;
		return language switch
		{
			AppLanguage.Ru or AppLanguage.Uk => modulo10 == 1 && modulo100 != 11
				? "One"
				: modulo10 is >= 2 and <= 4 && modulo100 is not (>= 12 and <= 14)
					? "Few"
					: "Many",
			AppLanguage.Pl => absolute == 1
				? "One"
				: modulo10 is >= 2 and <= 4 && modulo100 is not (>= 12 and <= 14)
					? "Few"
					: "Many",
			AppLanguage.Fr => absolute is 0 or 1 ? "One" : "Other",
			_ => absolute == 1 ? "One" : "Other"
		};
	}
}
