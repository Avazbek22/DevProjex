using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevProjex.Infrastructure.ThemePresets;

public sealed class ThemePresetStore
{
    private const int CurrentSchemaVersion = 1;
    private const string FolderName = "DevProjex";
    private const string FileName = "theme-presets.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public ThemePresetDb Load()
    {
        var path = GetPath();
        if (!File.Exists(path))
            return CreateDefaultDb();

        try
        {
            var json = File.ReadAllText(path);
            var db = JsonSerializer.Deserialize<ThemePresetDb>(json, SerializerOptions);
            if (db is null)
                return CreateDefaultDb();

            return Normalize(db);
        }
        catch
        {
            var fallback = CreateDefaultDb();
            TrySave(fallback);
            return fallback;
        }
    }

    public void Save(ThemePresetDb db) => TrySave(db);

    public ThemePreset GetPreset(ThemePresetDb db, ThemeVariant theme, ThemeEffectMode effect)
    {
        var key = GetKey(theme, effect);
        if (db.Presets.TryGetValue(key, out var preset) && preset is not null)
            return preset;

        var created = CreateDefaultPreset(theme, effect);
        db.Presets[key] = created;
        return created;
    }

    public void SetPreset(ThemePresetDb db, ThemeVariant theme, ThemeEffectMode effect, ThemePreset preset)
    {
        var key = GetKey(theme, effect);
        db.Presets[key] = preset;
    }

    /// <summary>
    /// Resets all presets to factory defaults and saves the result.
    /// Returns the new database with default values applied.
    /// </summary>
    public ThemePresetDb ResetToDefaults()
    {
        var defaultDb = CreateDefaultDb();
        TrySave(defaultDb);
        return defaultDb;
    }

    public string GetPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(root, FolderName, FileName);
    }

    public bool TryParseKey(string? key, out ThemeVariant theme, out ThemeEffectMode effect)
    {
        theme = ThemeVariant.Dark;
        effect = ThemeEffectMode.Transparent;

        if (string.IsNullOrWhiteSpace(key))
            return false;

        var parts = key.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return false;

        if (!Enum.TryParse(parts[0], true, out ThemeVariant parsedTheme))
            return false;

        if (!Enum.TryParse(parts[1], true, out ThemeEffectMode parsedEffect))
            return false;

        theme = parsedTheme;
        effect = parsedEffect;
        return true;
    }

    private ThemePresetDb Normalize(ThemePresetDb db)
    {
        db.SchemaVersion = CurrentSchemaVersion;
        db.Presets ??= new Dictionary<string, ThemePreset>();

        foreach (var preset in CreateDefaultPresets())
        {
            if (!db.Presets.ContainsKey(preset.Key))
                db.Presets[preset.Key] = preset.Value;
        }

        if (string.IsNullOrWhiteSpace(db.LastSelected) || !db.Presets.ContainsKey(db.LastSelected))
            db.LastSelected = GetKey(ThemeVariant.Dark, ThemeEffectMode.Transparent);

        return db;
    }

    private ThemePresetDb CreateDefaultDb()
    {
        var db = new ThemePresetDb
        {
            SchemaVersion = CurrentSchemaVersion,
            Presets = CreateDefaultPresets(),
            LastSelected = GetKey(ThemeVariant.Dark, ThemeEffectMode.Transparent)
        };

        return db;
    }

    private Dictionary<string, ThemePreset> CreateDefaultPresets()
    {
        return new Dictionary<string, ThemePreset>(StringComparer.OrdinalIgnoreCase)
        {
            [GetKey(ThemeVariant.Light, ThemeEffectMode.Transparent)] = new ThemePreset
            {
                Theme = ThemeVariant.Light,
                Effect = ThemeEffectMode.Transparent,
                MaterialIntensity = 78.43450479233228,
                BlurRadius = 30,
                PanelContrast = 0,
                MenuChildIntensity = 0,
                BorderStrength = 53.19488817891374
            },
            [GetKey(ThemeVariant.Light, ThemeEffectMode.Mica)] = new ThemePreset
            {
                Theme = ThemeVariant.Light,
                Effect = ThemeEffectMode.Mica,
                MaterialIntensity = 100,
                BlurRadius = 30,
                PanelContrast = 0,
                MenuChildIntensity = 0,
                BorderStrength = 57.66773162939298
            },
            [GetKey(ThemeVariant.Light, ThemeEffectMode.Acrylic)] = new ThemePreset
            {
                Theme = ThemeVariant.Light,
                Effect = ThemeEffectMode.Acrylic,
                MaterialIntensity = 75.87859424920129,
                BlurRadius = 30,
                PanelContrast = 0,
                MenuChildIntensity = 0,
                BorderStrength = 100
            },
            [GetKey(ThemeVariant.Dark, ThemeEffectMode.Transparent)] = new ThemePreset
            {
                Theme = ThemeVariant.Dark,
                Effect = ThemeEffectMode.Transparent,
                MaterialIntensity = 60.86261980830672,
                BlurRadius = 29.233226837060705,
                PanelContrast = 51.59744408945688,
                MenuChildIntensity = 0,
                BorderStrength = 31.789137380191697
            },
            [GetKey(ThemeVariant.Dark, ThemeEffectMode.Mica)] = new ThemePreset
            {
                Theme = ThemeVariant.Dark,
                Effect = ThemeEffectMode.Mica,
                MaterialIntensity = 100,
                BlurRadius = 30,
                PanelContrast = 0,
                MenuChildIntensity = 0,
                BorderStrength = 35.94249201277955
            },
            [GetKey(ThemeVariant.Dark, ThemeEffectMode.Acrylic)] = new ThemePreset
            {
                Theme = ThemeVariant.Dark,
                Effect = ThemeEffectMode.Acrylic,
                MaterialIntensity = 73.00319488817892,
                BlurRadius = 30,
                PanelContrast = 0,
                MenuChildIntensity = 0,
                BorderStrength = 26.677316293929714
            }
        };
    }

    private ThemePreset CreateDefaultPreset(ThemeVariant theme, ThemeEffectMode effect)
    {
        var defaults = CreateDefaultPresets();
        return defaults[GetKey(theme, effect)];
    }

    private string GetKey(ThemeVariant theme, ThemeEffectMode effect) => $"{theme}.{effect}";

    private void TrySave(ThemePresetDb db)
    {
        try
        {
            var path = GetPath();
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                return;

            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(db, SerializerOptions);
            var tempPath = Path.Combine(directory, $"{FileName}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(tempPath, json);

            try
            {
                if (File.Exists(path))
                    File.Replace(tempPath, path, null);
                else
                    File.Move(tempPath, path);
            }
            catch
            {
                File.Move(tempPath, path, true);
            }
        }
        catch
        {
            // Ignore persistence errors.
        }
    }
}
