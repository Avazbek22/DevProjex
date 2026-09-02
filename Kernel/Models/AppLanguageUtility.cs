using System.Globalization;

namespace DevProjex.Kernel.Models;

public static class AppLanguageUtility
{
	public static AppLanguage DetectSystemLanguage() => DetectSystemLanguage(CultureInfo.CurrentUICulture);

	internal static AppLanguage DetectSystemLanguage(CultureInfo culture)
	{
		ArgumentNullException.ThrowIfNull(culture);

		var cultureName = culture.Name.ToLowerInvariant();
		if (cultureName is "zh-tw" or "zh-hk" or "zh-mo" ||
		    cultureName.StartsWith("zh-hant", StringComparison.Ordinal))
		{
			return AppLanguage.ZhTw;
		}

		if (cultureName is "zh" or "zh-cn" or "zh-sg" ||
		    cultureName.StartsWith("zh-hans", StringComparison.Ordinal))
		{
			return AppLanguage.ZhCn;
		}

		if (cultureName is
		    "pt-pt" or "pt-ao" or "pt-mz" or "pt-cv" or "pt-gw" or
		    "pt-st" or "pt-gq" or "pt-tl" or "pt-mo")
		{
			return AppLanguage.PtPt;
		}

		return culture.TwoLetterISOLanguageName.ToLowerInvariant() switch
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
			"ja" => AppLanguage.Ja,
			"ko" => AppLanguage.Ko,
			"tr" => AppLanguage.Tr,
			"uk" => AppLanguage.Uk,
			"pl" => AppLanguage.Pl,
			"vi" => AppLanguage.Vi,
			"id" => AppLanguage.Id,
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
		AppLanguage.ZhCn => "zh-cn",
		AppLanguage.ZhTw => "zh-tw",
		AppLanguage.Ja => "ja",
		AppLanguage.Ko => "ko",
		AppLanguage.Tr => "tr",
		AppLanguage.Uk => "uk",
		AppLanguage.Pl => "pl",
		AppLanguage.Vi => "vi",
		AppLanguage.Id => "id",
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
