using System.Reflection;
using System.Text.Json;
using DevProjex.Assets;

namespace DevProjex.Infrastructure.ResourceStore;

public sealed class JsonLocalizationCatalog : ILocalizationCatalog
{
	private readonly Lazy<IReadOnlyDictionary<AppLanguage, IReadOnlyDictionary<string, string>>> _cache;

	public JsonLocalizationCatalog()
	{
		_cache = new Lazy<IReadOnlyDictionary<AppLanguage, IReadOnlyDictionary<string, string>>>(LoadAll);
	}

	public IReadOnlyDictionary<string, string> Get(AppLanguage language)
	{
		var cache = _cache.Value;
		return cache.TryGetValue(language, out var dict) ? dict : cache[AppLanguage.En];
	}

	private static IReadOnlyDictionary<AppLanguage, IReadOnlyDictionary<string, string>> LoadAll()
	{
		var assembly = typeof(Marker).Assembly;
		return new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.Ru] = Load(assembly, "ru"),
			[AppLanguage.En] = Load(assembly, "en"),
			[AppLanguage.Uz] = Load(assembly, "uz"),
			[AppLanguage.Tg] = Load(assembly, "tg"),
			[AppLanguage.Kk] = Load(assembly, "kk"),
			[AppLanguage.Fr] = Load(assembly, "fr"),
			[AppLanguage.De] = Load(assembly, "de"),
			[AppLanguage.It] = Load(assembly, "it")
		};
	}

	private static IReadOnlyDictionary<string, string> Load(Assembly assembly, string code)
	{
		var resourceName = $"DevProjex.Assets.Localization.{code}.json";
		using var stream = assembly.GetManifestResourceStream(resourceName)
			?? throw new InvalidOperationException($"Localization resource not found: {resourceName}");

		var data = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
			?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		return new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase);
	}
}
