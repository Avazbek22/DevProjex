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
		{ AppLanguage.Ru, "Поиск личных данных…" },
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

	[Fact]
	public void ManualMarkMenuFormats_KeepOneMaskedValueAndThreeDistinctScopeTooltipsInEveryLanguage()
	{
		const string maskedValue = "value…tail";
		var catalog = new JsonLocalizationCatalog();
		foreach (var language in Enum.GetValues<AppLanguage>())
		{
			var values = catalog.Get(language);
			foreach (var key in new[]
			{
				"Preview.Secret.Mark.Secret.Here",
				"Preview.Secret.Mark.Secret.Always",
				"Preview.Secret.Mark.PrivateData.Always"
			})
			{
				var formatted = string.Format(CultureInfo.InvariantCulture, values[key], maskedValue);
				Assert.Equal(1, formatted.Split(maskedValue, StringSplitOptions.None).Length - 1);
			}

			var tooltips = new[]
			{
				values["Preview.Secret.Mark.Tooltip.Here"],
				values["Preview.Secret.Mark.Tooltip.Persistent"],
				values["Preview.Secret.Mark.Tooltip.PrivateData"]
			};
			Assert.All(tooltips, static tooltip => Assert.False(string.IsNullOrWhiteSpace(tooltip)));
			Assert.Equal(3, tooltips.Distinct(StringComparer.Ordinal).Count());
		}
	}

	[Theory]
	[InlineData(AppLanguage.En, "Hide \"{0}\" here", "Always hide \"{0}\"", "Hide \"{0}\" as private data")]
	[InlineData(AppLanguage.Ru, "Скрыть \"{0}\" здесь", "Всегда скрывать \"{0}\"", "Скрывать \"{0}\" как личные данные")]
	public void ManualMarkMenuFormats_UseTheCanonicalEnglishAndRussianWording(
		AppLanguage language,
		string hideHere,
		string hideAlways,
		string privateDataAlways)
	{
		var values = new JsonLocalizationCatalog().Get(language);

		Assert.Equal(hideHere, values["Preview.Secret.Mark.Secret.Here"]);
		Assert.Equal(hideAlways, values["Preview.Secret.Mark.Secret.Always"]);
		Assert.Equal(privateDataAlways, values["Preview.Secret.Mark.PrivateData.Always"]);
	}
}
