namespace DevProjex.Kernel.Abstractions;

public interface ILocalizationCatalog
{
	IReadOnlyDictionary<string, string> Get(AppLanguage language);
}
