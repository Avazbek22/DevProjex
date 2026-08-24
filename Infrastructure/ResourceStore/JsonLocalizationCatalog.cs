using System.Collections.Frozen;

namespace DevProjex.Infrastructure.ResourceStore;

public sealed class JsonLocalizationCatalog : ILocalizationCatalog
{
	private readonly IReadOnlyDictionary<AppLanguage, Lazy<IReadOnlyDictionary<string, string>>> _cache = CreateCache();

	public IReadOnlyDictionary<string, string> Get(AppLanguage language)
	{
		var resource = _cache.TryGetValue(language, out var localizedResource)
			? localizedResource
			: _cache[AppLanguage.En];
		return resource.Value;
	}

	private static IReadOnlyDictionary<AppLanguage, Lazy<IReadOnlyDictionary<string, string>>> CreateCache()
	{
		var assembly = typeof(Marker).Assembly;
		return new Dictionary<AppLanguage, Lazy<IReadOnlyDictionary<string, string>>>
		{
			[AppLanguage.Ru] = CreateResource(assembly, "ru"),
			[AppLanguage.En] = CreateResource(assembly, "en"),
			[AppLanguage.Uz] = CreateResource(assembly, "uz"),
			[AppLanguage.Tg] = CreateResource(assembly, "tg"),
			[AppLanguage.Kk] = CreateResource(assembly, "kk"),
			[AppLanguage.Fr] = CreateResource(assembly, "fr"),
			[AppLanguage.De] = CreateResource(assembly, "de"),
			[AppLanguage.It] = CreateResource(assembly, "it"),
			[AppLanguage.Es] = CreateResource(assembly, "es"),
			[AppLanguage.Pt] = CreateResource(assembly, "pt"),
			[AppLanguage.PtPt] = CreateResource(assembly, "pt-pt"),
			[AppLanguage.ZhCn] = CreateResource(assembly, "zh-cn"),
			[AppLanguage.ZhTw] = CreateResource(assembly, "zh-tw"),
			[AppLanguage.Ja] = CreateResource(assembly, "ja"),
			[AppLanguage.Ko] = CreateResource(assembly, "ko"),
			[AppLanguage.Tr] = CreateResource(assembly, "tr"),
			[AppLanguage.Uk] = CreateResource(assembly, "uk"),
			[AppLanguage.Pl] = CreateResource(assembly, "pl"),
			[AppLanguage.Vi] = CreateResource(assembly, "vi"),
			[AppLanguage.Id] = CreateResource(assembly, "id")
		}.ToFrozenDictionary();
	}

	private static Lazy<IReadOnlyDictionary<string, string>> CreateResource(Assembly assembly, string code) =>
		new(() => Load(assembly, code), LazyThreadSafetyMode.ExecutionAndPublication);

	private static IReadOnlyDictionary<string, string> Load(Assembly assembly, string code)
	{
		var resourceName = $"DevProjex.Assets.Localization.{code}.json";
		using var stream = assembly.GetManifestResourceStream(resourceName)
			?? throw new InvalidOperationException($"Localization resource not found: {resourceName}");

		var data = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
			?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		return data.ToFrozenDictionary(
			static pair => pair.Key,
			static pair => pair.Value,
			StringComparer.OrdinalIgnoreCase);
	}
}
