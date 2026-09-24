namespace DevProjex.Tests.Unit.Avalonia;

public sealed class SelectionPersistenceStatusViewModelTests
{
	[Fact]
	public void SelectionPersistenceStatusIsHiddenInCompactMode()
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		using var viewModel = new MainWindowViewModel(localization, new HelpContentProvider());

		viewModel.SetSelectionPersistenceStatus(
			"Selection not saved; agent uses previous selection",
			"Could not save selection: access denied");
		Assert.True(viewModel.SelectionPersistenceStatusVisible);

		viewModel.IsCompactMode = true;

		Assert.False(viewModel.SelectionPersistenceStatusVisible);
		Assert.Equal(
			"Selection not saved; agent uses previous selection",
			viewModel.SelectionPersistenceStatusText);
	}
}
