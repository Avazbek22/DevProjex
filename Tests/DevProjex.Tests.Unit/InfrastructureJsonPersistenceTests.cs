using DevProjex.Infrastructure.RecentProjects;
using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Tests.Unit;

public sealed class InfrastructureJsonPersistenceTests
{
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
		Assert.Contains("dotFolders", selectedIgnoreOptions);
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
		Assert.NotNull(loaded.RootFolderStates);
		Assert.NotNull(loaded.ExtensionStates);
		Assert.NotNull(loaded.IgnoreOptionStates);
		Assert.Empty(loaded.RootFolderStates);
		Assert.Empty(loaded.ExtensionStates);
		Assert.Empty(loaded.IgnoreOptionStates);

		store.SaveProfile(projectPath, loaded);

		using var document = JsonDocument.Parse(File.ReadAllText(storePath));
		var storedProfile = document.RootElement
			.GetProperty("profiles")
			.EnumerateObject()
			.Single()
			.Value;

		Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
		Assert.Equal(JsonValueKind.Object, storedProfile.GetProperty("rootFolderStates").ValueKind);
		Assert.Equal(JsonValueKind.Object, storedProfile.GetProperty("extensionStates").ValueKind);
		Assert.Equal(JsonValueKind.Object, storedProfile.GetProperty("ignoreOptionStates").ValueKind);
	}

	[Fact]
	public void UserSettingsStore_WritesCamelCaseEnumValuesAndLoadsViewSettings()
	{
		using var temp = new TemporaryDirectory();
		var store = new UserSettingsStore(() => Path.Combine(temp.Path, "appdata"));
		var db = store.Load();
		db.LastSelected = "Dark.Acrylic";
		db.ViewSettings = new AppViewSettings
		{
			IsCompactMode = true,
			IsTreeAnimationEnabled = true,
			IsAdvancedIgnoreCountsEnabled = false,
			PreferredLanguage = AppLanguage.Ru
		};
		db.Presets["Dark.Acrylic"] = db.Presets["Dark.Acrylic"] with
		{
			Theme = ThemeVariant.Dark,
			Effect = ThemeEffectMode.Acrylic,
			MaterialIntensity = 42
		};

		store.Save(db);

		using var document = JsonDocument.Parse(File.ReadAllText(store.GetPath()));
		var preset = document.RootElement.GetProperty("presets").GetProperty("Dark.Acrylic");
		var viewSettings = document.RootElement.GetProperty("viewSettings");

		Assert.Equal("dark", preset.GetProperty("theme").GetString());
		Assert.Equal("acrylic", preset.GetProperty("effect").GetString());
		Assert.Equal("ru", viewSettings.GetProperty("preferredLanguage").GetString());

		var loaded = store.Load();
		Assert.True(loaded.ViewSettings.IsCompactMode);
		Assert.True(loaded.ViewSettings.IsTreeAnimationEnabled);
		Assert.False(loaded.ViewSettings.IsAdvancedIgnoreCountsEnabled);
		Assert.Equal(AppLanguage.Ru, loaded.ViewSettings.PreferredLanguage);
		Assert.Equal(ThemeEffectMode.Acrylic, loaded.Presets["Dark.Acrylic"].Effect);
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
}
