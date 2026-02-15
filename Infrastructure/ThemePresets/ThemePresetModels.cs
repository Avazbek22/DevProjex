using System.Collections.Generic;

namespace DevProjex.Infrastructure.ThemePresets;

public enum ThemeEffectMode
{
    Transparent,
    Mica,
    Acrylic
}

public enum ThemeVariant
{
    Light,
    Dark
}

public sealed record ThemePreset
{
    public ThemeVariant Theme { get; init; }
    public ThemeEffectMode Effect { get; init; }
    public double MaterialIntensity { get; init; }
    public double BlurRadius { get; init; }
    public double PanelContrast { get; init; }
    public double MenuChildIntensity { get; init; }
    public double BorderStrength { get; init; }
}

public sealed class ThemePresetDb
{
    public int SchemaVersion { get; set; }
    public Dictionary<string, ThemePreset> Presets { get; set; } = new();
    public string LastSelected { get; set; } = string.Empty;
    public AppViewSettings ViewSettings { get; set; } = new();
}

public sealed record AppViewSettings
{
    public bool IsCompactMode { get; init; }
    public bool IsTreeAnimationEnabled { get; init; }
}
