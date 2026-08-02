namespace DevProjex.Tests.Unit.Avalonia;

[Trait("Category", "TerminalCommand")]
public sealed class TerminalCommandMenuLocalizationTests
{
	[Fact]
	public void UpdateLocalization_UsesTerminalCommandHelpMenuKey()
	{
		var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
		{
			[AppLanguage.En] = new Dictionary<string, string>
			{
				["Menu.Help"] = "Help",
				["Menu.Help.Help"] = "Help",
				["Menu.Help.TerminalCommand"] = "Launch from terminal",
				["Menu.Help.About"] = "About",
				["Menu.Help.ResetSettings"] = "Reset settings",
				["Menu.Help.ResetData"] = "Reset data"
			}
		});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var viewModel = new MainWindowViewModel(localization, new HelpContentProvider());

		viewModel.UpdateLocalization();

		Assert.Equal("Launch from terminal", viewModel.MenuHelpTerminalCommand);
	}
}
