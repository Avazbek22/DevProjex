using DevProjex.Infrastructure.RecentProjects;
using DevProjex.Infrastructure.Persistence;
using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Tests.Unit;

public sealed class InfrastructureJsonPersistenceTests
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	[Fact]
	public void ProjectProfileStore_WritesCamelCaseEnumPayloadAndLoadsFullState()
	{
		using var temp = new TemporaryDirectory();
		var store = new ProjectProfileStore(() => Path.Combine(temp.Path, "appdata"));
		var projectPath = Path.Combine(temp.Path, "RepoA");
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: ["src"],
			SelectedExtensions: [".cs"],
			SelectedIgnoreOptions: [IgnoreOptionId.UseGitIgnore, IgnoreOptionId.DotFolders],
			RootFolderStates: new Dictionary<string, bool>(PathComparer.Default)
			{
				["src"] = true,
				["docs"] = false
			},
			ExtensionStates: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
			{
				[".cs"] = true,
				[".csv"] = false
			},
			IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.UseGitIgnore] = true,
				[IgnoreOptionId.DotFolders] = false
			});

		store.SaveProfile(projectPath, profile);

		using var document = JsonDocument.Parse(File.ReadAllText(store.GetPath()));
		var storedProfile = document.RootElement
			.GetProperty("profiles")
			.EnumerateObject()
			.Single()
			.Value;
		var selectedIgnoreOptions = storedProfile
			.GetProperty("selectedIgnoreOptions")
			.EnumerateArray()
			.Select(static item => item.GetString())
			.ToArray();

		Assert.Contains("useGitIgnore", selectedIgnoreOptions);
		Assert.DoesNotContain("dotFolders", selectedIgnoreOptions);
		Assert.False(storedProfile.GetProperty("rootFolderStates").GetProperty("docs").GetBoolean());
		Assert.False(storedProfile.GetProperty("extensionStates").GetProperty(".csv").GetBoolean());
		Assert.False(storedProfile.GetProperty("ignoreOptionStates").GetProperty("dotFolders").GetBoolean());

		Assert.True(store.TryLoadProfile(projectPath, out var loaded));
		Assert.True(loaded.RootFolderStates!["src"]);
		Assert.False(loaded.RootFolderStates!["docs"]);
		Assert.True(loaded.ExtensionStates![".cs"]);
		Assert.False(loaded.ExtensionStates![".csv"]);
		Assert.True(loaded.IgnoreOptionStates![IgnoreOptionId.UseGitIgnore]);
		Assert.False(loaded.IgnoreOptionStates![IgnoreOptionId.DotFolders]);
	}

	[Fact]
	public async Task ProjectProfileStore_LoadsMissingMarkedSecretsAndPersistsMarksInDedicatedStore()
	{
		using var temp = new TemporaryDirectory();
		var store = new ProjectProfileStore(() => Path.Combine(temp.Path, "appdata"));
		var projectPath = Path.Combine(temp.Path, "RepoBeforeManualMarks");
		var storePath = store.GetPath();
		Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
		File.WriteAllText(storePath, CreateLegacyProfileJson(projectPath));

		Assert.True(store.TryLoadProfile(projectPath, out var loaded));
		Assert.Empty(loaded.MarkedSecrets!);

		var write = await store.AddMarkAsync(
			projectPath,
			new MarkedSecretProfileEntry("9f2a4c1e8b3d", "STRIPE_SECRET_KEY", 24),
			TestContext.Current.CancellationToken);
		Assert.True(write.Succeeded);

		using var document = JsonDocument.Parse(File.ReadAllText(storePath));
		var persistedMarks = document.RootElement
			.GetProperty("profiles")
			.EnumerateObject()
			.Single()
			.Value
			.GetProperty("markedSecrets")
			.EnumerateArray();
		Assert.Empty(persistedMarks);
		Assert.True(store.TryLoadProfile(projectPath, out var reloaded));
		var persistedMark = Assert.Single(reloaded.MarkedSecrets!);
		Assert.Equal("9f2a4c1e8b3d", persistedMark.H);
		Assert.Equal("STRIPE_SECRET_KEY", persistedMark.Key);
		Assert.Equal(24, persistedMark.Length);
	}

	[Fact]
	public void ProjectProfileStore_LoadsLegacySelectedOnlyPayloadAndRewritesFullStateShape()
	{
		using var temp = new TemporaryDirectory();
		var store = new ProjectProfileStore(() => Path.Combine(temp.Path, "appdata"));
		var projectPath = Path.Combine(temp.Path, "RepoLegacy");
		var storePath = store.GetPath();
		Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
		File.WriteAllText(storePath, CreateLegacyProfileJson(projectPath));

		Assert.True(store.TryLoadProfile(projectPath, out var loaded));
		Assert.Contains("src", loaded.SelectedRootFolders);
		Assert.Contains(".cs", loaded.SelectedExtensions);
		Assert.Contains(IgnoreOptionId.SmartIgnore, loaded.SelectedIgnoreOptions);
		Assert.True(loaded.RootFolderStates!["src"]);
		Assert.True(loaded.ExtensionStates![".cs"]);
		Assert.True(loaded.IgnoreOptionStates![IgnoreOptionId.SmartIgnore]);

		store.SaveProfile(projectPath, loaded);

		using var document = JsonDocument.Parse(File.ReadAllText(storePath));
		var storedProfile = document.RootElement
			.GetProperty("profiles")
			.EnumerateObject()
			.Single()
			.Value;

		Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
		Assert.Equal(JsonValueKind.Object, storedProfile.GetProperty("rootFolderStates").ValueKind);
		Assert.Equal(JsonValueKind.Object, storedProfile.GetProperty("extensionStates").ValueKind);
		Assert.Equal(JsonValueKind.Object, storedProfile.GetProperty("ignoreOptionStates").ValueKind);
	}

	[Fact]
	public void ProjectProfileStore_MigratesLegacyHiddenSmartControllerFromEnabledGitState()
	{
		using var temp = new TemporaryDirectory();
		var store = new ProjectProfileStore(() => Path.Combine(temp.Path, "appdata"));
		var projectPath = Path.Combine(temp.Path, "RepoHybrid");
		var storePath = store.GetPath();
		Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
		var normalizedPath = JsonSerializer.Serialize(PathUtility.Normalize(projectPath));
		File.WriteAllText(storePath, $$"""
			{
			  "schemaVersion": 2,
			  "profiles": {
			    {{normalizedPath}}: {
			      "selectedRootFolders": [],
			      "selectedExtensions": [],
			      "selectedIgnoreOptions": [ "useGitIgnore" ],
			      "rootFolderStates": {},
			      "extensionStates": {},
			      "ignoreOptionStates": { "useGitIgnore": true },
			      "updatedUtc": "2026-01-01T00:00:00+00:00"
			    }
			  }
			}
			""");

		Assert.True(store.TryLoadProfile(projectPath, out var loaded));
		Assert.Contains(IgnoreOptionId.UseGitIgnore, loaded.SelectedIgnoreOptions);
		Assert.Contains(IgnoreOptionId.SmartIgnore, loaded.SelectedIgnoreOptions);
		Assert.True(loaded.IgnoreOptionStates![IgnoreOptionId.UseGitIgnore]);
		Assert.True(loaded.IgnoreOptionStates[IgnoreOptionId.SmartIgnore]);

		using var document = JsonDocument.Parse(File.ReadAllText(storePath));
		Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
	}

	[Fact]
	public void ProjectProfileStore_MigrationPreservesExplicitIndependentSmartState()
	{
		using var temp = new TemporaryDirectory();
		var store = new ProjectProfileStore(() => Path.Combine(temp.Path, "appdata"));
		var projectPath = Path.Combine(temp.Path, "RepoIndependent");
		var storePath = store.GetPath();
		Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
		var normalizedPath = JsonSerializer.Serialize(PathUtility.Normalize(projectPath));
		File.WriteAllText(storePath, $$"""
			{
			  "schemaVersion": 2,
			  "profiles": {
			    {{normalizedPath}}: {
			      "selectedRootFolders": [],
			      "selectedExtensions": [],
			      "selectedIgnoreOptions": [ "useGitIgnore" ],
			      "rootFolderStates": {},
			      "extensionStates": {},
			      "ignoreOptionStates": {
			        "useGitIgnore": true,
			        "smartIgnore": false
			      },
			      "updatedUtc": "2026-01-01T00:00:00+00:00"
			    }
			  }
			}
			""");

		Assert.True(store.TryLoadProfile(projectPath, out var loaded));
		Assert.True(loaded.IgnoreOptionStates![IgnoreOptionId.UseGitIgnore]);
		Assert.False(loaded.IgnoreOptionStates[IgnoreOptionId.SmartIgnore]);
		Assert.DoesNotContain(IgnoreOptionId.SmartIgnore, loaded.SelectedIgnoreOptions);
	}

	[Fact]
	public void UserAndThemeSettingsStores_WriteIndependentCleanCamelCaseDocuments()
	{
		using var temp = new TemporaryDirectory();
		var appDataPath = Path.Combine(temp.Path, "appdata");
		var store = new UserSettingsStore(() => appDataPath);
		var themeStore = new ThemeSettingsStore(() => appDataPath);
		var db = store.Load();
		db.ViewSettings = new AppViewSettings
		{
			IsCompactMode = true,
			IsTreeExpansionAnimationEnabled = false,
			IsTerminalCommandPromptDismissed = true,
			PreferredLanguage = AppLanguage.Ru
		};
		var themeDocument = themeStore.Load();
		themeDocument.SelectedPreset = "Dark.Acrylic";
		themeDocument.Presets["Dark.Acrylic"] = themeDocument.Presets["Dark.Acrylic"] with
		{
			BackgroundTransparency = 42
		};

		store.Save(db);
		Assert.True(themeStore.TrySave(themeDocument));

		using var userDocument = JsonDocument.Parse(File.ReadAllText(store.GetPath()));
		using var themeJson = JsonDocument.Parse(File.ReadAllText(themeStore.GetPath()));
		var preset = themeJson.RootElement.GetProperty("presets").GetProperty("Dark.Acrylic");
		var viewSettings = userDocument.RootElement.GetProperty("viewSettings");

		Assert.Equal(42, preset.GetProperty("backgroundTransparency").GetDouble());
		Assert.False(preset.TryGetProperty("theme", out _));
		Assert.False(preset.TryGetProperty("effect", out _));
		Assert.True(viewSettings.GetProperty("isTerminalCommandPromptDismissed").GetBoolean());
		Assert.Equal("ru", viewSettings.GetProperty("preferredLanguage").GetString());
		Assert.False(userDocument.RootElement.TryGetProperty("presets", out _));

		var loaded = store.Load();
		Assert.True(loaded.ViewSettings.IsCompactMode);
		Assert.False(loaded.ViewSettings.IsTreeExpansionAnimationEnabled);
		Assert.True(loaded.ViewSettings.IsTerminalCommandPromptDismissed);
		Assert.Equal(AppLanguage.Ru, loaded.ViewSettings.PreferredLanguage);
		Assert.Equal(42, themeStore.Load().Presets["Dark.Acrylic"].BackgroundTransparency);
	}

	[Fact]
	public void RecentProjectsStore_WritesCamelCaseShapeAndLoadsNormalizedEntries()
	{
		using var temp = new TemporaryDirectory();
		var store = new RecentProjectsStore(() => Path.Combine(temp.Path, "appdata"));
		var folderPath = Path.Combine(temp.Path, "RepoA");
		Directory.CreateDirectory(folderPath);

		store.AddFolder(null, folderPath);
		store.AddRepository(null, "https://github.com/example/repo.git/");

		using var document = JsonDocument.Parse(File.ReadAllText(store.GetPath()));
		Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("recentFolders").ValueKind);
		Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("recentRepositories").ValueKind);

		var loaded = store.Load();
		Assert.Single(loaded.RecentFolders);
		Assert.Single(loaded.RecentRepositories);
		Assert.Equal(PathUtility.Normalize(folderPath), loaded.RecentFolders[0].Path);
		Assert.Equal("https://github.com/example/repo.git", loaded.RecentRepositories[0].Url);
	}

	[Fact]
	public void JsonStorePersistence_TryReadNormalized_InvalidJsonDoesNotRewriteOrMutatePrimary()
	{
		using var temp = new TemporaryDirectory();
		var fileSet = CreateFileSet(temp, "settings.json");
		Directory.CreateDirectory(fileSet.DirectoryPath);
		File.WriteAllText(fileSet.PrimaryPath, "{ invalid json");

		var result = JsonStorePersistence.TryReadNormalized(
			fileSet.PrimaryPath,
			JsonOptions,
			static () => new TestDocument("default", 0),
			static document => document with { Name = document.Name.Trim() },
			out var document,
			out var requiresRewrite);

		Assert.False(result);
		Assert.False(requiresRewrite);
		Assert.Equal(new TestDocument("default", 0), document);
		Assert.Equal("{ invalid json", File.ReadAllText(fileSet.PrimaryPath));
	}

	[Fact]
	public void JsonStorePersistence_TryReadNormalized_ReportsRewriteOnlyAfterSuccessfulNormalization()
	{
		using var temp = new TemporaryDirectory();
		var fileSet = CreateFileSet(temp, "settings.json");
		Directory.CreateDirectory(fileSet.DirectoryPath);
		File.WriteAllText(fileSet.PrimaryPath, """{"name":" Project ","count":3}""");

		var result = JsonStorePersistence.TryReadNormalized(
			fileSet.PrimaryPath,
			JsonOptions,
			static () => new TestDocument("default", 0),
			static document => document with { Name = document.Name.Trim() },
			out var document,
			out var requiresRewrite);

		Assert.True(result);
		Assert.True(requiresRewrite);
		Assert.Equal(new TestDocument("Project", 3), document);
	}

	[Fact]
	public void JsonStorePersistence_TryReadNormalized_RejectsDocumentBeyondExplicitLimit()
	{
		using var temp = new TemporaryDirectory();
		var fileSet = CreateFileSet(temp, "settings.json");
		Directory.CreateDirectory(fileSet.DirectoryPath);
		using (var stream = new FileStream(fileSet.PrimaryPath, FileMode.CreateNew, FileAccess.Write))
			stream.SetLength(257);

		var result = JsonStorePersistence.TryReadNormalized(
			fileSet.PrimaryPath,
			JsonOptions,
			static () => new TestDocument("default", 0),
			static document => document,
			out var document,
			out var requiresRewrite,
			maximumDocumentBytes: 256);

		Assert.False(result);
		Assert.False(requiresRewrite);
		Assert.Equal(new TestDocument("default", 0), document);
		Assert.Equal(257, new FileInfo(fileSet.PrimaryPath).Length);
	}

	[Fact]
	public void JsonStorePersistence_ContainsFutureDocument_ProtectsDocumentBeyondExplicitLimit()
	{
		using var temp = new TemporaryDirectory();
		var fileSet = CreateFileSet(temp, "settings.json");
		Directory.CreateDirectory(fileSet.DirectoryPath);
		using (var stream = new FileStream(fileSet.PrimaryPath, FileMode.CreateNew, FileAccess.Write))
			stream.SetLength(257);

		var protectedDocument = JsonStorePersistence.ContainsFutureDocument(
			fileSet,
			currentSchemaVersion: 1,
			maximumDocumentBytes: 256);

		Assert.True(protectedDocument);
		Assert.Equal(257, new FileInfo(fileSet.PrimaryPath).Length);
	}

	[Fact]
	public void JsonStorePersistence_TryWriteAtomic_CommitsPrimaryMirrorsBackupAndLeavesNoTempFiles()
	{
		using var temp = new TemporaryDirectory();
		var fileSet = CreateFileSet(temp, "settings.json");

		var result = JsonStorePersistence.TryWriteAtomic(
			fileSet,
			new TestDocument("committed", 7),
			JsonOptions);

		Assert.True(result);
		Assert.True(File.Exists(fileSet.PrimaryPath));
		Assert.True(File.Exists(fileSet.BackupPath));
		Assert.Equal(File.ReadAllText(fileSet.PrimaryPath), File.ReadAllText(fileSet.BackupPath));
		Assert.Empty(Directory.EnumerateFiles(fileSet.DirectoryPath, "*.tmp"));
		Assert.Contains("\"name\":\"committed\"", File.ReadAllText(fileSet.PrimaryPath));
	}

	[Fact]
	public void JsonStorePersistence_TryWriteAtomic_ReturnsFalseForUnresolvablePrimaryDirectory()
	{
		var fileSet = new JsonStoreFileSet("settings.json", "settings.json.bak", "settings.json.lock");

		var result = JsonStorePersistence.TryWriteAtomic(
			fileSet,
			new TestDocument("ignored", 1),
			JsonOptions);

		Assert.False(result);
	}

	[Fact]
	public void JsonStorePersistence_DurableWriteDoesNotReportSuccessWithoutBackup()
	{
		using var temp = new TemporaryDirectory();
		var fileSet = CreateFileSet(temp, "secret-marks.json");
		Directory.CreateDirectory(fileSet.DirectoryPath);
		File.WriteAllText(fileSet.PrimaryPath, "old");
		var operations = new JsonStoreWriteOperations(
			static (_, _, _) => throw new PlatformNotSupportedException("replace unavailable"),
			static (_, _, _) => throw new IOException("backup unavailable"));

		var result = JsonStorePersistence.TryWriteAtomicDurable(
			fileSet,
			new TestDocument("committed", 7),
			JsonOptions,
			operations);

		Assert.False(result);
		Assert.Contains("\"name\":\"committed\"", File.ReadAllText(fileSet.PrimaryPath));
		Assert.False(File.Exists(fileSet.BackupPath));
	}

	[Fact]
	public void CrossProcessFileLock_AcquireFailsFastWhenSidecarLockIsAlreadyHeld()
	{
		using var temp = new TemporaryDirectory();
		var fileSet = CreateFileSet(temp, "settings.json");
		using var heldLock = CrossProcessFileLock.Acquire(fileSet, TimeSpan.Zero);

		Assert.ThrowsAny<IOException>(() => CrossProcessFileLock.Acquire(fileSet, TimeSpan.Zero));
	}

	[Fact]
	public void CrossProcessFileLock_BoundedWaitTimesOutAndDoesNotPoisonTheSidecar()
	{
		using var temp = new TemporaryDirectory();
		var fileSet = CreateFileSet(temp, "settings.json");
		var timeout = TimeSpan.FromMilliseconds(75);
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();
		using (CrossProcessFileLock.Acquire(fileSet, TimeSpan.Zero))
		{
			Assert.ThrowsAny<IOException>(() => CrossProcessFileLock.Acquire(fileSet, timeout));
		}
		stopwatch.Stop();

		Assert.True(stopwatch.Elapsed >= timeout, $"Lock wait ended after {stopwatch.Elapsed}.");
		using var reacquired = CrossProcessFileLock.Acquire(fileSet, TimeSpan.Zero);
		Assert.NotNull(reacquired);
	}

	[Fact]
	public void CrossProcessFileLock_DisposeReleasesSidecarLockForNextWriter()
	{
		using var temp = new TemporaryDirectory();
		var fileSet = CreateFileSet(temp, "settings.json");

		using (CrossProcessFileLock.Acquire(fileSet, TimeSpan.Zero))
		{
			Assert.True(File.Exists(fileSet.LockPath));
		}

		using var reacquired = CrossProcessFileLock.Acquire(fileSet, TimeSpan.Zero);
		Assert.NotNull(reacquired);
	}

	private static string CreateLegacyProfileJson(string projectPath)
	{
		var normalizedPath = JsonSerializer.Serialize(PathUtility.Normalize(projectPath));
		return $$"""
			{
			  "schemaVersion": 1,
			  "profiles": {
			    {{normalizedPath}}: {
			      "selectedRootFolders": [ "src" ],
			      "selectedExtensions": [ ".cs" ],
			      "selectedIgnoreOptions": [ "smartIgnore" ],
			      "updatedUtc": "2026-01-01T00:00:00+00:00"
			    }
			  }
			}
			""";
	}

	private static JsonStoreFileSet CreateFileSet(TemporaryDirectory temp, string fileName)
	{
		var primaryPath = Path.Combine(temp.Path, "appdata", fileName);
		return new JsonStoreFileSet(primaryPath, $"{primaryPath}.bak", $"{primaryPath}.lock");
	}

	private sealed record TestDocument(string Name, int Count);
}
