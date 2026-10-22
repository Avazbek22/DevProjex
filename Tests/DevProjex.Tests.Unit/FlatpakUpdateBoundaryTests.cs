using DevProjex.Application.Updates;
using DevProjex.Avalonia.Services;
using DevProjex.Infrastructure.ThemePresets;
using DevProjex.Infrastructure.Updates;

namespace DevProjex.Tests.Unit;

public sealed class FlatpakUpdateBoundaryTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void CompositionCreatesOnlyTheCorrectUpdateService(bool flatpak)
	{
		using var environment = FlatpakEnvironment.UseForTesting(new(flatpak, flatpak ? "test.app" : null));
		using var directory = new TemporaryDirectory();
		var services = AvaloniaCompositionRoot.CreateDefault(DesktopStartupOptions.Default, () => directory.Path);
		try
		{
			Assert.Equal(flatpak ? typeof(FlathubApplicationUpdateService) : typeof(GitHubReleaseUpdateService), services.ApplicationUpdateService.GetType());
		}
		finally
		{
			(services.ApplicationUpdateService as IDisposable)?.Dispose();
			(services.AgentJournalReader as IDisposable)?.Dispose();
		}
	}

	[Fact]
	public async Task StartupTimerManualAndPopoverPathsCannotReachTheReleaseServiceInFlatpak()
	{
		using var environment = FlatpakEnvironment.UseForTesting(new(true, "test.app"));
		using var directory = new TemporaryDirectory();
		using var model = new MainWindowViewModel(new(new JsonLocalizationCatalog(), AppLanguage.En), new HelpContentProvider());
		var rejected = new RejectUpdateService();
		using var coordinator = new ApplicationUpdateCoordinator(model, rejected, new UserSettingsStore(() => directory.Path), "5.2.1",
			() => new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero));
		await coordinator.SetAutomaticCheckEnabledAsync(true, TestContext.Current.CancellationToken);
		for (var tick = 0; tick < 3; tick++) await coordinator.RunAutomaticCheckIfDueAsync(TestContext.Current.CancellationToken);
		await coordinator.OpenManualCheckAsync(TestContext.Current.CancellationToken);
		await coordinator.CheckManuallyAsync(TestContext.Current.CancellationToken);
		model.UpdatePopoverOpen = true;
		Assert.False(model.UpdatePopoverOpen);
		Assert.False(model.IsUpdateCheckButtonVisible);
		Assert.Equal(0, rejected.Calls);
	}

	private sealed class RejectUpdateService : IApplicationUpdateService
	{
		public int Calls { get; private set; }
		public Task<ApplicationUpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
		{
			Calls++;
			throw new InvalidOperationException("Flatpak must not call the GitHub release service.");
		}
	}
}
