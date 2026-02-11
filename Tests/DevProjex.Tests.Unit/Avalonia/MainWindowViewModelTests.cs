using System.Collections.Generic;
using DevProjex.Avalonia.ViewModels;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.ResourceStore;
using DevProjex.Kernel.Models;
using DevProjex.Tests.Unit.Helpers;
using Xunit;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class MainWindowViewModelTests
{
    private static MainWindowViewModel CreateViewModel(IReadOnlyDictionary<string, string>? strings = null)
    {
        var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
        {
            [AppLanguage.En] = strings ?? new Dictionary<string, string>()
        });
        var localization = new LocalizationService(catalog, AppLanguage.En);
        var helpContentProvider = new HelpContentProvider();
        return new MainWindowViewModel(localization, helpContentProvider);
    }

    [Fact]
    public void Constructor_SetsDefaults()
    {
        var viewModel = CreateViewModel();

        Assert.True(viewModel.AllExtensionsChecked);
        Assert.True(viewModel.AllRootFoldersChecked);
        Assert.True(viewModel.AllIgnoreChecked);
        Assert.True(viewModel.IsDarkTheme);
        Assert.True(viewModel.IsTransparentEnabled);
        Assert.Equal(15, viewModel.TreeFontSize);
    }

    [Fact]
    public void Constructor_Defaults_ShowTransparencyAndBlur()
    {
        var viewModel = CreateViewModel();

        Assert.True(viewModel.HasAnyEffect);
        Assert.True(viewModel.ShowTransparencySliders);
        Assert.True(viewModel.ShowBlurSlider);
    }

    [Fact]
    public void Title_Changes()
    {
        var viewModel = CreateViewModel();

        viewModel.Title = "New Title";

        Assert.Equal("New Title", viewModel.Title);
    }

    [Fact]
    public void IsProjectLoaded_Changes()
    {
        var viewModel = CreateViewModel();

        viewModel.IsProjectLoaded = true;

        Assert.True(viewModel.IsProjectLoaded);
    }

    [Fact]
    public void IsProjectLoaded_CanToggleFalse()
    {
        var viewModel = CreateViewModel();
        viewModel.IsProjectLoaded = true;

        viewModel.IsProjectLoaded = false;

        Assert.False(viewModel.IsProjectLoaded);
    }

    [Fact]
    public void SettingsVisible_Changes()
    {
        var viewModel = CreateViewModel();

        viewModel.SettingsVisible = true;

        Assert.True(viewModel.SettingsVisible);
    }

    [Fact]
    public void SettingsVisible_CanToggleFalse()
    {
        var viewModel = CreateViewModel();
        viewModel.SettingsVisible = true;

        viewModel.SettingsVisible = false;

        Assert.False(viewModel.SettingsVisible);
    }

    [Fact]
    public void SearchVisible_Changes()
    {
        var viewModel = CreateViewModel();

        viewModel.SearchVisible = true;

        Assert.True(viewModel.SearchVisible);
    }

    [Fact]
    public void SearchVisible_CanToggleFalse()
    {
        var viewModel = CreateViewModel();
        viewModel.SearchVisible = true;

        viewModel.SearchVisible = false;

        Assert.False(viewModel.SearchVisible);
    }

    [Fact]
    public void SearchQuery_Changes()
    {
        var viewModel = CreateViewModel();

        viewModel.SearchQuery = "query";

        Assert.Equal("query", viewModel.SearchQuery);
    }

    [Fact]
    public void SearchQuery_AllowsEmptyString()
    {
        var viewModel = CreateViewModel();
        viewModel.SearchQuery = "query";

        viewModel.SearchQuery = string.Empty;

        Assert.Equal(string.Empty, viewModel.SearchQuery);
    }

    [Fact]
    public void NameFilter_Changes()
    {
        var viewModel = CreateViewModel();

        viewModel.NameFilter = "filter";

        Assert.Equal("filter", viewModel.NameFilter);
    }

    [Fact]
    public void NameFilter_AllowsEmptyString()
    {
        var viewModel = CreateViewModel();
        viewModel.NameFilter = "filter";

        viewModel.NameFilter = string.Empty;

        Assert.Equal(string.Empty, viewModel.NameFilter);
    }

    [Fact]
    public void IsDarkTheme_FalseSetsIsLightThemeTrue()
    {
        var viewModel = CreateViewModel();

        viewModel.IsDarkTheme = false;

        Assert.False(viewModel.IsDarkTheme);
        Assert.True(viewModel.IsLightTheme);
    }

    [Fact]
    public void IsCompactMode_Changes()
    {
        var viewModel = CreateViewModel();

        viewModel.IsCompactMode = true;

        Assert.True(viewModel.IsCompactMode);
    }

    [Fact]
    public void IsCompactMode_CanToggleFalse()
    {
        var viewModel = CreateViewModel();
        viewModel.IsCompactMode = true;

        viewModel.IsCompactMode = false;

        Assert.False(viewModel.IsCompactMode);
    }

    [Fact]
    public void FilterVisible_Changes()
    {
        var viewModel = CreateViewModel();

        viewModel.FilterVisible = true;

        Assert.True(viewModel.FilterVisible);
    }

    [Fact]
    public void FilterVisible_CanToggleFalse()
    {
        var viewModel = CreateViewModel();
        viewModel.FilterVisible = true;

        viewModel.FilterVisible = false;

        Assert.False(viewModel.FilterVisible);
    }

    [Fact]
    public void IsMicaEnabled_SetTrue_DisablesOtherEffects()
    {
        var viewModel = CreateViewModel();

        viewModel.IsMicaEnabled = true;

        Assert.True(viewModel.IsMicaEnabled);
        Assert.False(viewModel.IsAcrylicEnabled);
        Assert.False(viewModel.IsTransparentEnabled);
    }

    [Fact]
    public void IsMicaEnabled_SetFalse_LeavesAllEffectsOff()
    {
        var viewModel = CreateViewModel();
        viewModel.IsTransparentEnabled = false;
        viewModel.IsMicaEnabled = true;

        viewModel.IsMicaEnabled = false;

        Assert.False(viewModel.HasAnyEffect);
    }

    [Fact]
    public void IsAcrylicEnabled_SetTrue_DisablesOtherEffects()
    {
        var viewModel = CreateViewModel();

        viewModel.IsAcrylicEnabled = true;

        Assert.True(viewModel.IsAcrylicEnabled);
        Assert.False(viewModel.IsMicaEnabled);
        Assert.False(viewModel.IsTransparentEnabled);
    }

    [Fact]
    public void IsAcrylicEnabled_SetFalse_LeavesAllEffectsOff()
    {
        var viewModel = CreateViewModel();
        viewModel.IsTransparentEnabled = false;
        viewModel.IsAcrylicEnabled = true;

        viewModel.IsAcrylicEnabled = false;

        Assert.False(viewModel.HasAnyEffect);
    }

    [Fact]
    public void IsTransparentEnabled_SetTrue_DisablesOtherEffects()
    {
        var viewModel = CreateViewModel();

        viewModel.IsTransparentEnabled = true;

        Assert.True(viewModel.IsTransparentEnabled);
        Assert.False(viewModel.IsMicaEnabled);
        Assert.False(viewModel.IsAcrylicEnabled);
    }

    [Fact]
    public void IsTransparentEnabled_SetFalse_LeavesAllEffectsOff()
    {
        var viewModel = CreateViewModel();
        viewModel.IsTransparentEnabled = true;

        viewModel.IsTransparentEnabled = false;

        Assert.False(viewModel.HasAnyEffect);
    }

    [Fact]
    public void IsTransparentEnabled_SetTrue_DisablesAcrylic()
    {
        var viewModel = CreateViewModel();
        viewModel.IsAcrylicEnabled = true;

        viewModel.IsTransparentEnabled = true;

        Assert.True(viewModel.IsTransparentEnabled);
        Assert.False(viewModel.IsAcrylicEnabled);
    }

    [Fact]
    public void IsTransparentEnabled_SetTrue_DisablesMica()
    {
        var viewModel = CreateViewModel();
        viewModel.IsMicaEnabled = true;

        viewModel.IsTransparentEnabled = true;

        Assert.True(viewModel.IsTransparentEnabled);
        Assert.False(viewModel.IsMicaEnabled);
    }

    [Fact]
    public void IsMicaEnabled_SetTrue_DisablesAcrylic()
    {
        var viewModel = CreateViewModel();
        viewModel.IsAcrylicEnabled = true;

        viewModel.IsMicaEnabled = true;

        Assert.True(viewModel.IsMicaEnabled);
        Assert.False(viewModel.IsAcrylicEnabled);
    }

    [Fact]
    public void IsAcrylicEnabled_SetTrue_DisablesMica()
    {
        var viewModel = CreateViewModel();
        viewModel.IsMicaEnabled = true;

        viewModel.IsAcrylicEnabled = true;

        Assert.True(viewModel.IsAcrylicEnabled);
        Assert.False(viewModel.IsMicaEnabled);
    }

    [Fact]
    public void ToggleTransparent_EnablesTransparentDisablesOthers()
    {
        var viewModel = CreateViewModel();
        viewModel.IsMicaEnabled = true;

        viewModel.ToggleTransparent();

        Assert.True(viewModel.IsTransparentEnabled);
        Assert.False(viewModel.IsMicaEnabled);
        Assert.False(viewModel.IsAcrylicEnabled);
    }

    [Fact]
    public void ToggleTransparent_WhenEnabled_DisablesAllEffects()
    {
        var viewModel = CreateViewModel();
        viewModel.IsTransparentEnabled = true;

        viewModel.ToggleTransparent();

        Assert.False(viewModel.IsTransparentEnabled);
        Assert.False(viewModel.HasAnyEffect);
    }

    [Fact]
    public void ToggleTransparent_FromAllOff_EnablesTransparentOnly()
    {
        var viewModel = CreateViewModel();
        viewModel.IsTransparentEnabled = false;

        viewModel.ToggleTransparent();

        Assert.True(viewModel.IsTransparentEnabled);
        Assert.False(viewModel.IsMicaEnabled);
        Assert.False(viewModel.IsAcrylicEnabled);
    }

    [Fact]
    public void ToggleMica_EnablesMicaDisablesOthers()
    {
        var viewModel = CreateViewModel();
        viewModel.IsTransparentEnabled = true;

        viewModel.ToggleMica();

        Assert.True(viewModel.IsMicaEnabled);
        Assert.False(viewModel.IsTransparentEnabled);
        Assert.False(viewModel.IsAcrylicEnabled);
    }

    [Fact]
    public void ToggleMica_WhenEnabled_DisablesAllEffects()
    {
        var viewModel = CreateViewModel();
        viewModel.IsMicaEnabled = true;

        viewModel.ToggleMica();

        Assert.False(viewModel.HasAnyEffect);
    }

    [Fact]
    public void ToggleMica_FromAllOff_EnablesMicaOnly()
    {
        var viewModel = CreateViewModel();
        viewModel.IsTransparentEnabled = false;

        viewModel.ToggleMica();

        Assert.True(viewModel.IsMicaEnabled);
        Assert.False(viewModel.IsAcrylicEnabled);
        Assert.False(viewModel.IsTransparentEnabled);
    }

    [Fact]
    public void ToggleAcrylic_EnablesAcrylicDisablesOthers()
    {
        var viewModel = CreateViewModel();
        viewModel.IsTransparentEnabled = true;

        viewModel.ToggleAcrylic();

        Assert.True(viewModel.IsAcrylicEnabled);
        Assert.False(viewModel.IsTransparentEnabled);
        Assert.False(viewModel.IsMicaEnabled);
    }

    [Fact]
    public void ToggleAcrylic_WhenEnabled_DisablesAllEffects()
    {
        var viewModel = CreateViewModel();
        viewModel.IsAcrylicEnabled = true;

        viewModel.ToggleAcrylic();

        Assert.False(viewModel.HasAnyEffect);
    }

    [Fact]
    public void ToggleAcrylic_FromAllOff_EnablesAcrylicOnly()
    {
        var viewModel = CreateViewModel();
        viewModel.IsTransparentEnabled = false;

        viewModel.ToggleAcrylic();

        Assert.True(viewModel.IsAcrylicEnabled);
        Assert.False(viewModel.IsMicaEnabled);
        Assert.False(viewModel.IsTransparentEnabled);
    }

    [Fact]
    public void HasAnyEffect_TrueWhenAnyEffectEnabled()
    {
        var viewModel = CreateViewModel();
        viewModel.IsTransparentEnabled = false;

        viewModel.IsMicaEnabled = true;

        Assert.True(viewModel.HasAnyEffect);
    }

    [Fact]
    public void ShowTransparencySliders_TrueWhenAnyEffectEnabled()
    {
        var viewModel = CreateViewModel();
        viewModel.IsTransparentEnabled = false;

        viewModel.IsAcrylicEnabled = true;

        Assert.True(viewModel.ShowTransparencySliders);
    }

    [Fact]
    public void ShowTransparencySliders_FalseWhenNoEffects()
    {
        var viewModel = CreateViewModel();
        viewModel.IsTransparentEnabled = false;

        viewModel.IsAcrylicEnabled = false;
        viewModel.IsMicaEnabled = false;

        Assert.False(viewModel.ShowTransparencySliders);
    }

    [Fact]
    public void ShowBlurSlider_TrueOnlyWhenTransparentEnabled()
    {
        var viewModel = CreateViewModel();
        viewModel.IsTransparentEnabled = false;
        viewModel.IsMicaEnabled = true;

        Assert.False(viewModel.ShowBlurSlider);

        viewModel.IsTransparentEnabled = true;

        Assert.True(viewModel.ShowBlurSlider);
    }

    [Fact]
    public void ShowBlurSlider_FalseWhenAcrylicEnabled()
    {
        var viewModel = CreateViewModel();
        viewModel.IsTransparentEnabled = false;

        viewModel.IsAcrylicEnabled = true;

        Assert.False(viewModel.ShowBlurSlider);
    }

    [Fact]
    public void ThemePopoverOpen_Changes()
    {
        var viewModel = CreateViewModel();

        viewModel.ThemePopoverOpen = true;

        Assert.True(viewModel.ThemePopoverOpen);
    }

    [Fact]
    public void ThemePopoverOpen_CanToggleFalse()
    {
        var viewModel = CreateViewModel();
        viewModel.ThemePopoverOpen = true;

        viewModel.ThemePopoverOpen = false;

        Assert.False(viewModel.ThemePopoverOpen);
    }

    [Fact]
    public void MaterialIntensity_ChangesBeyondThreshold()
    {
        var viewModel = CreateViewModel();

        viewModel.MaterialIntensity = 80;

        Assert.Equal(80, viewModel.MaterialIntensity);
    }

    [Fact]
    public void MaterialIntensity_AllowsNegativeValues()
    {
        var viewModel = CreateViewModel();

        viewModel.MaterialIntensity = -5;

        Assert.Equal(-5, viewModel.MaterialIntensity);
    }

    [Fact]
    public void BlurRadius_ChangesBeyondThreshold()
    {
        var viewModel = CreateViewModel();

        viewModel.BlurRadius = 40;

        Assert.Equal(40, viewModel.BlurRadius);
    }

    [Fact]
    public void BlurRadius_AllowsNegativeValues()
    {
        var viewModel = CreateViewModel();

        viewModel.BlurRadius = -10;

        Assert.Equal(-10, viewModel.BlurRadius);
    }

    [Fact]
    public void PanelContrast_ChangesBeyondThreshold()
    {
        var viewModel = CreateViewModel();

        viewModel.PanelContrast = 70;

        Assert.Equal(70, viewModel.PanelContrast);
    }

    [Fact]
    public void PanelContrast_AllowsNegativeValues()
    {
        var viewModel = CreateViewModel();

        viewModel.PanelContrast = -1;

        Assert.Equal(-1, viewModel.PanelContrast);
    }

    [Fact]
    public void BorderStrength_ChangesBeyondThreshold()
    {
        var viewModel = CreateViewModel();

        viewModel.BorderStrength = 70;

        Assert.Equal(70, viewModel.BorderStrength);
    }

    [Fact]
    public void BorderStrength_AllowsNegativeValues()
    {
        var viewModel = CreateViewModel();

        viewModel.BorderStrength = -2;

        Assert.Equal(-2, viewModel.BorderStrength);
    }

    [Fact]
    public void MenuChildIntensity_ChangesBeyondThreshold()
    {
        var viewModel = CreateViewModel();

        viewModel.MenuChildIntensity = 70;

        Assert.Equal(70, viewModel.MenuChildIntensity);
    }

    [Fact]
    public void MenuChildIntensity_AllowsNegativeValues()
    {
        var viewModel = CreateViewModel();

        viewModel.MenuChildIntensity = -3;

        Assert.Equal(-3, viewModel.MenuChildIntensity);
    }

    [Theory]
    [InlineData(10, 12)]
    [InlineData(12, 15)]
    [InlineData(16, 20)]
    public void TreeFontSize_UpdatesTreeIconSize(double size, double expectedIconSize)
    {
        var viewModel = CreateViewModel();

        viewModel.TreeFontSize = size;

        Assert.Equal(expectedIconSize, viewModel.TreeIconSize);
    }

    [Fact]
    public void TreeIconSize_RoundsToNearestWhole()
    {
        var viewModel = CreateViewModel();

        viewModel.TreeFontSize = 13;

        Assert.Equal(16, viewModel.TreeIconSize);
    }

    [Fact]
    public void TreeIconSize_RoundsDownForSmallFraction()
    {
        var viewModel = CreateViewModel();

        viewModel.TreeFontSize = 14.5;

        Assert.Equal(18, viewModel.TreeIconSize);
    }

    [Fact]
    public void AllExtensionsChecked_Changes()
    {
        var viewModel = CreateViewModel();

        viewModel.AllExtensionsChecked = false;

        Assert.False(viewModel.AllExtensionsChecked);
    }

    [Fact]
    public void AllExtensionsChecked_CanToggleTrue()
    {
        var viewModel = CreateViewModel();
        viewModel.AllExtensionsChecked = false;

        viewModel.AllExtensionsChecked = true;

        Assert.True(viewModel.AllExtensionsChecked);
    }

    [Fact]
    public void AllRootFoldersChecked_Changes()
    {
        var viewModel = CreateViewModel();

        viewModel.AllRootFoldersChecked = false;

        Assert.False(viewModel.AllRootFoldersChecked);
    }

    [Fact]
    public void AllRootFoldersChecked_CanToggleTrue()
    {
        var viewModel = CreateViewModel();
        viewModel.AllRootFoldersChecked = false;

        viewModel.AllRootFoldersChecked = true;

        Assert.True(viewModel.AllRootFoldersChecked);
    }

    [Fact]
    public void AllIgnoreChecked_Changes()
    {
        var viewModel = CreateViewModel();

        viewModel.AllIgnoreChecked = false;

        Assert.False(viewModel.AllIgnoreChecked);
    }

    [Fact]
    public void AllIgnoreChecked_CanToggleTrue()
    {
        var viewModel = CreateViewModel();
        viewModel.AllIgnoreChecked = false;

        viewModel.AllIgnoreChecked = true;

        Assert.True(viewModel.AllIgnoreChecked);
    }

    [Fact]
    public void SelectedFontFamily_Changes()
    {
        var viewModel = CreateViewModel();

        viewModel.SelectedFontFamily = "Segoe UI";

        Assert.Equal("Segoe UI", viewModel.SelectedFontFamily);
    }

    [Fact]
    public void SelectedFontFamily_CanBeCleared()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedFontFamily = "Segoe UI";

        viewModel.SelectedFontFamily = null;

        Assert.Null(viewModel.SelectedFontFamily);
    }

    [Fact]
    public void PendingFontFamily_Changes()
    {
        var viewModel = CreateViewModel();

        viewModel.PendingFontFamily = "Consolas";

        Assert.Equal("Consolas", viewModel.PendingFontFamily);
    }

    [Fact]
    public void PendingFontFamily_CanBeCleared()
    {
        var viewModel = CreateViewModel();
        viewModel.PendingFontFamily = "Consolas";

        viewModel.PendingFontFamily = null;

        Assert.Null(viewModel.PendingFontFamily);
    }

    [Fact]
    public void EffectToggle_SwitchesShowBlurSlider()
    {
        var viewModel = CreateViewModel();

        viewModel.ToggleMica();

        Assert.False(viewModel.ShowBlurSlider);

        viewModel.ToggleTransparent();

        Assert.True(viewModel.ShowBlurSlider);
    }

    [Fact]
    public void IsTransparentEnabled_WhenDisabled_HasAnyEffectFalse()
    {
        var viewModel = CreateViewModel();

        viewModel.IsTransparentEnabled = false;

        Assert.False(viewModel.HasAnyEffect);
    }

    [Fact]
    public void HasAnyEffect_FalseWhenAllEffectsDisabled()
    {
        var viewModel = CreateViewModel();
        viewModel.IsTransparentEnabled = false;

        viewModel.IsMicaEnabled = false;
        viewModel.IsAcrylicEnabled = false;

        Assert.False(viewModel.HasAnyEffect);
    }

    [Fact]
    public void IsMicaEnabled_WhenDisabled_HasAnyEffectFalse()
    {
        var viewModel = CreateViewModel();
        viewModel.IsTransparentEnabled = false;

        viewModel.IsMicaEnabled = false;

        Assert.False(viewModel.HasAnyEffect);
    }

    [Fact]
    public void IsAcrylicEnabled_WhenDisabled_HasAnyEffectFalse()
    {
        var viewModel = CreateViewModel();
        viewModel.IsTransparentEnabled = false;

        viewModel.IsAcrylicEnabled = false;

        Assert.False(viewModel.HasAnyEffect);
    }

    [Fact]
    public void TreeIconSize_MinimumIs12()
    {
        var viewModel = CreateViewModel();

        viewModel.TreeFontSize = 1;

        Assert.Equal(12, viewModel.TreeIconSize);
    }

    #region SettingsAllCheckboxLabels Tests

    [Fact]
    public void SettingsAllIgnore_WhenEmpty_ReturnsBaseText()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });

        Assert.Equal("All", viewModel.SettingsAllIgnore);
    }

    [Fact]
    public void SettingsAllExtensions_WhenEmpty_ReturnsBaseText()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });

        Assert.Equal("All", viewModel.SettingsAllExtensions);
    }

    [Fact]
    public void SettingsAllRootFolders_WhenEmpty_ReturnsBaseText()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });

        Assert.Equal("All", viewModel.SettingsAllRootFolders);
    }

    [Fact]
    public void SettingsAllIgnore_WhenHasItems_ReturnsTextWithCount()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });

        viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.HiddenFolders, "bin", true));
        viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.HiddenFiles, "obj", true));

        Assert.Equal("All (2)", viewModel.SettingsAllIgnore);
    }

    [Fact]
    public void SettingsAllExtensions_WhenHasItems_ReturnsTextWithCount()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });

        viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
        viewModel.Extensions.Add(new SelectionOptionViewModel(".js", true));
        viewModel.Extensions.Add(new SelectionOptionViewModel(".ts", true));

        Assert.Equal("All (3)", viewModel.SettingsAllExtensions);
    }

    [Fact]
    public void SettingsAllRootFolders_WhenHasItems_ReturnsTextWithCount()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });

        viewModel.RootFolders.Add(new SelectionOptionViewModel("src", true));

        Assert.Equal("All (1)", viewModel.SettingsAllRootFolders);
    }

    [Fact]
    public void SettingsAllIgnore_WhenItemRemoved_UpdatesCount()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });
        viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.HiddenFolders, "bin", true));
        viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.HiddenFiles, "obj", true));

        viewModel.IgnoreOptions.RemoveAt(0);

        Assert.Equal("All (1)", viewModel.SettingsAllIgnore);
    }

    [Fact]
    public void SettingsAllExtensions_WhenItemRemoved_UpdatesCount()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });
        viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
        viewModel.Extensions.Add(new SelectionOptionViewModel(".js", true));

        viewModel.Extensions.RemoveAt(0);

        Assert.Equal("All (1)", viewModel.SettingsAllExtensions);
    }

    [Fact]
    public void SettingsAllRootFolders_WhenItemRemoved_UpdatesCount()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });
        viewModel.RootFolders.Add(new SelectionOptionViewModel("src", true));
        viewModel.RootFolders.Add(new SelectionOptionViewModel("tests", true));

        viewModel.RootFolders.RemoveAt(0);

        Assert.Equal("All (1)", viewModel.SettingsAllRootFolders);
    }

    [Fact]
    public void SettingsAllIgnore_WhenCleared_ReturnsBaseText()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });
        viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.HiddenFolders, "bin", true));

        viewModel.IgnoreOptions.Clear();

        Assert.Equal("All", viewModel.SettingsAllIgnore);
    }

    [Fact]
    public void SettingsAllExtensions_WhenCleared_ReturnsBaseText()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });
        viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));

        viewModel.Extensions.Clear();

        Assert.Equal("All", viewModel.SettingsAllExtensions);
    }

    [Fact]
    public void SettingsAllRootFolders_WhenCleared_ReturnsBaseText()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });
        viewModel.RootFolders.Add(new SelectionOptionViewModel("src", true));

        viewModel.RootFolders.Clear();

        Assert.Equal("All", viewModel.SettingsAllRootFolders);
    }

    [Fact]
    public void SettingsAllLabels_AreIndependent()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });

        viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.HiddenFolders, "bin", true));
        viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
        viewModel.Extensions.Add(new SelectionOptionViewModel(".js", true));
        // RootFolders stays empty

        Assert.Equal("All (1)", viewModel.SettingsAllIgnore);
        Assert.Equal("All (2)", viewModel.SettingsAllExtensions);
        Assert.Equal("All", viewModel.SettingsAllRootFolders);
    }

    [Fact]
    public void SettingsAllLabels_WithRussianLocalization()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "Все"
        });

        viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
        viewModel.Extensions.Add(new SelectionOptionViewModel(".js", true));
        viewModel.Extensions.Add(new SelectionOptionViewModel(".ts", true));
        viewModel.Extensions.Add(new SelectionOptionViewModel(".py", true));
        viewModel.Extensions.Add(new SelectionOptionViewModel(".go", true));

        Assert.Equal("Все (5)", viewModel.SettingsAllExtensions);
    }

    [Fact]
    public void SettingsAllIgnore_RaisesPropertyChanged_WhenCollectionChanges()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });
        var raised = false;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(viewModel.SettingsAllIgnore))
                raised = true;
        };

        viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.HiddenFolders, "bin", true));

        Assert.True(raised);
    }

    [Fact]
    public void SettingsAllExtensions_RaisesPropertyChanged_WhenCollectionChanges()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });
        var raised = false;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(viewModel.SettingsAllExtensions))
                raised = true;
        };

        viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));

        Assert.True(raised);
    }

    [Fact]
    public void SettingsAllRootFolders_RaisesPropertyChanged_WhenCollectionChanges()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });
        var raised = false;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(viewModel.SettingsAllRootFolders))
                raised = true;
        };

        viewModel.RootFolders.Add(new SelectionOptionViewModel("src", true));

        Assert.True(raised);
    }

    [Fact]
    public void SettingsAllIgnore_MultipleAdds_UpdatesCorrectly()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });

        viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.HiddenFolders, "bin", true));
        Assert.Equal("All (1)", viewModel.SettingsAllIgnore);

        viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.HiddenFiles, "obj", true));
        Assert.Equal("All (2)", viewModel.SettingsAllIgnore);

        viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.HiddenFolders, "hidden", true));
        Assert.Equal("All (3)", viewModel.SettingsAllIgnore);
    }

    [Fact]
    public void SettingsAllExtensions_MultipleAdds_UpdatesCorrectly()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });

        for (var i = 1; i <= 10; i++)
        {
            viewModel.Extensions.Add(new SelectionOptionViewModel($".ext{i}", true));
            Assert.Equal($"All ({i})", viewModel.SettingsAllExtensions);
        }
    }

    [Fact]
    public void SettingsAllRootFolders_MultipleAdds_UpdatesCorrectly()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });

        viewModel.RootFolders.Add(new SelectionOptionViewModel("src", true));
        viewModel.RootFolders.Add(new SelectionOptionViewModel("tests", true));
        viewModel.RootFolders.Add(new SelectionOptionViewModel("docs", true));
        viewModel.RootFolders.Add(new SelectionOptionViewModel("lib", true));

        Assert.Equal("All (4)", viewModel.SettingsAllRootFolders);
    }

    [Fact]
    public void UpdateAllCheckboxLabels_ManualCall_UpdatesAllLabels()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });
        viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.HiddenFolders, "bin", true));
        viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
        viewModel.RootFolders.Add(new SelectionOptionViewModel("src", true));

        viewModel.UpdateAllCheckboxLabels();

        Assert.Equal("All (1)", viewModel.SettingsAllIgnore);
        Assert.Equal("All (1)", viewModel.SettingsAllExtensions);
        Assert.Equal("All (1)", viewModel.SettingsAllRootFolders);
    }

    [Fact]
    public void SettingsAllIgnore_WhenAddAndRemove_UpdatesCorrectly()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });

        var item1 = new IgnoreOptionViewModel(IgnoreOptionId.HiddenFolders, "bin", true);
        var item2 = new IgnoreOptionViewModel(IgnoreOptionId.HiddenFiles, "obj", true);

        viewModel.IgnoreOptions.Add(item1);
        viewModel.IgnoreOptions.Add(item2);
        Assert.Equal("All (2)", viewModel.SettingsAllIgnore);

        viewModel.IgnoreOptions.Remove(item1);
        Assert.Equal("All (1)", viewModel.SettingsAllIgnore);

        viewModel.IgnoreOptions.Remove(item2);
        Assert.Equal("All", viewModel.SettingsAllIgnore);
    }

    [Fact]
    public void SettingsAllExtensions_WhenAddAndRemove_UpdatesCorrectly()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });

        var item1 = new SelectionOptionViewModel(".cs", true);
        var item2 = new SelectionOptionViewModel(".js", true);

        viewModel.Extensions.Add(item1);
        viewModel.Extensions.Add(item2);
        Assert.Equal("All (2)", viewModel.SettingsAllExtensions);

        viewModel.Extensions.Remove(item1);
        Assert.Equal("All (1)", viewModel.SettingsAllExtensions);

        viewModel.Extensions.Remove(item2);
        Assert.Equal("All", viewModel.SettingsAllExtensions);
    }

    [Fact]
    public void SettingsAllRootFolders_WhenAddAndRemove_UpdatesCorrectly()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });

        var item1 = new SelectionOptionViewModel("src", true);
        var item2 = new SelectionOptionViewModel("tests", true);

        viewModel.RootFolders.Add(item1);
        viewModel.RootFolders.Add(item2);
        Assert.Equal("All (2)", viewModel.SettingsAllRootFolders);

        viewModel.RootFolders.Remove(item1);
        Assert.Equal("All (1)", viewModel.SettingsAllRootFolders);

        viewModel.RootFolders.Remove(item2);
        Assert.Equal("All", viewModel.SettingsAllRootFolders);
    }

    [Fact]
    public void SettingsAllLabels_LargeCount_FormatsCorrectly()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });

        for (var i = 0; i < 100; i++)
        {
            viewModel.Extensions.Add(new SelectionOptionViewModel($".ext{i}", true));
        }

        Assert.Equal("All (100)", viewModel.SettingsAllExtensions);
    }

    [Fact]
    public void SettingsAllIgnore_InsertAtIndex_UpdatesCount()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });
        viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.HiddenFolders, "bin", true));

        viewModel.IgnoreOptions.Insert(0, new IgnoreOptionViewModel(IgnoreOptionId.HiddenFiles, "obj", true));

        Assert.Equal("All (2)", viewModel.SettingsAllIgnore);
    }

    [Fact]
    public void SettingsAllExtensions_InsertAtIndex_UpdatesCount()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });
        viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));

        viewModel.Extensions.Insert(0, new SelectionOptionViewModel(".js", true));

        Assert.Equal("All (2)", viewModel.SettingsAllExtensions);
    }

    [Fact]
    public void SettingsAllRootFolders_InsertAtIndex_UpdatesCount()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });
        viewModel.RootFolders.Add(new SelectionOptionViewModel("src", true));

        viewModel.RootFolders.Insert(0, new SelectionOptionViewModel("tests", true));

        Assert.Equal("All (2)", viewModel.SettingsAllRootFolders);
    }

    [Fact]
    public void SettingsAll_BaseProperty_StillExists()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "TestAll"
        });

        Assert.Equal("TestAll", viewModel.SettingsAll);
    }

    [Fact]
    public void SettingsAllLabels_EmptyLocalizationKey_FallsBackGracefully()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>());

        viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));

        // Should not throw and should handle empty string gracefully
        Assert.Contains("(1)", viewModel.SettingsAllExtensions);
    }

    [Fact]
    public void SettingsAllIgnore_RaisesPropertyChanged_OnClear()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });
        viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.HiddenFolders, "bin", true));

        var raised = false;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(viewModel.SettingsAllIgnore))
                raised = true;
        };

        viewModel.IgnoreOptions.Clear();

        Assert.True(raised);
    }

    [Fact]
    public void SettingsAllExtensions_RaisesPropertyChanged_OnRemove()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });
        var item = new SelectionOptionViewModel(".cs", true);
        viewModel.Extensions.Add(item);

        var raised = false;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(viewModel.SettingsAllExtensions))
                raised = true;
        };

        viewModel.Extensions.Remove(item);

        Assert.True(raised);
    }

    [Fact]
    public void SettingsAllRootFolders_RaisesPropertyChanged_OnInsert()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });

        var raised = false;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(viewModel.SettingsAllRootFolders))
                raised = true;
        };

        viewModel.RootFolders.Insert(0, new SelectionOptionViewModel("src", true));

        Assert.True(raised);
    }

    [Theory]
    [InlineData(1, "All (1)")]
    [InlineData(5, "All (5)")]
    [InlineData(10, "All (10)")]
    [InlineData(50, "All (50)")]
    public void SettingsAllIgnore_VariousCounts_FormatsCorrectly(int count, string expected)
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });

        for (var i = 0; i < count; i++)
        {
            viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.HiddenFolders, $"item{i}", true));
        }

        Assert.Equal(expected, viewModel.SettingsAllIgnore);
    }

    [Theory]
    [InlineData(1, "Все (1)")]
    [InlineData(3, "Все (3)")]
    [InlineData(7, "Все (7)")]
    public void SettingsAllExtensions_RussianVariousCounts_FormatsCorrectly(int count, string expected)
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "Все"
        });

        for (var i = 0; i < count; i++)
        {
            viewModel.Extensions.Add(new SelectionOptionViewModel($".ext{i}", true));
        }

        Assert.Equal(expected, viewModel.SettingsAllExtensions);
    }

    [Fact]
    public void AllThreeLabels_IndependentPropertyChangedEvents()
    {
        var viewModel = CreateViewModel(new Dictionary<string, string>
        {
            ["Settings.All"] = "All"
        });

        var ignoreRaised = false;
        var extensionsRaised = false;
        var rootFoldersRaised = false;

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(viewModel.SettingsAllIgnore)) ignoreRaised = true;
            if (e.PropertyName == nameof(viewModel.SettingsAllExtensions)) extensionsRaised = true;
            if (e.PropertyName == nameof(viewModel.SettingsAllRootFolders)) rootFoldersRaised = true;
        };

        viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(IgnoreOptionId.HiddenFolders, "bin", true));

        // All three should be raised because UpdateAllCheckboxLabels updates all
        Assert.True(ignoreRaised);
        Assert.True(extensionsRaised);
        Assert.True(rootFoldersRaised);
    }

    #endregion
}

