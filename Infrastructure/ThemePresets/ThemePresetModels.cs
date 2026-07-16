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
}

public sealed class ThemeSettingsDocument
{
    public int SchemaVersion { get; set; }
    public int DefaultsRevision { get; set; }
    public Dictionary<string, ThemePreset> Presets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string SelectedPreset { get; set; } = string.Empty;
}

public sealed record AppViewSettings
{
    public bool IsCompactMode { get; init; }
    public bool IsTreeAnimationEnabled { get; init; }
    public bool IsTerminalCommandPromptDismissed { get; init; }
    public AppLanguage? PreferredLanguage { get; init; }
}
