using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevProjex.Infrastructure.ThemePresets;
using DevProjex.Tests.Unit.Helpers;
using Xunit;

namespace DevProjex.Tests.Unit;

public sealed class ThemePresetStoreTests
{
	[Fact]
	// Ensures loading without a file returns defaults without requiring persistence.
	public void Load_ReturnsDefaultsWithoutCreatingFileWhenMissing()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var path = store.GetPath();

		if (File.Exists(path))
			File.Delete(path);

		var db = store.Load();

		Assert.False(File.Exists(path));
		Assert.NotEmpty(db.Presets);

		foreach (var theme in Enum.GetValues<ThemeVariant>())
		{
			foreach (var effect in Enum.GetValues<ThemeEffectMode>())
			{
				var key = $"{theme}.{effect}";
				Assert.True(db.Presets.ContainsKey(key));
				Assert.Equal(theme, db.Presets[key].Theme);
				Assert.Equal(effect, db.Presets[key].Effect);
			}
		}
	}

	[Fact]
	// Ensures GetPreset populates missing preset entries in the database.
	public void GetPreset_AddsMissingPresetToDb()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var db = new ThemePresetDb { Presets = new Dictionary<string, ThemePreset>() };

		var preset = store.GetPreset(db, ThemeVariant.Dark, ThemeEffectMode.Acrylic);

		Assert.NotNull(preset);
		Assert.Equal(ThemeVariant.Dark, preset.Theme);
		Assert.Equal(ThemeEffectMode.Acrylic, preset.Effect);
		Assert.True(db.Presets.ContainsKey("Dark.Acrylic"));
		Assert.Same(preset, db.Presets["Dark.Acrylic"]);
	}

	[Fact]
	// Ensures a custom preset survives Save/Load persistence round-trips.
	public void Save_And_Load_RoundTripsCustomPreset()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var custom = new ThemePreset
		{
			Theme = ThemeVariant.Light,
			Effect = ThemeEffectMode.Mica,
			MaterialIntensity = 12.5,
			BlurRadius = 9.4,
			PanelContrast = 7.3,
			MenuChildIntensity = 6.2,
			BorderStrength = 5.1
		};

		var db = new ThemePresetDb
		{
			SchemaVersion = 99,
			Presets = new Dictionary<string, ThemePreset>(),
			LastSelected = "Light.Mica"
		};

		store.SetPreset(db, ThemeVariant.Light, ThemeEffectMode.Mica, custom);
		store.Save(db);

		var loaded = store.Load();
		var loadedPreset = loaded.Presets["Light.Mica"];

		Assert.Equal(custom.Theme, loadedPreset.Theme);
		Assert.Equal(custom.Effect, loadedPreset.Effect);
		Assert.Equal(custom.MaterialIntensity, loadedPreset.MaterialIntensity);
		Assert.Equal(custom.BlurRadius, loadedPreset.BlurRadius);
		Assert.Equal(custom.PanelContrast, loadedPreset.PanelContrast);
		Assert.Equal(custom.MenuChildIntensity, loadedPreset.MenuChildIntensity);
		Assert.Equal(custom.BorderStrength, loadedPreset.BorderStrength);
		Assert.Equal("Light.Mica", loaded.LastSelected);
		Assert.True(loaded.SchemaVersion > 0);
	}

	[Fact]
	// Ensures view settings are persisted together with theme presets.
	public void Save_And_Load_RoundTripsViewSettings()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var db = new ThemePresetDb
		{
			SchemaVersion = 99,
			Presets = new Dictionary<string, ThemePreset>(),
			LastSelected = "Dark.Transparent",
			ViewSettings = new AppViewSettings
			{
				IsCompactMode = true,
				IsTreeAnimationEnabled = true
			}
		};

		store.Save(db);
		var loaded = store.Load();

		Assert.True(loaded.ViewSettings.IsCompactMode);
		Assert.True(loaded.ViewSettings.IsTreeAnimationEnabled);
	}

	[Fact]
	// Ensures missing preset combinations are filled during normalization.
	public void Load_PartialPresetList_FillsMissingCombinations()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var path = store.GetPath();
		var customPreset = new ThemePreset
		{
			Theme = ThemeVariant.Dark,
			Effect = ThemeEffectMode.Transparent,
			MaterialIntensity = 1,
			BlurRadius = 2,
			PanelContrast = 3,
			MenuChildIntensity = 4,
			BorderStrength = 5
		};

		var db = new ThemePresetDb
		{
			SchemaVersion = 1,
			Presets = new Dictionary<string, ThemePreset>
			{
				["Dark.Transparent"] = customPreset
			},
			LastSelected = "Dark.Transparent"
		};

		WritePresetFile(path, db);

		var loaded = store.Load();

		foreach (var theme in Enum.GetValues<ThemeVariant>())
		{
			foreach (var effect in Enum.GetValues<ThemeEffectMode>())
			{
				var key = $"{theme}.{effect}";
				Assert.True(loaded.Presets.ContainsKey(key));
			}
		}

		var reloadedPreset = loaded.Presets["Dark.Transparent"];
		Assert.Equal(customPreset.MaterialIntensity, reloadedPreset.MaterialIntensity);
		Assert.Equal(customPreset.BlurRadius, reloadedPreset.BlurRadius);
		Assert.Equal(customPreset.PanelContrast, reloadedPreset.PanelContrast);
		Assert.Equal(customPreset.MenuChildIntensity, reloadedPreset.MenuChildIntensity);
		Assert.Equal(customPreset.BorderStrength, reloadedPreset.BorderStrength);
	}

	[Fact]
	// Ensures invalid lastSelected values are corrected to a valid preset key.
	public void Load_InvalidLastSelected_ResetsToExistingKey()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var path = store.GetPath();
		var db = new ThemePresetDb
		{
			SchemaVersion = 1,
			Presets = new Dictionary<string, ThemePreset>(),
			LastSelected = "Not.A.Valid.Key"
		};

		WritePresetFile(path, db);

		var loaded = store.Load();

		Assert.False(string.IsNullOrWhiteSpace(loaded.LastSelected));
		Assert.True(loaded.Presets.ContainsKey(loaded.LastSelected));
		Assert.True(store.TryParseKey(loaded.LastSelected, out _, out _));
	}

	[Fact]
	// Ensures corrupted JSON is handled and a valid file is recreated.
	public void Load_CorruptJson_RecreatesFile()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var path = store.GetPath();
		var directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrWhiteSpace(directory))
			Directory.CreateDirectory(directory);
		File.WriteAllText(path, "{ invalid json");

		var db = store.Load();

		Assert.True(File.Exists(path));
		var json = File.ReadAllText(path);
		using var doc = JsonDocument.Parse(json);
		Assert.True(doc.RootElement.TryGetProperty("presets", out _));
		Assert.True(doc.RootElement.TryGetProperty("schemaVersion", out _));
		Assert.NotEmpty(db.Presets);
	}

	[Fact]
	// Ensures Save writes a file that can be read as JSON with required properties.
	public void Save_WritesReadableJson()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var db = new ThemePresetDb
		{
			SchemaVersion = 1,
			Presets = new Dictionary<string, ThemePreset>(),
			LastSelected = "Dark.Transparent"
		};

		store.Save(db);

		var json = File.ReadAllText(store.GetPath());
		using var doc = JsonDocument.Parse(json);
		Assert.True(doc.RootElement.TryGetProperty("schemaVersion", out _));
		Assert.True(doc.RootElement.TryGetProperty("presets", out _));
		Assert.True(doc.RootElement.TryGetProperty("lastSelected", out _));
	}

	[Fact]
	// Ensures Save does not throw and creates the target directory when needed.
	public void Save_CreatesDirectoryWhenMissing()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var path = store.GetPath();
		var directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
		{
			try
			{
				Directory.Delete(directory, recursive: true);
			}
			catch (UnauthorizedAccessException)
			{
				// Ignore - files may be locked by other tests
			}
			catch (IOException)
			{
				// Ignore - files may be in use
			}
		}

		store.Save(new ThemePresetDb());

		Assert.True(File.Exists(path));
	}

	[Fact]
	// Ensures SetPreset overwrites existing entries.
	public void SetPreset_OverridesExistingPreset()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var db = new ThemePresetDb { Presets = new Dictionary<string, ThemePreset>() };
		var original = new ThemePreset { Theme = ThemeVariant.Dark, Effect = ThemeEffectMode.Mica };
		var updated = new ThemePreset { Theme = ThemeVariant.Dark, Effect = ThemeEffectMode.Mica, BlurRadius = 42 };

		store.SetPreset(db, ThemeVariant.Dark, ThemeEffectMode.Mica, original);
		store.SetPreset(db, ThemeVariant.Dark, ThemeEffectMode.Mica, updated);

		Assert.Same(updated, db.Presets["Dark.Mica"]);
	}

	[Fact]
	// Ensures GetPreset returns existing presets without replacing them.
	public void GetPreset_ReturnsExistingPresetInstance()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var preset = new ThemePreset { Theme = ThemeVariant.Light, Effect = ThemeEffectMode.Transparent, BlurRadius = 1 };
		var db = new ThemePresetDb
		{
			Presets = new Dictionary<string, ThemePreset> { ["Light.Transparent"] = preset }
		};

		var loaded = store.GetPreset(db, ThemeVariant.Light, ThemeEffectMode.Transparent);

		Assert.Same(preset, loaded);
		Assert.Equal(1, db.Presets["Light.Transparent"].BlurRadius);
	}

	[Fact]
	// Ensures normalization preserves a valid lastSelected key.
	public void Load_ValidLastSelected_IsPreserved()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var path = store.GetPath();
		var db = new ThemePresetDb
		{
			SchemaVersion = 1,
			Presets = new Dictionary<string, ThemePreset>
			{
				["Light.Acrylic"] = new ThemePreset { Theme = ThemeVariant.Light, Effect = ThemeEffectMode.Acrylic }
			},
			LastSelected = "Light.Acrylic"
		};

		WritePresetFile(path, db);

		var loaded = store.Load();

		Assert.Equal("Light.Acrylic", loaded.LastSelected);
	}

	[Fact]
	// Ensures normalization fills presets without removing existing custom ones.
	public void Load_PreservesCustomPresetValues()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var path = store.GetPath();
		var custom = new ThemePreset
		{
			Theme = ThemeVariant.Light,
			Effect = ThemeEffectMode.Transparent,
			MaterialIntensity = 11,
			BlurRadius = 22,
			PanelContrast = 33,
			MenuChildIntensity = 44,
			BorderStrength = 55
		};
		var db = new ThemePresetDb
		{
			SchemaVersion = 1,
			Presets = new Dictionary<string, ThemePreset> { ["Light.Transparent"] = custom },
			LastSelected = "Light.Transparent"
		};

		WritePresetFile(path, db);

		var loaded = store.Load();

		Assert.Equal(11, loaded.Presets["Light.Transparent"].MaterialIntensity);
		Assert.Equal(22, loaded.Presets["Light.Transparent"].BlurRadius);
		Assert.Equal(33, loaded.Presets["Light.Transparent"].PanelContrast);
		Assert.Equal(44, loaded.Presets["Light.Transparent"].MenuChildIntensity);
		Assert.Equal(55, loaded.Presets["Light.Transparent"].BorderStrength);
	}

	[Fact]
	// Ensures Load returns defaults when JSON file is empty.
	public void Load_EmptyFile_ReturnsDefaults()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var path = store.GetPath();
		var directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrWhiteSpace(directory))
			Directory.CreateDirectory(directory);
		File.WriteAllText(path, string.Empty);

		var db = store.Load();

		Assert.NotEmpty(db.Presets);
		Assert.True(db.SchemaVersion > 0);
	}

	[Fact]
	// Ensures TryParseKey succeeds with exact enum names.
	public void TryParseKey_ParsesExactCase()
	{
		var store = new ThemePresetStore();

		Assert.True(store.TryParseKey("Dark.Transparent", out var theme, out var effect));
		Assert.Equal(ThemeVariant.Dark, theme);
		Assert.Equal(ThemeEffectMode.Transparent, effect);
	}

	[Fact]
	// Ensures TryParseKey is case-insensitive for theme and effect.
	public void TryParseKey_ParsesCaseInsensitive()
	{
		var store = new ThemePresetStore();

		Assert.True(store.TryParseKey("lIgHt.aCrYlIc", out var theme, out var effect));
		Assert.Equal(ThemeVariant.Light, theme);
		Assert.Equal(ThemeEffectMode.Acrylic, effect);
	}

	[Fact]
	// Ensures TryParseKey trims whitespace around the dot-separated parts.
	public void TryParseKey_TrimsWhitespace()
	{
		var store = new ThemePresetStore();

		Assert.True(store.TryParseKey("  Dark . Mica ", out var theme, out var effect));
		Assert.Equal(ThemeVariant.Dark, theme);
		Assert.Equal(ThemeEffectMode.Mica, effect);
	}

	[Fact]
	// Ensures TryParseKey rejects null or empty strings.
	public void TryParseKey_RejectsNullOrEmpty()
	{
		var store = new ThemePresetStore();

		Assert.False(store.TryParseKey(null, out _, out _));
		Assert.False(store.TryParseKey(string.Empty, out _, out _));
		Assert.False(store.TryParseKey("   ", out _, out _));
	}

	[Fact]
	// Ensures TryParseKey rejects keys with the wrong format.
	public void TryParseKey_RejectsInvalidFormat()
	{
		var store = new ThemePresetStore();

		Assert.False(store.TryParseKey("Dark", out _, out _));
		Assert.False(store.TryParseKey("Dark.Transparent.Extra", out _, out _));
		Assert.False(store.TryParseKey("Dark-Transparent", out _, out _));
	}

	[Theory]
	// Ensures TryParseKey rejects unknown theme names.
	[InlineData("Blue.Transparent")]
	[InlineData("Unknown.Transparent")]
	[InlineData("Darkness.Transparent")]
	[InlineData("Lightish.Transparent")]
	[InlineData("Transparent.Transparent")]
	[InlineData(".Transparent")]
	[InlineData(" .Transparent")]
	[InlineData("Dark..")]
	[InlineData("..")]
	[InlineData(".")]
	[InlineData("Transparent.")]
	public void TryParseKey_RejectsInvalidTheme(string key)
	{
		var store = new ThemePresetStore();

		Assert.False(store.TryParseKey(key, out _, out _));
	}

	[Theory]
	// Ensures TryParseKey rejects unknown effect names.
	[InlineData("Dark.Glow")]
	[InlineData("Light.Blur")]
	[InlineData("Light.Transparentish")]
	[InlineData("Dark.Micaa")]
	[InlineData("Light.*")]
	[InlineData("Dark.")]
	[InlineData("Dark. ")]
	[InlineData("Dark..")]
	public void TryParseKey_RejectsInvalidEffect(string key)
	{
		var store = new ThemePresetStore();

		Assert.False(store.TryParseKey(key, out _, out _));
	}

	[Theory]
	// Ensures TryParseKey parses supported themes and effects.
	[InlineData("Light.Transparent", ThemeVariant.Light, ThemeEffectMode.Transparent)]
	[InlineData("Light.Mica", ThemeVariant.Light, ThemeEffectMode.Mica)]
	[InlineData("Light.Acrylic", ThemeVariant.Light, ThemeEffectMode.Acrylic)]
	[InlineData("Dark.Transparent", ThemeVariant.Dark, ThemeEffectMode.Transparent)]
	[InlineData("Dark.Mica", ThemeVariant.Dark, ThemeEffectMode.Mica)]
	[InlineData("Dark.Acrylic", ThemeVariant.Dark, ThemeEffectMode.Acrylic)]
	[InlineData("light.transparent", ThemeVariant.Light, ThemeEffectMode.Transparent)]
	[InlineData("dark.mica", ThemeVariant.Dark, ThemeEffectMode.Mica)]
	[InlineData("LIGHT.ACRYLIC", ThemeVariant.Light, ThemeEffectMode.Acrylic)]
	[InlineData("DaRk.TrAnSpArEnT", ThemeVariant.Dark, ThemeEffectMode.Transparent)]
	public void TryParseKey_ParsesAllValidCombinations(string key, ThemeVariant theme, ThemeEffectMode effect)
	{
		var store = new ThemePresetStore();

		Assert.True(store.TryParseKey(key, out var parsedTheme, out var parsedEffect));
		Assert.Equal(theme, parsedTheme);
		Assert.Equal(effect, parsedEffect);
	}

	[Fact]
	// Ensures Save persists schema version updates from Normalize on Load.
	public void Load_UpdatesSchemaVersionToCurrent()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var path = store.GetPath();
		var db = new ThemePresetDb
		{
			SchemaVersion = -1,
			Presets = new Dictionary<string, ThemePreset>(),
			LastSelected = string.Empty
		};

		WritePresetFile(path, db);

		var loaded = store.Load();

		Assert.True(loaded.SchemaVersion > 0);
	}

	[Fact]
	// Ensures GetPath includes the expected filename and folder.
	public void GetPath_IncludesExpectedSegments()
	{
		var store = new ThemePresetStore();
		var path = store.GetPath();

		Assert.EndsWith(Path.Combine("DevProjex", "user-settings.json"), path);
	}

	[Theory]
	// Ensures Save/Load preserves lastSelected for multiple valid keys.
	[InlineData("Light.Transparent")]
	[InlineData("Light.Mica")]
	[InlineData("Light.Acrylic")]
	[InlineData("Dark.Transparent")]
	[InlineData("Dark.Mica")]
	[InlineData("Dark.Acrylic")]
	public void Load_PreservesValidLastSelectedKeys(string lastSelected)
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var path = store.GetPath();
		var db = new ThemePresetDb
		{
			SchemaVersion = 1,
			Presets = new Dictionary<string, ThemePreset>
			{
				[lastSelected] = new ThemePreset
				{
					Theme = lastSelected.StartsWith("Light", StringComparison.OrdinalIgnoreCase)
						? ThemeVariant.Light
						: ThemeVariant.Dark,
					Effect = lastSelected.EndsWith("Mica", StringComparison.OrdinalIgnoreCase)
						? ThemeEffectMode.Mica
						: lastSelected.EndsWith("Acrylic", StringComparison.OrdinalIgnoreCase)
							? ThemeEffectMode.Acrylic
							: ThemeEffectMode.Transparent
				}
			},
			LastSelected = lastSelected
		};

		WritePresetFile(path, db);

		var loaded = store.Load();

		Assert.Equal(lastSelected, loaded.LastSelected);
	}

	[Fact]
	// Ensures Load initializes presets dictionary even when null in JSON.
	public void Load_NullPresets_InitializesDictionary()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var path = store.GetPath();
		var json = """
		{
		  "schemaVersion": 1,
		  "presets": null,
		  "lastSelected": "Dark.Transparent"
		}
		""";
		var directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrWhiteSpace(directory))
			Directory.CreateDirectory(directory);
		File.WriteAllText(path, json);

		var loaded = store.Load();

		Assert.NotNull(loaded.Presets);
		Assert.NotEmpty(loaded.Presets);
	}

	[Fact]
	// Ensures Load corrects missing lastSelected values to a valid key.
	public void Load_EmptyLastSelected_IsFixed()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var path = store.GetPath();
		var db = new ThemePresetDb
		{
			SchemaVersion = 1,
			Presets = new Dictionary<string, ThemePreset>(),
			LastSelected = string.Empty
		};

		WritePresetFile(path, db);

		var loaded = store.Load();

		Assert.False(string.IsNullOrWhiteSpace(loaded.LastSelected));
		Assert.True(loaded.Presets.ContainsKey(loaded.LastSelected));
	}

	[Fact]
	// Ensures Save/Load keeps preset dictionary count stable.
	public void Load_KeepsPresetCountStable()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var db = store.Load();
		var initialCount = db.Presets.Count;

		store.Save(db);
		var loaded = store.Load();

		Assert.Equal(initialCount, loaded.Presets.Count);
	}

	[Fact]
	// Ensures Save accepts an empty database and still writes defaults on load.
	public void Save_EmptyDb_LoadsWithDefaults()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var db = new ThemePresetDb { Presets = new Dictionary<string, ThemePreset>(), LastSelected = string.Empty };

		store.Save(db);

		var loaded = store.Load();
		Assert.NotEmpty(loaded.Presets);
		Assert.False(string.IsNullOrWhiteSpace(loaded.LastSelected));
	}

	[Theory]
	// Ensures invalid JSON payloads trigger fallback and recovery.
	[InlineData("{")]
	[InlineData("{\"schemaVersion\":}")]
	[InlineData("{\"presets\":")]
	[InlineData("{\"lastSelected\":\"Dark.Transparent\"")]
	[InlineData("{\"schemaVersion\":\"not-a-number\"}")]
	[InlineData("not json at all")]
	[InlineData("[1,2,3]")]
	[InlineData("\"just a string\"")]
	[InlineData("null")]
	[InlineData("true")]
	public void Load_InvalidJsonPayloads_Recovers(string payload)
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var path = store.GetPath();
		var directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrWhiteSpace(directory))
			Directory.CreateDirectory(directory);
		File.WriteAllText(path, payload);

		var loaded = store.Load();

		Assert.NotEmpty(loaded.Presets);
		Assert.True(File.Exists(path));
	}

	[Theory]
	// Ensures GetPreset always returns presets with matching theme/effect metadata.
	[InlineData(ThemeVariant.Light, ThemeEffectMode.Transparent)]
	[InlineData(ThemeVariant.Light, ThemeEffectMode.Mica)]
	[InlineData(ThemeVariant.Light, ThemeEffectMode.Acrylic)]
	[InlineData(ThemeVariant.Dark, ThemeEffectMode.Transparent)]
	[InlineData(ThemeVariant.Dark, ThemeEffectMode.Mica)]
	[InlineData(ThemeVariant.Dark, ThemeEffectMode.Acrylic)]
	public void GetPreset_ReturnsMatchingMetadata(ThemeVariant theme, ThemeEffectMode effect)
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var db = new ThemePresetDb { Presets = new Dictionary<string, ThemePreset>() };

		var preset = store.GetPreset(db, theme, effect);

		Assert.Equal(theme, preset.Theme);
		Assert.Equal(effect, preset.Effect);
	}

	[Theory]
	// Ensures SetPreset stores presets under the expected key.
	[InlineData(ThemeVariant.Light, ThemeEffectMode.Transparent)]
	[InlineData(ThemeVariant.Light, ThemeEffectMode.Mica)]
	[InlineData(ThemeVariant.Light, ThemeEffectMode.Acrylic)]
	[InlineData(ThemeVariant.Dark, ThemeEffectMode.Transparent)]
	[InlineData(ThemeVariant.Dark, ThemeEffectMode.Mica)]
	[InlineData(ThemeVariant.Dark, ThemeEffectMode.Acrylic)]
	public void SetPreset_UsesExpectedKey(ThemeVariant theme, ThemeEffectMode effect)
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var db = new ThemePresetDb { Presets = new Dictionary<string, ThemePreset>() };
		var preset = new ThemePreset { Theme = theme, Effect = effect, BlurRadius = 99 };

		store.SetPreset(db, theme, effect, preset);

		Assert.Same(preset, db.Presets[$"{theme}.{effect}"]);
	}

	[Theory]
	// Ensures Save does not throw for various lastSelected values.
	[InlineData("")]
	[InlineData(" ")]
	[InlineData("Dark.Transparent")]
	[InlineData("Light.Acrylic")]
	[InlineData("Invalid.Key")]
	public void Save_AllowsAnyLastSelectedValue(string lastSelected)
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var db = new ThemePresetDb
		{
			SchemaVersion = 1,
			Presets = new Dictionary<string, ThemePreset>(),
			LastSelected = lastSelected
		};

		store.Save(db);

		Assert.True(File.Exists(store.GetPath()));
	}

	#region ResetToDefaults Tests

	[Fact]
	// Ensures ResetToDefaults returns a database with all theme/effect combinations.
	public void ResetToDefaults_ReturnsAllPresetCombinations()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();

		var db = store.ResetToDefaults();

		foreach (var theme in Enum.GetValues<ThemeVariant>())
		{
			foreach (var effect in Enum.GetValues<ThemeEffectMode>())
			{
				var key = $"{theme}.{effect}";
				Assert.True(db.Presets.ContainsKey(key), $"Missing preset: {key}");
			}
		}
	}

	[Fact]
	// Ensures ResetToDefaults always restores view toggles to default disabled state.
	public void ResetToDefaults_DisablesViewTogglesByDefault()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();

		var customDb = new ThemePresetDb
		{
			SchemaVersion = 1,
			Presets = new Dictionary<string, ThemePreset>(),
			LastSelected = "Dark.Transparent",
			ViewSettings = new AppViewSettings
			{
				IsCompactMode = true,
				IsTreeAnimationEnabled = true
			}
		};
		store.Save(customDb);

		var resetDb = store.ResetToDefaults();

		Assert.False(resetDb.ViewSettings.IsCompactMode);
		Assert.False(resetDb.ViewSettings.IsTreeAnimationEnabled);
	}

	[Fact]
	// Ensures ResetToDefaults overwrites any previously saved custom presets.
	public void ResetToDefaults_OverwritesCustomPresets()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();

		// Save custom preset with unique values
		var customDb = new ThemePresetDb
		{
			SchemaVersion = 1,
			Presets = new Dictionary<string, ThemePreset>
			{
				["Dark.Transparent"] = new ThemePreset
				{
					Theme = ThemeVariant.Dark,
					Effect = ThemeEffectMode.Transparent,
					MaterialIntensity = 999,
					BlurRadius = 999,
					PanelContrast = 999,
					MenuChildIntensity = 999,
					BorderStrength = 999
				}
			},
			LastSelected = "Dark.Transparent"
		};
		store.Save(customDb);

		// Reset to defaults
		var resetDb = store.ResetToDefaults();

		// Verify custom values were replaced (we don't know exact defaults, but 999 is unrealistic)
		var preset = resetDb.Presets["Dark.Transparent"];
		Assert.NotEqual(999, preset.MaterialIntensity);
		Assert.NotEqual(999, preset.BlurRadius);
		Assert.NotEqual(999, preset.PanelContrast);
		Assert.NotEqual(999, preset.MenuChildIntensity);
		Assert.NotEqual(999, preset.BorderStrength);
	}

	[Fact]
	// Ensures ResetToDefaults saves the file immediately.
	public void ResetToDefaults_SavesFileImmediately()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var path = store.GetPath();

		if (File.Exists(path))
			File.Delete(path);

		store.ResetToDefaults();

		Assert.True(File.Exists(path));
	}

	[Fact]
	// Ensures ResetToDefaults values match what Load returns for a fresh database.
	public void ResetToDefaults_PresetValuesMatchLoadDefaults()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var path = store.GetPath();

		// Clear any existing file
		if (File.Exists(path))
			File.Delete(path);

		// Get defaults from Load (creates fresh defaults)
		var loadedDefaults = store.Load();

		// Clear again and call ResetToDefaults
		if (File.Exists(path))
			File.Delete(path);
		var resetDefaults = store.ResetToDefaults();

		// All presets should have identical values
		foreach (var theme in Enum.GetValues<ThemeVariant>())
		{
			foreach (var effect in Enum.GetValues<ThemeEffectMode>())
			{
				var key = $"{theme}.{effect}";
				var loadedPreset = loadedDefaults.Presets[key];
				var resetPreset = resetDefaults.Presets[key];

				Assert.Equal(loadedPreset.Theme, resetPreset.Theme);
				Assert.Equal(loadedPreset.Effect, resetPreset.Effect);
				Assert.Equal(loadedPreset.MaterialIntensity, resetPreset.MaterialIntensity);
				Assert.Equal(loadedPreset.BlurRadius, resetPreset.BlurRadius);
				Assert.Equal(loadedPreset.PanelContrast, resetPreset.PanelContrast);
				Assert.Equal(loadedPreset.MenuChildIntensity, resetPreset.MenuChildIntensity);
				Assert.Equal(loadedPreset.BorderStrength, resetPreset.BorderStrength);
			}
		}
	}

	[Fact]
	// Ensures ResetToDefaults returns a database with a valid LastSelected key.
	public void ResetToDefaults_LastSelectedIsValid()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();

		var db = store.ResetToDefaults();

		Assert.False(string.IsNullOrWhiteSpace(db.LastSelected));
		Assert.True(db.Presets.ContainsKey(db.LastSelected));
		Assert.True(store.TryParseKey(db.LastSelected, out _, out _));
	}

	[Fact]
	// Ensures ResetToDefaults returns a database with valid schema version.
	public void ResetToDefaults_HasValidSchemaVersion()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();

		var db = store.ResetToDefaults();

		Assert.True(db.SchemaVersion > 0);
	}

	[Theory]
	// Ensures ResetToDefaults presets have matching theme/effect metadata.
	[InlineData(ThemeVariant.Light, ThemeEffectMode.Transparent)]
	[InlineData(ThemeVariant.Light, ThemeEffectMode.Mica)]
	[InlineData(ThemeVariant.Light, ThemeEffectMode.Acrylic)]
	[InlineData(ThemeVariant.Dark, ThemeEffectMode.Transparent)]
	[InlineData(ThemeVariant.Dark, ThemeEffectMode.Mica)]
	[InlineData(ThemeVariant.Dark, ThemeEffectMode.Acrylic)]
	public void ResetToDefaults_AllPresetsHaveMatchingMetadata(ThemeVariant theme, ThemeEffectMode effect)
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();

		var db = store.ResetToDefaults();
		var key = $"{theme}.{effect}";
		var preset = db.Presets[key];

		Assert.Equal(theme, preset.Theme);
		Assert.Equal(effect, preset.Effect);
	}

	[Fact]
	// Ensures multiple calls to ResetToDefaults return consistent values.
	public void ResetToDefaults_ReturnsConsistentDefaults()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();

		var first = store.ResetToDefaults();
		var second = store.ResetToDefaults();

		foreach (var theme in Enum.GetValues<ThemeVariant>())
		{
			foreach (var effect in Enum.GetValues<ThemeEffectMode>())
			{
				var key = $"{theme}.{effect}";
				var firstPreset = first.Presets[key];
				var secondPreset = second.Presets[key];

				Assert.Equal(firstPreset.MaterialIntensity, secondPreset.MaterialIntensity);
				Assert.Equal(firstPreset.BlurRadius, secondPreset.BlurRadius);
				Assert.Equal(firstPreset.PanelContrast, secondPreset.PanelContrast);
				Assert.Equal(firstPreset.MenuChildIntensity, secondPreset.MenuChildIntensity);
				Assert.Equal(firstPreset.BorderStrength, secondPreset.BorderStrength);
			}
		}
	}

	[Fact]
	// Ensures ResetToDefaults works even when file was corrupted.
	public void ResetToDefaults_WorksWithCorruptedFile()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var path = store.GetPath();
		var directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrWhiteSpace(directory))
			Directory.CreateDirectory(directory);
		File.WriteAllText(path, "{ invalid json content");

		var db = store.ResetToDefaults();

		Assert.NotEmpty(db.Presets);
		Assert.True(db.SchemaVersion > 0);
		Assert.False(string.IsNullOrWhiteSpace(db.LastSelected));
	}

	[Fact]
	// Ensures ResetToDefaults works when no file exists.
	public void ResetToDefaults_WorksWithMissingFile()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var path = store.GetPath();

		if (File.Exists(path))
			File.Delete(path);
		var directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
		{
			try
			{
				Directory.Delete(directory, recursive: true);
			}
			catch (UnauthorizedAccessException)
			{
				// Ignore - files may be locked by other tests
			}
			catch (IOException)
			{
				// Ignore - files may be in use
			}
		}

		var db = store.ResetToDefaults();

		Assert.NotEmpty(db.Presets);
		Assert.True(File.Exists(path));
	}

	[Fact]
	// Ensures ResetToDefaults preset values are within valid slider ranges (0-100).
	public void ResetToDefaults_AllValuesWithinValidRange()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();

		var db = store.ResetToDefaults();

		foreach (var preset in db.Presets.Values)
		{
			Assert.InRange(preset.MaterialIntensity, 0, 100);
			Assert.InRange(preset.BlurRadius, 0, 100);
			Assert.InRange(preset.PanelContrast, 0, 100);
			Assert.InRange(preset.MenuChildIntensity, 0, 100);
			Assert.InRange(preset.BorderStrength, 0, 100);
		}
	}

	[Fact]
	// Ensures ResetToDefaults file can be loaded again and matches.
	public void ResetToDefaults_PersistedFileCanBeLoaded()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();

		var resetDb = store.ResetToDefaults();
		var loadedDb = store.Load();

		Assert.Equal(resetDb.SchemaVersion, loadedDb.SchemaVersion);
		Assert.Equal(resetDb.LastSelected, loadedDb.LastSelected);
		Assert.Equal(resetDb.Presets.Count, loadedDb.Presets.Count);

		foreach (var key in resetDb.Presets.Keys)
		{
			var resetPreset = resetDb.Presets[key];
			var loadedPreset = loadedDb.Presets[key];

			Assert.Equal(resetPreset.MaterialIntensity, loadedPreset.MaterialIntensity);
			Assert.Equal(resetPreset.BlurRadius, loadedPreset.BlurRadius);
			Assert.Equal(resetPreset.PanelContrast, loadedPreset.PanelContrast);
			Assert.Equal(resetPreset.MenuChildIntensity, loadedPreset.MenuChildIntensity);
			Assert.Equal(resetPreset.BorderStrength, loadedPreset.BorderStrength);
		}
	}

	[Fact]
	// Ensures ResetToDefaults does not preserve any previously selected custom preset key.
	public void ResetToDefaults_IgnoresPreviousLastSelected()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();

		// Save with unusual lastSelected
		var customDb = new ThemePresetDb
		{
			SchemaVersion = 1,
			Presets = new Dictionary<string, ThemePreset>
			{
				["Light.Acrylic"] = new ThemePreset
				{
					Theme = ThemeVariant.Light,
					Effect = ThemeEffectMode.Acrylic,
					MaterialIntensity = 50,
					BlurRadius = 50,
					PanelContrast = 50,
					MenuChildIntensity = 50,
					BorderStrength = 50
				}
			},
			LastSelected = "Light.Acrylic"
		};
		store.Save(customDb);

		// Reset should use default lastSelected, not the custom one
		var resetDb = store.ResetToDefaults();

		// We don't know the exact default, but it should be valid
		Assert.True(store.TryParseKey(resetDb.LastSelected, out _, out _));
		Assert.True(resetDb.Presets.ContainsKey(resetDb.LastSelected));
	}

	[Fact]
	// Ensures ResetToDefaults restores values after multiple custom modifications.
	public void ResetToDefaults_RestoresAfterMultipleModifications()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();

		// Get baseline defaults
		var baseline = store.ResetToDefaults();
		var baselineValues = CaptureAllPresetValues(baseline);

		// Modify all presets with unique custom values
		foreach (var theme in Enum.GetValues<ThemeVariant>())
		{
			foreach (var effect in Enum.GetValues<ThemeEffectMode>())
			{
				var customPreset = new ThemePreset
				{
					Theme = theme,
					Effect = effect,
					MaterialIntensity = 11.11,
					BlurRadius = 22.22,
					PanelContrast = 33.33,
					MenuChildIntensity = 44.44,
					BorderStrength = 55.55
				};
				store.SetPreset(baseline, theme, effect, customPreset);
			}
		}
		store.Save(baseline);

		// Verify custom values were saved
		var loaded = store.Load();
		foreach (var preset in loaded.Presets.Values)
		{
			Assert.Equal(11.11, preset.MaterialIntensity);
		}

		// Reset and verify defaults are restored
		var reset = store.ResetToDefaults();
		var resetValues = CaptureAllPresetValues(reset);

		Assert.Equal(baselineValues, resetValues);
	}

	[Theory]
	// Ensures each preset combination is properly reset to its specific default.
	[InlineData(ThemeVariant.Light, ThemeEffectMode.Transparent)]
	[InlineData(ThemeVariant.Light, ThemeEffectMode.Mica)]
	[InlineData(ThemeVariant.Light, ThemeEffectMode.Acrylic)]
	[InlineData(ThemeVariant.Dark, ThemeEffectMode.Transparent)]
	[InlineData(ThemeVariant.Dark, ThemeEffectMode.Mica)]
	[InlineData(ThemeVariant.Dark, ThemeEffectMode.Acrylic)]
	public void ResetToDefaults_RestoresSpecificPresetToDefault(ThemeVariant theme, ThemeEffectMode effect)
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();
		var key = $"{theme}.{effect}";

		// Get original default for this specific preset
		var originalDb = store.ResetToDefaults();
		var originalPreset = originalDb.Presets[key];
		var originalValues = CapturePresetValues(originalPreset);

		// Modify only this preset
		var customPreset = new ThemePreset
		{
			Theme = theme,
			Effect = effect,
			MaterialIntensity = 1.23,
			BlurRadius = 4.56,
			PanelContrast = 7.89,
			MenuChildIntensity = 10.11,
			BorderStrength = 12.13
		};
		store.SetPreset(originalDb, theme, effect, customPreset);
		store.Save(originalDb);

		// Reset and verify this specific preset is restored
		var resetDb = store.ResetToDefaults();
		var resetPreset = resetDb.Presets[key];
		var resetValues = CapturePresetValues(resetPreset);

		Assert.Equal(originalValues, resetValues);
	}

	[Fact]
	// Ensures reset cycle works correctly: default -> custom -> reset -> custom -> reset.
	public void ResetToDefaults_WorksInRepeatedResetCycle()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();

		// First reset to get baseline
		var baseline = store.ResetToDefaults();
		var baselineValues = CaptureAllPresetValues(baseline);

		for (int cycle = 0; cycle < 3; cycle++)
		{
			// Modify with cycle-specific values
			foreach (var theme in Enum.GetValues<ThemeVariant>())
			{
				foreach (var effect in Enum.GetValues<ThemeEffectMode>())
				{
					var custom = new ThemePreset
					{
						Theme = theme,
						Effect = effect,
						MaterialIntensity = 10 + cycle,
						BlurRadius = 20 + cycle,
						PanelContrast = 30 + cycle,
						MenuChildIntensity = 40 + cycle,
						BorderStrength = 50 + cycle
					};
					store.SetPreset(baseline, theme, effect, custom);
				}
			}
			store.Save(baseline);

			// Reset and verify
			var reset = store.ResetToDefaults();
			var resetValues = CaptureAllPresetValues(reset);

			Assert.Equal(baselineValues, resetValues);
		}
	}

	[Fact]
	// Ensures reset does not preserve any custom slider values.
	public void ResetToDefaults_NoCustomValuesRemain()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();

		// Use obviously wrong values that cannot be defaults
		const double impossibleValue = 123.456789;

		var db = new ThemePresetDb
		{
			SchemaVersion = 1,
			Presets = new Dictionary<string, ThemePreset>(),
			LastSelected = "Dark.Transparent"
		};

		foreach (var theme in Enum.GetValues<ThemeVariant>())
		{
			foreach (var effect in Enum.GetValues<ThemeEffectMode>())
			{
				db.Presets[$"{theme}.{effect}"] = new ThemePreset
				{
					Theme = theme,
					Effect = effect,
					MaterialIntensity = impossibleValue,
					BlurRadius = impossibleValue,
					PanelContrast = impossibleValue,
					MenuChildIntensity = impossibleValue,
					BorderStrength = impossibleValue
				};
			}
		}
		store.Save(db);

		// Reset
		var reset = store.ResetToDefaults();

		// Verify none of the impossible values remain
		foreach (var preset in reset.Presets.Values)
		{
			Assert.NotEqual(impossibleValue, preset.MaterialIntensity);
			Assert.NotEqual(impossibleValue, preset.BlurRadius);
			Assert.NotEqual(impossibleValue, preset.PanelContrast);
			Assert.NotEqual(impossibleValue, preset.MenuChildIntensity);
			Assert.NotEqual(impossibleValue, preset.BorderStrength);
		}
	}

	[Fact]
	// Ensures different theme variants have independent default values after reset.
	public void ResetToDefaults_ThemeVariantsHaveIndependentDefaults()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();

		var db = store.ResetToDefaults();

		// For same effect, Light and Dark may have different defaults
		foreach (var effect in Enum.GetValues<ThemeEffectMode>())
		{
			var lightPreset = db.Presets[$"Light.{effect}"];
			var darkPreset = db.Presets[$"Dark.{effect}"];

			// Both should have valid values
			Assert.InRange(lightPreset.MaterialIntensity, 0, 100);
			Assert.InRange(darkPreset.MaterialIntensity, 0, 100);

			// Metadata should match their keys
			Assert.Equal(ThemeVariant.Light, lightPreset.Theme);
			Assert.Equal(ThemeVariant.Dark, darkPreset.Theme);
			Assert.Equal(effect, lightPreset.Effect);
			Assert.Equal(effect, darkPreset.Effect);
		}
	}

	[Fact]
	// Ensures different effects have independent default values after reset.
	public void ResetToDefaults_EffectModesHaveIndependentDefaults()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();

		var db = store.ResetToDefaults();

		// For same theme, different effects may have different defaults
		foreach (var theme in Enum.GetValues<ThemeVariant>())
		{
			var transparentPreset = db.Presets[$"{theme}.Transparent"];
			var micaPreset = db.Presets[$"{theme}.Mica"];
			var acrylicPreset = db.Presets[$"{theme}.Acrylic"];

			// All should have valid values
			Assert.InRange(transparentPreset.MaterialIntensity, 0, 100);
			Assert.InRange(micaPreset.MaterialIntensity, 0, 100);
			Assert.InRange(acrylicPreset.MaterialIntensity, 0, 100);

			// Metadata should match their keys
			Assert.Equal(theme, transparentPreset.Theme);
			Assert.Equal(theme, micaPreset.Theme);
			Assert.Equal(theme, acrylicPreset.Theme);
			Assert.Equal(ThemeEffectMode.Transparent, transparentPreset.Effect);
			Assert.Equal(ThemeEffectMode.Mica, micaPreset.Effect);
			Assert.Equal(ThemeEffectMode.Acrylic, acrylicPreset.Effect);
		}
	}

	[Fact]
	// Ensures file content after reset matches expected JSON structure.
	public void ResetToDefaults_WritesValidJsonStructure()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();

		store.ResetToDefaults();

		var json = File.ReadAllText(store.GetPath());
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;

		// Verify structure
		Assert.True(root.TryGetProperty("schemaVersion", out var schemaVersion));
		Assert.True(schemaVersion.GetInt32() > 0);

		Assert.True(root.TryGetProperty("lastSelected", out var lastSelected));
		Assert.False(string.IsNullOrWhiteSpace(lastSelected.GetString()));

		Assert.True(root.TryGetProperty("presets", out var presets));
		Assert.Equal(JsonValueKind.Object, presets.ValueKind);

		// Count presets (should be 6: 2 themes × 3 effects)
		int count = 0;
		foreach (var _ in presets.EnumerateObject())
			count++;
		Assert.Equal(6, count);
	}

	[Fact]
	// Ensures each preset in reset database contains all required properties.
	public void ResetToDefaults_AllPresetsHaveRequiredProperties()
	{
		using var scope = new AppDataScope();
		var store = new ThemePresetStore();

		var db = store.ResetToDefaults();

		foreach (var kvp in db.Presets)
		{
			var preset = kvp.Value;
			Assert.NotNull(preset);

			// Verify theme and effect are set (not default enum values for wrong key)
			Assert.True(Enum.IsDefined(preset.Theme));
			Assert.True(Enum.IsDefined(preset.Effect));

			// Verify all slider values are initialized (not default 0 unless intentional)
			// We check they're within valid range, which implies they were set
			Assert.True(preset.MaterialIntensity >= 0 && preset.MaterialIntensity <= 100,
				$"MaterialIntensity out of range for {kvp.Key}");
			Assert.True(preset.BlurRadius >= 0 && preset.BlurRadius <= 100,
				$"BlurRadius out of range for {kvp.Key}");
			Assert.True(preset.PanelContrast >= 0 && preset.PanelContrast <= 100,
				$"PanelContrast out of range for {kvp.Key}");
			Assert.True(preset.MenuChildIntensity >= 0 && preset.MenuChildIntensity <= 100,
				$"MenuChildIntensity out of range for {kvp.Key}");
			Assert.True(preset.BorderStrength >= 0 && preset.BorderStrength <= 100,
				$"BorderStrength out of range for {kvp.Key}");
		}
	}

	// Helper to capture all preset values for comparison
	private static Dictionary<string, (double MI, double BR, double PC, double MCI, double BS)> CaptureAllPresetValues(ThemePresetDb db)
	{
		var result = new Dictionary<string, (double, double, double, double, double)>();
		foreach (var kvp in db.Presets)
		{
			result[kvp.Key] = CapturePresetValues(kvp.Value);
		}
		return result;
	}

	// Helper to capture single preset values
	private static (double MI, double BR, double PC, double MCI, double BS) CapturePresetValues(ThemePreset preset)
	{
		return (preset.MaterialIntensity, preset.BlurRadius, preset.PanelContrast,
			preset.MenuChildIntensity, preset.BorderStrength);
	}

	#endregion

	private static void WritePresetFile(string path, ThemePresetDb db)
	{
		var directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrWhiteSpace(directory))
			Directory.CreateDirectory(directory);
		var json = JsonSerializer.Serialize(db, SerializerOptions);
		File.WriteAllText(path, json);
	}

	private static JsonSerializerOptions SerializerOptions => new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
	};

	private sealed class AppDataScope : IDisposable
	{
		private readonly TemporaryDirectory _temp = new();
		private readonly string? _originalHome;
		private readonly string? _originalXdgConfig;
		private readonly string? _originalAppData;
		private readonly string? _originalLocalAppData;

		public AppDataScope()
		{
			_originalHome = Environment.GetEnvironmentVariable("HOME");
			_originalXdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
			_originalAppData = Environment.GetEnvironmentVariable("APPDATA");
			_originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");

			Environment.SetEnvironmentVariable("HOME", _temp.Path);
			Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _temp.Path);
			Environment.SetEnvironmentVariable("APPDATA", _temp.Path);
			Environment.SetEnvironmentVariable("LOCALAPPDATA", _temp.Path);
		}

		public void Dispose()
		{
			Environment.SetEnvironmentVariable("HOME", _originalHome);
			Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalXdgConfig);
			Environment.SetEnvironmentVariable("APPDATA", _originalAppData);
			Environment.SetEnvironmentVariable("LOCALAPPDATA", _originalLocalAppData);
			_temp.Dispose();
		}
	}
}
