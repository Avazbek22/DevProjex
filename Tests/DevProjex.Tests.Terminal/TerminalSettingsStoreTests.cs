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
	public void OversizedSettingsSafelyFallBackWithoutDeserializingTheDocument()
	{
		using var workspace = new TemporaryDirectory();
		var store = new TerminalSettingsStore(() => workspace.Path);
		Directory.CreateDirectory(Path.GetDirectoryName(store.GetPath())!);
		File.WriteAllText(
			store.GetPath(),
			"{\"SchemaVersion\":1,\"ScreenMode\":2,\"CommandHistory\":[]}" +
			new string(' ', 1024 * 1024));

		Assert.Equal(TerminalScreenMode.Auto, store.LoadScreenMode());
		Assert.Empty(store.LoadCommandHistory());
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
	public async Task UpdateWaitsForTheSharedStoreLockAndPreservesTheCommittedDocument()
	{
		using var workspace = new TemporaryDirectory();
		var store = new TerminalSettingsStore(() => workspace.Path);
		var settingsPath = store.GetPath();
		Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
		using var heldLock = new FileStream(
			settingsPath + ".lock",
			FileMode.OpenOrCreate,
			FileAccess.ReadWrite,
			FileShare.None);

		var update = store.SaveScreenModeAsync(
			TerminalScreenMode.Inline,
			TestContext.Current.CancellationToken);
		await Task.Delay(100, TestContext.Current.CancellationToken);
		Assert.False(update.IsCompleted);

		File.WriteAllText(
			settingsPath,
			"{\"SchemaVersion\":1,\"ScreenMode\":0,\"CommandHistory\":[\"external command\"]}");
		heldLock.Dispose();
		await update.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

		var reloaded = new TerminalSettingsStore(() => workspace.Path);
		Assert.Equal(TerminalScreenMode.Inline, reloaded.LoadScreenMode());
		Assert.Equal(["external command"], reloaded.LoadCommandHistory());
	}

	[Fact]
	public async Task ConcurrentReaderDoesNotBlockAtomicReplacement()
	{
		using var workspace = new TemporaryDirectory();
		var writer = new TerminalSettingsStore(() => workspace.Path);
		await writer.SaveScreenModeAsync(
			TerminalScreenMode.Auto,
			TestContext.Current.CancellationToken);
		using var readerOpened = new ManualResetEventSlim();
		using var releaseReader = new ManualResetEventSlim();
		var reader = new TerminalSettingsStore(
			() => workspace.Path,
			() =>
			{
				readerOpened.Set();
				if (!releaseReader.Wait(
					    TimeSpan.FromSeconds(5),
					    TestContext.Current.CancellationToken))
					throw new TimeoutException("The settings reader was not released by the test.");
			});
		var read = Task.Run(reader.LoadScreenMode, TestContext.Current.CancellationToken);
		Assert.True(readerOpened.Wait(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken));

		try
		{
			await writer.SaveScreenModeAsync(
				TerminalScreenMode.Inline,
				TestContext.Current.CancellationToken);
		}
		finally
		{
			releaseReader.Set();
		}

		Assert.Equal(TerminalScreenMode.Auto, await read);
		Assert.Equal(
			TerminalScreenMode.Inline,
			new TerminalSettingsStore(() => workspace.Path).LoadScreenMode());
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

	[Fact]
	public async Task CommandHistoryLengthLimitNeverSplitsAUnicodeScalar()
	{
		using var workspace = new TemporaryDirectory();
		var store = new TerminalSettingsStore(() => workspace.Path);
		var prefix = new string('x', 4_095);

		await store.SaveCommandHistoryAsync(
			[prefix + "😀tail"],
			TestContext.Current.CancellationToken);

		Assert.Equal(
			[prefix],
			new TerminalSettingsStore(() => workspace.Path).LoadCommandHistory());
	}

	[Fact]
	public async Task FutureSchemaIsNeverOverwrittenByOlderStore()
	{
		using var workspace = new TemporaryDirectory();
		var store = new TerminalSettingsStore(() => workspace.Path);
		Directory.CreateDirectory(Path.GetDirectoryName(store.GetPath())!);
		const string futureDocument =
			"{\"SchemaVersion\":2,\"ScreenMode\":2,\"CommandHistory\":[\"future command\"],\"FutureValue\":true}";
		File.WriteAllText(store.GetPath(), futureDocument);

		Assert.Equal(TerminalScreenMode.Auto, store.LoadScreenMode());
		Assert.Empty(store.LoadCommandHistory());

		await store.SaveScreenModeAsync(
			TerminalScreenMode.Inline,
			TestContext.Current.CancellationToken);
		await store.SaveCommandHistoryAsync(
			["view content"],
			TestContext.Current.CancellationToken);

		Assert.Equal(futureDocument, File.ReadAllText(store.GetPath()));
	}

	[Fact]
	public async Task PersistedSettingsArePrivateToTheCurrentUnixUser()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("Unix file modes are not available on Windows.");
			return;
		}

		using var workspace = new TemporaryDirectory();
		var store = new TerminalSettingsStore(() => workspace.Path);

		await store.SaveCommandHistoryAsync(
			["search confidential-project-name"],
			TestContext.Current.CancellationToken);

		Assert.Equal(
			UnixFileMode.UserRead | UnixFileMode.UserWrite,
			File.GetUnixFileMode(store.GetPath()));
	}
}
