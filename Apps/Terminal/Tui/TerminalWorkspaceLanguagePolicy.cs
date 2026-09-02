namespace DevProjex.Terminal.Tui;

internal static class TerminalWorkspaceLanguagePolicy
{
	public static AppLanguage Resolve(
		AppLanguage automaticLanguage,
		AppLanguage? explicitLanguage,
		AppLanguage? persistedLanguage) =>
		explicitLanguage ?? persistedLanguage ?? automaticLanguage;
}
