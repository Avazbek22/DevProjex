namespace DevProjex.Tests.Terminal;

public sealed class TerminalSettingsStoreTests
{
	[Fact]
	public async Task ProjectSettingsRoundTripAndUseLruCapacity()
	{
		using var workspace = new TemporaryDirectory();
		var store = new TerminalSettingsStore(() => workspace.Path);
		for (var index = 0; index < 34; index++)
		{
			var root = workspace.CreateDirectory($"project-{index}");
			await store.SaveProjectSettingsAsync(new TerminalProjectSettings(
				root,
				["src/a.cs"],
				[".", "src"],
				"src/a.cs",
				ProjectContextView.Content,
				ProjectContextDocumentFormat.Markdown,
				DateTimeOffset.MinValue), TestContext.Current.CancellationToken);
		}

		var newest = store.LoadProjectSettings(Path.Combine(workspace.Path, "project-33"));
		var evicted = store.LoadProjectSettings(Path.Combine(workspace.Path, "project-0"));
		Assert.NotNull(newest);
		Assert.Equal(["src/a.cs"], newest.SelectedPaths);
		Assert.Equal(ProjectContextView.Content, newest.PreviewView);
		Assert.Null(evicted);
	}
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

	[Theory]
	[InlineData(AppLanguage.En)]
	[InlineData(AppLanguage.Ja)]
	[InlineData(AppLanguage.ZhCn)]
	public async Task TerminalLanguageSurvivesANewStoreInstance(AppLanguage language)
	{
		using var workspace = new TemporaryDirectory();
		var store = new TerminalSettingsStore(() => workspace.Path);

		await store.SaveLanguageAsync(language, TestContext.Current.CancellationToken);

		Assert.Equal(language, new TerminalSettingsStore(() => workspace.Path).LoadLanguage());
		Assert.Contains(
			$"\"Language\":\"{AppLanguageUtility.ToCode(language)}\"",
			File.ReadAllText(store.GetPath()),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task LanguageScreenModeAndHistoryRoundTripWithoutOverwritingEachOther()
	{
		using var workspace = new TemporaryDirectory();
		var store = new TerminalSettingsStore(() => workspace.Path);

		await store.SaveLanguageAsync(AppLanguage.Ja, TestContext.Current.CancellationToken);
		await store.SaveCommandHistoryAsync(["language ja"], TestContext.Current.CancellationToken);
		await store.SaveScreenModeAsync(TerminalScreenMode.Inline, TestContext.Current.CancellationToken);

		var reloaded = new TerminalSettingsStore(() => workspace.Path);
		Assert.Equal(AppLanguage.Ja, reloaded.LoadLanguage());
		Assert.Equal(["language ja"], reloaded.LoadCommandHistory());
		Assert.Equal(TerminalScreenMode.Inline, reloaded.LoadScreenMode());
	}

	[Fact]
	public void UnsupportedPersistedLanguageIsIgnored()
	{
		using var workspace = new TemporaryDirectory();
		var store = new TerminalSettingsStore(() => workspace.Path);
		Directory.CreateDirectory(Path.GetDirectoryName(store.GetPath())!);
		File.WriteAllText(
			store.GetPath(),
			"{\"SchemaVersion\":1,\"ScreenMode\":0,\"CommandHistory\":[],\"Language\":\"klingon\"}");

		Assert.Null(store.LoadLanguage());
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
	public async Task NonObjectSettingsSafelyFallBackAndCanBeReplaced()
	{
		using var workspace = new TemporaryDirectory();
		var store = new TerminalSettingsStore(() => workspace.Path);
		Directory.CreateDirectory(Path.GetDirectoryName(store.GetPath())!);
		File.WriteAllText(store.GetPath(), "[]");

		Assert.Equal(TerminalScreenMode.Auto, store.LoadScreenMode());
		Assert.Empty(store.LoadCommandHistory());

		await store.SaveScreenModeAsync(
			TerminalScreenMode.Inline,
			TestContext.Current.CancellationToken);
		Assert.Equal(TerminalScreenMode.Inline, store.LoadScreenMode());
	}

	[Fact]
	public async Task OversizedSettingsSafelyFallBackWithoutOverwritingTheDocument()
	{
		using var workspace = new TemporaryDirectory();
		var store = new TerminalSettingsStore(() => workspace.Path);
		Directory.CreateDirectory(Path.GetDirectoryName(store.GetPath())!);
		var oversizedDocument =
			"{\"SchemaVersion\":1,\"ScreenMode\":2,\"CommandHistory\":[]}" +
			new string(' ', 1024 * 1024);
		File.WriteAllText(store.GetPath(), oversizedDocument);

		Assert.Equal(TerminalScreenMode.Auto, store.LoadScreenMode());
		Assert.Empty(store.LoadCommandHistory());

		await store.SaveScreenModeAsync(
			TerminalScreenMode.Inline,
			TestContext.Current.CancellationToken);

		Assert.Equal(oversizedDocument, File.ReadAllText(store.GetPath()));
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
	public async Task FutureSchemaWithIncompatibleKnownFieldIsNeverOverwritten()
	{
		using var workspace = new TemporaryDirectory();
		var store = new TerminalSettingsStore(() => workspace.Path);
		Directory.CreateDirectory(Path.GetDirectoryName(store.GetPath())!);
		const string futureDocument =
			"{\"SchemaVersion\":2,\"ScreenMode\":{\"mode\":\"inline\"},\"CommandHistory\":[\"future command\"]}";
		File.WriteAllText(store.GetPath(), futureDocument);

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

	[Fact]
	public async Task ExistingAndFutureSettingsAreRestrictedToTheCurrentUnixUser()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("Unix file modes are not available on Windows.");
			return;
		}

		using var workspace = new TemporaryDirectory();
		var store = new TerminalSettingsStore(() => workspace.Path);
		Directory.CreateDirectory(Path.GetDirectoryName(store.GetPath())!);
		const UnixFileMode legacyMode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
		                                UnixFileMode.GroupRead | UnixFileMode.OtherRead;
		const UnixFileMode expectedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		const string currentDocument =
			"{\"SchemaVersion\":1,\"ScreenMode\":0,\"CommandHistory\":[\"confidential-project\"]}";
		File.WriteAllText(store.GetPath(), currentDocument);
		File.SetUnixFileMode(store.GetPath(), legacyMode);

		Assert.Equal(["confidential-project"], store.LoadCommandHistory());
		Assert.Equal(expectedMode, File.GetUnixFileMode(store.GetPath()));

		const string futureDocument =
			"{\"SchemaVersion\":2,\"ScreenMode\":0,\"CommandHistory\":[\"future\"],\"FutureValue\":true}";
		File.WriteAllText(store.GetPath(), futureDocument);
		File.SetUnixFileMode(store.GetPath(), legacyMode);
		await store.SaveScreenModeAsync(
			TerminalScreenMode.Inline,
			TestContext.Current.CancellationToken);

		Assert.Equal(futureDocument, File.ReadAllText(store.GetPath()));
		Assert.Equal(expectedMode, File.GetUnixFileMode(store.GetPath()));
	}
}
