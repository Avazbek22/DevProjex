namespace DevProjex.Application.Services;

public sealed class LocalizationService
{
	private const string TerminalTuiKeyPrefix = "Terminal.Tui.";
	private readonly ILocalizationCatalog _catalog;
	private readonly Func<string, string>? _textFormatter;
	private readonly Dictionary<AppLanguage, IReadOnlyDictionary<string, string>> _formattedCatalogs = [];
	private readonly object _sync = new();

	public LocalizationService(
		ILocalizationCatalog catalog,
		AppLanguage initialLanguage,
		Func<string, string>? textFormatter = null)
	{
		_catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
		_textFormatter = textFormatter;
		CurrentLanguage = initialLanguage;
	}

	public AppLanguage CurrentLanguage { get; private set; }

	public event EventHandler? LanguageChanged;

	public string this[string key]
	{
		get
		{
			var dict = GetCurrentCatalog();
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

	private IReadOnlyDictionary<string, string> GetCurrentCatalog()
	{
		if (_textFormatter is null)
			return _catalog.Get(CurrentLanguage);

		lock (_sync)
		{
			if (_formattedCatalogs.TryGetValue(CurrentLanguage, out var formatted))
				return formatted;

			formatted = FormatCatalog(_catalog.Get(CurrentLanguage));
			_formattedCatalogs.Add(CurrentLanguage, formatted);
			return formatted;
		}
	}

	private IReadOnlyDictionary<string, string> FormatCatalog(
		IReadOnlyDictionary<string, string> source)
	{
		var formatted = new Dictionary<string, string>(
			source.Count,
			StringComparer.OrdinalIgnoreCase);
		foreach (var (key, value) in source)
		{
			formatted.Add(
				key,
				key.StartsWith(TerminalTuiKeyPrefix, StringComparison.Ordinal)
					? value
					: _textFormatter!(value));
		}

		return formatted;
	}
}
