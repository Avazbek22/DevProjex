namespace DevProjex.Tests.Unit.Helpers;

public sealed class StartupResourceLoadingTests
{
	[Fact]
	public void LocalizationCatalog_LoadsOnlyRequestedLanguageUntilFallbackIsNeeded()
	{
		var catalog = new JsonLocalizationCatalog();
		var cache = GetCache<IReadOnlyDictionary<string, string>>(catalog);

		Assert.All(cache.Values, resource => Assert.False(resource.IsValueCreated));

		Assert.NotEmpty(catalog.Get(AppLanguage.Ru));

		Assert.True(cache[AppLanguage.Ru].IsValueCreated);
		Assert.False(cache[AppLanguage.En].IsValueCreated);
		Assert.All(
			cache.Where(pair => pair.Key is not AppLanguage.Ru and not AppLanguage.En),
			pair => Assert.False(pair.Value.IsValueCreated));

		Assert.NotEmpty(catalog.Get((AppLanguage)int.MaxValue));
		Assert.True(cache[AppLanguage.En].IsValueCreated);
	}

	[Fact]
	public void HelpProvider_LoadsOnlyRequestedLanguageUntilFallbackIsNeeded()
	{
		var provider = new HelpContentProvider();
		var cache = GetCache<string>(provider);

		Assert.All(cache.Values, resource => Assert.False(resource.IsValueCreated));

		Assert.False(string.IsNullOrWhiteSpace(provider.GetHelpBody(AppLanguage.De)));

		Assert.True(cache[AppLanguage.De].IsValueCreated);
		Assert.False(cache[AppLanguage.En].IsValueCreated);
		Assert.All(
			cache.Where(pair => pair.Key is not AppLanguage.De and not AppLanguage.En),
			pair => Assert.False(pair.Value.IsValueCreated));
	}

	private static IReadOnlyDictionary<AppLanguage, Lazy<T>> GetCache<T>(object owner)
	{
		var field = owner.GetType().GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic);
		return Assert.IsAssignableFrom<IReadOnlyDictionary<AppLanguage, Lazy<T>>>(field?.GetValue(owner));
	}
}
