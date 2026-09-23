namespace DevProjex.Tests.Terminal;

public sealed class TerminalProjectContextFactoryRedactionStateTests
{
	[Fact]
	public async Task FailedPlanBuildPreservesActiveWorkspaceSecretMarks()
	{
		using var appData = new TemporaryDirectory();
		using var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var activeProject = Path.Combine(appData.Path, "active-project");
		var missingProject = Path.Combine(appData.Path, "missing-project");
		var activeMark = new MarkedSecretProfileEntry("001122334455", "TOKEN", 8);
		services.SecretRedactionSession.ReplacePersistentMarks(
			activeProject,
			new PersistentSecretMarksSnapshot(1, [activeMark]));

		var error = await Record.ExceptionAsync(() => services.ContextFactory.BuildAsync(
			missingProject,
			ProjectSelectionSpec.Standard,
			cancellationToken: TestContext.Current.CancellationToken));

		Assert.NotNull(error);
		Assert.Equal(activeMark, Assert.Single(services.SecretRedactionSession.GetMarkedSecrets()));
	}

	[Fact]
	public async Task CanceledPlanBuildPreservesActiveWorkspaceSecretMarks()
	{
		using var appData = new TemporaryDirectory();
		using var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var activeMark = new MarkedSecretProfileEntry("001122334455", "TOKEN", 8);
		services.SecretRedactionSession.ReplacePersistentMarks(
			appData.Path,
			new PersistentSecretMarksSnapshot(1, [activeMark]));
		using var canceled = new CancellationTokenSource();
		await canceled.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => services.ContextFactory.BuildAsync(
			appData.Path,
			ProjectSelectionSpec.Standard,
			cancellationToken: canceled.Token));

		Assert.Equal(activeMark, Assert.Single(services.SecretRedactionSession.GetMarkedSecrets()));
	}

	[Fact]
	public async Task CompletedPlanBuildAppliesItsSecretMarks()
	{
		using var appData = new TemporaryDirectory();
		using var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var activeMark = new MarkedSecretProfileEntry("001122334455", "TOKEN", 8);
		services.SecretRedactionSession.ReplacePersistentMarks(
			appData.Path,
			new PersistentSecretMarksSnapshot(1, [activeMark]));

		await services.ContextFactory.BuildAsync(
			appData.Path,
			ProjectSelectionSpec.Standard,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Empty(services.SecretRedactionSession.GetMarkedSecrets());
	}

	[Fact]
	public async Task CanceledPreparedTuiOpenDoesNotChangeActiveWorkspaceSecretMarks()
	{
		using var appData = new TemporaryDirectory();
		using var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var activeMark = new MarkedSecretProfileEntry("001122334455", "TOKEN", 8);
		services.SecretRedactionSession.ReplacePersistentMarks(
			appData.Path,
			new PersistentSecretMarksSnapshot(1, [activeMark]));
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var operation = new CancellationTokenSource();

		using var prepared = await controller.OpenAsync(
			appData.Path,
			ProjectProfileReference.Standard,
			operation.Token);
		await operation.CancelAsync();

		Assert.Equal(activeMark, Assert.Single(services.SecretRedactionSession.GetMarkedSecrets()));
	}

	[Fact]
	public async Task PublishedTuiOpenAppliesItsSecretMarks()
	{
		using var appData = new TemporaryDirectory();
		using var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var activeMark = new MarkedSecretProfileEntry("001122334455", "TOKEN", 8);
		services.SecretRedactionSession.ReplacePersistentMarks(
			appData.Path,
			new PersistentSecretMarksSnapshot(1, [activeMark]));
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());

		using var prepared = await controller.OpenAsync(
			appData.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);
		services.ContextFactory.ApplyMarkedSecrets(prepared.Plan.SourceRoot, prepared.Plan.Selection);

		Assert.Empty(services.SecretRedactionSession.GetMarkedSecrets());
	}
}
