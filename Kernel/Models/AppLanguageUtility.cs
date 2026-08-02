using System.Globalization;

namespace DevProjex.Kernel.Models;

public static class AppLanguageUtility
{
	public static AppLanguage DetectSystemLanguage()
	{
		var cultureName = CultureInfo.CurrentUICulture.Name.ToLowerInvariant();
		if (cultureName is
		    "pt-pt" or "pt-ao" or "pt-mz" or "pt-cv" or "pt-gw" or
		    "pt-st" or "pt-gq" or "pt-tl" or "pt-mo")
		{
			return AppLanguage.PtPt;
		}

		return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant() switch
		{
			"ru" => AppLanguage.Ru,
			"uz" => AppLanguage.Uz,
			"tg" => AppLanguage.Tg,
			"kk" => AppLanguage.Kk,
			"fr" => AppLanguage.Fr,
			"de" => AppLanguage.De,
			"it" => AppLanguage.It,
			"es" => AppLanguage.Es,
			"pt" => AppLanguage.Pt,
			_ => AppLanguage.En
		};
	}

	public static string ToCode(AppLanguage language) => language switch
	{
		AppLanguage.Ru => "ru",
		AppLanguage.Uz => "uz",
		AppLanguage.Tg => "tg",
		AppLanguage.Kk => "kk",
		AppLanguage.Fr => "fr",
		AppLanguage.De => "de",
		AppLanguage.It => "it",
		AppLanguage.Es => "es",
		AppLanguage.Pt => "pt",
		AppLanguage.PtPt => "pt-pt",
		_ => "en"
	};

	public static bool TryParseCode(string? code, out AppLanguage language)
	{
		language = AppLanguage.En;
		if (string.IsNullOrWhiteSpace(code))
			return false;

		var normalized = code.Trim().Replace('_', '-').ToLowerInvariant();
		foreach (var candidate in Enum.GetValues<AppLanguage>())
		{
			if (!string.Equals(ToCode(candidate), normalized, StringComparison.Ordinal))
				continue;

			language = candidate;
			return true;
		}

		return false;
	}
}
