using DevProjex.Avalonia.Collections;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class MainWindowViewModelResettableCollectionsTests
{
    private static MainWindowViewModel CreateViewModel()
    {
        var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
        {
            [AppLanguage.En] = new Dictionary<string, string>
            {
                ["Settings.All"] = "All"
            }
        });

        var localization = new LocalizationService(catalog, AppLanguage.En);
        return new MainWindowViewModel(localization, new HelpContentProvider());
    }

    [Fact]
    public void SettingsAllLabels_UpdateCorrectly_WhenCollectionsRaiseReset()
    {
        var viewModel = CreateViewModel();
        var ignoreOptions = Assert.IsType<ResettableObservableCollection<IgnoreOptionViewModel>>(viewModel.IgnoreOptions);
        var extensions = Assert.IsType<ResettableObservableCollection<SelectionOptionViewModel>>(viewModel.Extensions);
        var rootFolders = Assert.IsType<ResettableObservableCollection<SelectionOptionViewModel>>(viewModel.RootFolders);

        ignoreOptions.ReplaceAll([
            new IgnoreOptionViewModel(IgnoreOptionId.EmptyFiles, "Empty files", true),
            new IgnoreOptionViewModel(IgnoreOptionId.EmptyFolders, "Empty folders", true)
        ]);
        extensions.ReplaceAll([
            new SelectionOptionViewModel(".cs", true),
            new SelectionOptionViewModel(".xaml", true),
            new SelectionOptionViewModel(".json", true)
        ]);
        rootFolders.ReplaceAll([
            new SelectionOptionViewModel("src", true),
            new SelectionOptionViewModel("tests", true)
        ]);

        Assert.Equal("All (2)", viewModel.SettingsAllIgnore);
        Assert.Equal("All (3)", viewModel.SettingsAllExtensions);
        Assert.Equal("All (2)", viewModel.SettingsAllRootFolders);
    }
}
