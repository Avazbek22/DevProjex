namespace DevProjex.Tests.Unit.Helpers;

public sealed class StartupResourceLoadingTests
{
	[Fact]
	public void LocalizationCatalog_SharesEachImmutableLanguageResourceAcrossInstances()
	{
		var first = new JsonLocalizationCatalog();
		var second = new JsonLocalizationCatalog();

		Assert.Same(first.Get(AppLanguage.Ru), second.Get(AppLanguage.Ru));
		Assert.Same(
			first.Get((AppLanguage)int.MaxValue),
			second.Get(AppLanguage.En));
	}

	[Fact]
	public void LocalizationCatalog_CacheFactoryKeepsUnrequestedLanguagesLazy()
	{
		var factory = typeof(JsonLocalizationCatalog).GetMethod(
			"CreateCache",
			BindingFlags.Static | BindingFlags.NonPublic);
		var cache = Assert.IsAssignableFrom<
			IReadOnlyDictionary<AppLanguage, Lazy<IReadOnlyDictionary<string, string>>>>(
			factory?.Invoke(null, null));

		Assert.All(cache.Values, resource => Assert.False(resource.IsValueCreated));
		Assert.NotEmpty(cache[AppLanguage.Ru].Value);
		Assert.True(cache[AppLanguage.Ru].IsValueCreated);
		Assert.All(
			cache.Where(static pair => pair.Key != AppLanguage.Ru),
			static pair => Assert.False(pair.Value.IsValueCreated));
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
