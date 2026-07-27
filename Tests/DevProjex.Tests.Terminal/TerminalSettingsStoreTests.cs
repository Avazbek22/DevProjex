namespace DevProjex.Tests.Terminal;

public sealed class TerminalSettingsStoreTests
{
	[Theory]
	[InlineData(TerminalScreenMode.Auto)]
	[InlineData(TerminalScreenMode.Alternate)]
	[InlineData(TerminalScreenMode.Inline)]
	public async Task ExplicitScreenModeSurvivesANewStoreInstance(TerminalScreenMode screenMode)
	{
		using var workspace = new TemporaryDirectory();
		var store = new TerminalSettingsStore(() => workspace.Path);

		await store.SaveScreenModeAsync(screenMode, TestContext.Current.CancellationToken);

		var reloaded = new TerminalSettingsStore(() => workspace.Path).LoadScreenMode();
		Assert.Equal(screenMode, reloaded);
		Assert.True(File.Exists(store.GetPath()));
		Assert.Empty(Directory.EnumerateFiles(
			Path.GetDirectoryName(store.GetPath())!,
			"*.tmp",
			SearchOption.TopDirectoryOnly));
	}

	[Fact]
	public void MissingOrCorruptSettingsSafelyFallBackToAuto()
	{
		using var workspace = new TemporaryDirectory();
		var store = new TerminalSettingsStore(() => workspace.Path);

		Assert.Equal(TerminalScreenMode.Auto, store.LoadScreenMode());
		Directory.CreateDirectory(Path.GetDirectoryName(store.GetPath())!);
		File.WriteAllText(store.GetPath(), "{ invalid json");

		Assert.Equal(TerminalScreenMode.Auto, store.LoadScreenMode());
	}

	[Fact]
	public async Task ExplicitCommandOptionIsPersistedBeforeTheTuiCapabilityGate()
	{
		using var workspace = new TemporaryDirectory();
		var appData = workspace.CreateDirectory("app-data");
		var environment = new TestTerminalEnvironment();
		var application = new TerminalApplication(
			environment,
			new TerminalServiceFactory(() => appData));

		var exitCode = await application.RunAsync(
			["tui", workspace.Path, "--screen", "inline", "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Equal(
			TerminalScreenMode.Inline,
			new TerminalSettingsStore(() => appData).LoadScreenMode());
	}
}
