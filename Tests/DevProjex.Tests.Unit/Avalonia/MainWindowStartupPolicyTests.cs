namespace DevProjex.Tests.Unit.Avalonia;

public sealed class MainWindowStartupPolicyTests
{
	[Theory]
	[InlineData(AppLanguage.En, AppLanguage.Ru, AppLanguage.De, AppLanguage.Ru)]
	[InlineData(AppLanguage.En, null, AppLanguage.De, AppLanguage.De)]
	[InlineData(AppLanguage.Fr, null, null, AppLanguage.Fr)]
	public void ResolveStartupLanguage_UsesCommandLineThenPreferenceThenCurrent(
		AppLanguage currentLanguage,
		AppLanguage? commandLineLanguage,
		AppLanguage? preferredLanguage,
		AppLanguage expected)
	{
		Assert.Equal(
			expected,
			MainWindow.ResolveStartupLanguage(currentLanguage, commandLineLanguage, preferredLanguage));
	}
}
