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

	[Theory]
	[MemberData(nameof(ExpectedSearchLabels))]
	public void SecretDiscoveryProgress_UsesAnActionPhraseInEveryLanguage(
		AppLanguage language,
		string expected)
	{
		var catalog = new JsonLocalizationCatalog();

		Assert.Equal(expected, catalog.Get(language)["Settings.Ignore.HideSecrets.Scanning"]);
	}
}
