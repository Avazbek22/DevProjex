using DevProjex.Application.Updates;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class ApplicationUpdateViewModelTests
{
    [Fact]
    public void UpdateCheck_StateTransitionsExposeOneConsistentPresentation()
    {
        using var viewModel = CreateViewModel();
        viewModel.SetCurrentApplicationVersion("4.9");

        viewModel.PrepareManualUpdateCheck();
        Assert.True(viewModel.IsUpdateCheckReady);
        Assert.Equal("Current version: v4.9", viewModel.CurrentApplicationVersionText);
        Assert.False(viewModel.IsLatestApplicationVersionVisible);

        viewModel.BeginUpdateCheck();
        Assert.True(viewModel.IsUpdateCheckInProgress);
        Assert.True(viewModel.IsUpdateCheckButtonVisible);

        viewModel.CompleteUpdateCheck(new ApplicationUpdateCheckResult(
            ApplicationUpdateAvailability.UpdateAvailable,
            "4.9",
            "4.10.0"));

        Assert.True(viewModel.IsUpdateAvailable);
        Assert.True(viewModel.IsKnownUpdateAvailable);
        Assert.True(viewModel.IsLatestApplicationVersionVisible);
        Assert.Equal("Latest version: v4.10.0", viewModel.LatestApplicationVersionText);
        Assert.True(viewModel.IsUpdateCheckButtonVisible);
        Assert.Equal("Check again", viewModel.UpdateCheckActionText);
    }

    [Fact]
    public void KnownUpdateIndicator_SurvivesFailureAndClearsAfterSuccessfulCurrentResult()
    {
        using var viewModel = CreateViewModel();
        viewModel.CompleteUpdateCheck(new ApplicationUpdateCheckResult(
            ApplicationUpdateAvailability.UpdateAvailable,
            "5.0",
            "5.1"));

        viewModel.CompleteUpdateCheck(new ApplicationUpdateCheckResult(
            ApplicationUpdateAvailability.CheckFailed,
            "5.0"));

        Assert.True(viewModel.IsKnownUpdateAvailable);
        Assert.True(viewModel.HasUpdateCheckFailed);

        viewModel.CompleteUpdateCheck(new ApplicationUpdateCheckResult(
            ApplicationUpdateAvailability.UpToDate,
            "5.1",
            "5.1"));

        Assert.False(viewModel.IsKnownUpdateAvailable);
        Assert.True(viewModel.IsApplicationUpToDate);
    }

    [Theory]
    [InlineData(ApplicationUpdateAvailability.UpToDate, UpdateCheckPresentationState.UpToDate)]
    [InlineData(
        ApplicationUpdateAvailability.CurrentVersionNewer,
        UpdateCheckPresentationState.CurrentVersionNewer)]
    [InlineData(ApplicationUpdateAvailability.CheckFailed, UpdateCheckPresentationState.Failed)]
    public void UpdateCheck_ResultMappingDoesNotFallBackToSuccess(
        ApplicationUpdateAvailability availability,
        UpdateCheckPresentationState expectedState)
    {
        using var viewModel = CreateViewModel();

        viewModel.CompleteUpdateCheck(new ApplicationUpdateCheckResult(
            availability,
            "4.9",
            availability == ApplicationUpdateAvailability.CheckFailed ? null : "4.9"));

        Assert.Equal(expectedState, viewModel.UpdateCheckState);
    }

    private static MainWindowViewModel CreateViewModel()
    {
        var catalog = new StubLocalizationCatalog(
            new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
            {
                [AppLanguage.En] = new Dictionary<string, string>
                {
                    ["Update.CurrentVersion"] = "Current version: {0}",
                    ["Update.LatestVersion"] = "Latest version: {0}",
                    ["Update.Check"] = "Check",
                    ["Update.CheckAgain"] = "Check again",
                    ["Update.Checking"] = "Checking…",
                    ["Update.Retry"] = "Try again"
                }
            });
        return new MainWindowViewModel(
            new LocalizationService(catalog, AppLanguage.En),
            new HelpContentProvider());
    }
}
