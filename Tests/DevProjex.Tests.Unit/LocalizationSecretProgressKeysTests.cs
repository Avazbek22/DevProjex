namespace DevProjex.Tests.Unit;

public sealed class LocalizationSecretProgressKeysTests
{
	public static TheoryData<AppLanguage, string> ExpectedSearchLabels => new()
	{
		{ AppLanguage.En, "Searching for secrets…" },
		{ AppLanguage.Ru, "Поиск секретов…" },
		{ AppLanguage.De, "Suche nach Geheimnissen…" },
		{ AppLanguage.Fr, "Recherche de secrets…" },
		{ AppLanguage.It, "Ricerca di segreti…" },
		{ AppLanguage.Es, "Buscando secretos…" },
		{ AppLanguage.Pt, "Procurando segredos…" },
		{ AppLanguage.PtPt, "A procurar segredos…" },
		{ AppLanguage.Kk, "Құпияларды іздеу…" },
		{ AppLanguage.Tg, "Ҷустуҷӯи сирҳо…" },
		{ AppLanguage.Uz, "Sirlarni qidirish…" }
	};

	public static TheoryData<AppLanguage, string> ExpectedPrivateDataSearchLabels => new()
	{
		{ AppLanguage.En, "Searching for private data…" },
		{ AppLanguage.Ru, "Поиск приватных данных…" },
		{ AppLanguage.De, "Suche nach privaten Daten…" },
		{ AppLanguage.Fr, "Recherche de données privées…" },
		{ AppLanguage.It, "Ricerca di dati privati…" },
		{ AppLanguage.Es, "Buscando datos privados…" },
		{ AppLanguage.Pt, "Procurando dados privados…" },
		{ AppLanguage.PtPt, "A procurar dados privados…" },
		{ AppLanguage.Kk, "Жеке деректерді іздеу…" },
		{ AppLanguage.Tg, "Ҷустуҷӯи маълумоти хусусӣ…" },
		{ AppLanguage.Uz, "Shaxsiy ma’lumotlar qidirilmoqda…" }
	};

	[Theory]
	[MemberData(nameof(ExpectedSearchLabels))]
	public void SecretDiscoveryProgress_UsesAnActionPhraseInEveryLanguage(
		AppLanguage language,
		string expected)
	{
		var catalog = new JsonLocalizationCatalog();

		Assert.Equal(expected, catalog.Get(language)["Settings.Ignore.HideSecrets.Scanning"]);
	}

	[Theory]
	[MemberData(nameof(ExpectedPrivateDataSearchLabels))]
	public void PrivateDataDiscoveryProgress_UsesAnActionPhraseInEveryLanguage(
		AppLanguage language,
		string expected)
	{
		var catalog = new JsonLocalizationCatalog();

		Assert.Equal(expected, catalog.Get(language)["Settings.Ignore.HidePrivateData.Scanning"]);
	}
}
