namespace DevProjex.Tests.Unit.Avalonia;

public sealed class SelectionPersistenceStatusViewModelTests
{
	[Fact]
	public void SelectionPersistenceStatusIsHiddenInCompactMode()
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		using var viewModel = new MainWindowViewModel(localization, new HelpContentProvider());

		viewModel.SetSelectionPersistenceStatus("Saving selection...", "Saving selection...");
		Assert.True(viewModel.SelectionPersistenceStatusVisible);

		viewModel.IsCompactMode = true;

		Assert.False(viewModel.SelectionPersistenceStatusVisible);
		Assert.Equal("Saving selection...", viewModel.SelectionPersistenceStatusText);
	}
}
