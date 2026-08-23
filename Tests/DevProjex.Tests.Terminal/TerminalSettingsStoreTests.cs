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
	public async Task ExplicitCommandOptionDoesNotPersistBeforeTheTuiCapabilityGate()
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
			TerminalScreenMode.Auto,
			new TerminalSettingsStore(() => appData).LoadScreenMode());
	}

	[Fact]
	public async Task ScreenModeAndCommandHistoryRoundTripWithoutOverwritingEachOther()
	{
		using var workspace = new TemporaryDirectory();
		var store = new TerminalSettingsStore(() => workspace.Path);

		await store.SaveCommandHistoryAsync(
			["view content", "set hide-secrets on"],
			TestContext.Current.CancellationToken);
		await store.SaveScreenModeAsync(
			TerminalScreenMode.Inline,
			TestContext.Current.CancellationToken);

		var reloaded = new TerminalSettingsStore(() => workspace.Path);
		Assert.Equal(TerminalScreenMode.Inline, reloaded.LoadScreenMode());
		Assert.Equal(
			["view content", "set hide-secrets on"],
			reloaded.LoadCommandHistory());
	}

	[Fact]
	public async Task CommandHistoryIsNormalizedAndBoundedBeforePersistence()
	{
		using var workspace = new TemporaryDirectory();
		var store = new TerminalSettingsStore(() => workspace.Path);
		var commands = Enumerable.Range(0, 55)
			.Select(index => $"search item-{index}")
			.Append("search item-54")
			.ToArray();

		await store.SaveCommandHistoryAsync(commands, TestContext.Current.CancellationToken);

		var history = new TerminalSettingsStore(() => workspace.Path).LoadCommandHistory();
		Assert.Equal(50, history.Count);
		Assert.Equal("search item-5", history[0]);
		Assert.Equal("search item-54", history[^1]);
	}
}
