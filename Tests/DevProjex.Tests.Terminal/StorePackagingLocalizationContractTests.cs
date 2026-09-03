using System.Xml.Linq;
using DevProjex.Kernel.Models;

namespace DevProjex.Tests.Terminal;

public sealed class StorePackagingLocalizationContractTests
{
	private const string EnglishPackageLanguage = "en-US";

	// AppLanguage code -> the BCP-47 tag declared in Package.appxmanifest, which is also the
	// Strings/<locale> folder name. A new AppLanguage value must be added here together with a
	// manifest <Resource> entry and a translated Resources.resw; otherwise Partner Center
	// demotes that language to an "additional Store listing language" without a localized
	// package name and description.
	private static readonly IReadOnlyDictionary<string, string> PackageLanguageByAppCode =
		new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["en"] = EnglishPackageLanguage,
			["ru"] = "ru-RU",
			["uz"] = "uz-Latn-UZ",
			["tg"] = "tg-Cyrl-TJ",
			["kk"] = "kk-KZ",
			["fr"] = "fr-FR",
			["de"] = "de-DE",
			["it"] = "it-IT",
			["es"] = "es-ES",
			["pt"] = "pt-BR",
			["pt-pt"] = "pt-PT",
			["zh-cn"] = "zh-CN",
			["zh-tw"] = "zh-TW",
			["ja"] = "ja-JP",
			["ko"] = "ko-KR",
			["tr"] = "tr-TR",
			["uk"] = "uk-UA",
			["pl"] = "pl-PL",
			["vi"] = "vi-VN",
			["id"] = "id-ID"
		};

	[Fact]
	public void StoreManifestDeclaresEveryAppLanguage()
	{
		var expected = Enum.GetValues<AppLanguage>()
			.Select(static language =>
			{
				var code = AppLanguageUtility.ToCode(language);
				Assert.True(
					PackageLanguageByAppCode.TryGetValue(code, out var packageLanguage),
					$"App language '{code}' has no Store package language mapping.");
				return packageLanguage!;
			})
			.Order(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		var declared = ReadDeclaredManifestLanguages();

		Assert.Equal(
			declared.Length,
			declared.Distinct(StringComparer.OrdinalIgnoreCase).Count());
		Assert.Equal(
			expected,
			declared.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
			StringComparer.OrdinalIgnoreCase);
	}

	[Fact]
	public void EveryDeclaredPackageLanguageShipsPackageStrings()
	{
		var declared = ReadDeclaredManifestLanguages();
		var localeFolders = EnumerateStringsLocaleFolders();

		Assert.Equal(
			declared.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
			localeFolders.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
			StringComparer.OrdinalIgnoreCase);

		var publisherNames = new HashSet<string>(StringComparer.Ordinal);
		foreach (var localeFolder in localeFolders)
		{
			var values = ReadPackageStrings(localeFolder);
			Assert.Equal("DevProjex", values["AppDisplayName"]);
			Assert.False(
				string.IsNullOrWhiteSpace(values["AppDescription"]),
				$"Store package description for {localeFolder} is empty.");
			publisherNames.Add(values["PublisherDisplayName"]);
		}

		Assert.Single(publisherNames);
	}

	[Fact]
	public void NonEnglishPackageDescriptionsAreTranslated()
	{
		var englishDescription = ReadPackageStrings(EnglishPackageLanguage)["AppDescription"];

		foreach (var localeFolder in EnumerateStringsLocaleFolders())
		{
			if (string.Equals(localeFolder, EnglishPackageLanguage, StringComparison.OrdinalIgnoreCase))
				continue;

			Assert.False(
				string.Equals(
					englishDescription,
					ReadPackageStrings(localeFolder)["AppDescription"],
					StringComparison.Ordinal),
				$"Store package description for {localeFolder} still uses the English text.");
		}
	}

	[Fact]
	public void PackageProjectDiscoversLocalizedStringsByGlob()
	{
		var packageProject = XDocument.Load(
			Path.Combine(GetStorePackageRoot(), "DevProjex.Store.wapproj"));

		Assert.Contains(
			packageProject
				.Descendants()
				.Where(static element => element.Name.LocalName == "PRIResource"),
			static element => element.Attribute("Include")?.Value == @"Strings\**\*.resw");
	}

	private static string GetStorePackageRoot()
		=> Path.Combine(
			PublishedApplicationLocator.FindRepositoryRoot(),
			"Packaging",
			"Windows",
			"DevProjex.Store");

	private static string[] ReadDeclaredManifestLanguages()
	{
		var manifest = XDocument.Load(
			Path.Combine(GetStorePackageRoot(), "Package.appxmanifest"));
		var declared = manifest.Root!
			.Elements()
			.Where(static element => element.Name.LocalName == "Resources")
			.SelectMany(static element => element.Elements())
			.Where(static element => element.Name.LocalName == "Resource")
			.Select(static element => element.Attribute("Language")?.Value)
			.Where(static value => !string.IsNullOrWhiteSpace(value))
			.Cast<string>()
			.ToArray();

		Assert.NotEmpty(declared);
		return declared;
	}

	private static string[] EnumerateStringsLocaleFolders()
		=> Directory
			.EnumerateDirectories(Path.Combine(GetStorePackageRoot(), "Strings"))
			.Select(static path => Path.GetFileName(path)!)
			.ToArray();

	private static IReadOnlyDictionary<string, string> ReadPackageStrings(string packageLanguage)
	{
		var localePath = Directory
			.EnumerateDirectories(Path.Combine(GetStorePackageRoot(), "Strings"))
			.Single(path => string.Equals(
				Path.GetFileName(path),
				packageLanguage,
				StringComparison.OrdinalIgnoreCase));
		var document = XDocument.Load(Path.Combine(localePath, "Resources.resw"));

		return document.Root!
			.Elements("data")
			.ToDictionary(
				static element => element.Attribute("name")!.Value,
				static element => element.Element("value")!.Value,
				StringComparer.Ordinal);
	}
}
