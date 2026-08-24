namespace DevProjex.Infrastructure.ThemePresets;

public enum ThemeEffectMode
{
    Transparent,
    Mica,
    Acrylic,
    Solid
}

public enum ThemeVariant
{
    Light,
    Dark
}

public enum ThemeSelectionMode
{
    System,
    Light,
    Dark
}

public sealed record ThemePreset
{
    public double BackgroundTransparency { get; init; }
    public double PanelContrast { get; init; }
    public double MenuTransparency { get; init; }
    public double BorderVisibility { get; init; }
}

public sealed class UserSettingsDb
{
    public int SchemaVersion { get; set; }
    public AppViewSettings ViewSettings { get; set; } = new();
    public UpdateCheckSettings UpdateCheckSettings { get; set; } = new();
}

public sealed record UpdateCheckSettings
{
    public bool IsAutomaticCheckEnabled { get; init; }
    public DateTimeOffset? LastCheckUtc { get; init; }
    public string LatestKnownVersion { get; init; } = string.Empty;
    public string LastNotifiedVersion { get; init; } = string.Empty;
}

public sealed class ThemeSettingsDocument
{
    public int SchemaVersion { get; set; }
    public int DefaultsRevision { get; set; }
    public Dictionary<string, ThemePreset> Presets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ThemeSelectionMode SelectedThemeMode { get; set; } = ThemeSelectionMode.System;
    public ThemeEffectMode LightThemeEffect { get; set; } = ThemeEffectMode.Solid;
    public ThemeEffectMode DarkThemeEffect { get; set; } = ThemeEffectMode.Acrylic;
    public string SelectedPreset { get; set; } = string.Empty;
}

public sealed record AppViewSettings
{
    public bool IsCompactMode { get; init; }
    public bool IsTreeExpansionAnimationEnabled { get; init; } = true;
    public bool IsStatusMetricsAnimationEnabled { get; init; } = true;
    public bool IsTerminalCommandPromptDismissed { get; init; }
    public AppLanguage? PreferredLanguage { get; init; }
}
