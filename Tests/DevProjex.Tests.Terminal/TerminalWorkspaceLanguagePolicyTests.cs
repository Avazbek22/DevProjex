namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspaceLanguagePolicyTests
{
	[Theory]
	[InlineData(AppLanguage.En, null, null, AppLanguage.En)]
	[InlineData(AppLanguage.En, null, AppLanguage.Ja, AppLanguage.Ja)]
	[InlineData(AppLanguage.En, AppLanguage.Ru, AppLanguage.Ja, AppLanguage.Ru)]
	internal void Resolve_UsesExplicitThenPersistedThenAutomaticLanguage(
		AppLanguage automaticLanguage,
		AppLanguage? explicitLanguage,
		AppLanguage? persistedLanguage,
		AppLanguage expected)
	{
		Assert.Equal(
			expected,
			TerminalWorkspaceLanguagePolicy.Resolve(
				automaticLanguage,
				explicitLanguage,
				persistedLanguage));
	}
}
