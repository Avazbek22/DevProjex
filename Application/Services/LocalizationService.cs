namespace DevProjex.Application.Services;

public sealed class LocalizationService
{
	private readonly ILocalizationCatalog _catalog;
	public AppLanguage CurrentLanguage { get; private set; }

	public event EventHandler? LanguageChanged;

	public LocalizationService(ILocalizationCatalog catalog, AppLanguage initialLanguage)
	{
		_catalog = catalog;
		CurrentLanguage = initialLanguage;
	}

	public string this[string key]
	{
		get
		{
			var dict = _catalog.Get(CurrentLanguage);
			return dict.TryGetValue(key, out var value) ? value : $"[[{key}]]";
		}
	}

	public string Format(string key, params object[] args) => string.Format(this[key], args);

	public void SetLanguage(AppLanguage language)
	{
		if (CurrentLanguage == language) return;

		CurrentLanguage = language;
		LanguageChanged?.Invoke(this, EventArgs.Empty);
	}
}
