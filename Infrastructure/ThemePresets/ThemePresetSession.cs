namespace DevProjex.Infrastructure.ThemePresets;

public sealed class ThemePresetSession
{
    private readonly ThemeSettingsStore _store;
    private readonly HashSet<string> _changedPresetKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ThemeVariant> _changedEffectThemes = [];
    private bool _selectionModeChanged;

    public ThemePresetSession(
        ThemeSettingsStore store,
        ThemeSettingsDocument database,
        ThemeVariant? systemTheme = null)
    {
        _store = store;
        Database = database;

        CurrentMode = Enum.IsDefined(database.SelectedThemeMode)
            ? database.SelectedThemeMode
            : ThemeSelectionMode.System;
        Database.SelectedThemeMode = CurrentMode;
        CurrentTheme = ThemeSelectionPolicy.ResolveEffectiveTheme(CurrentMode, systemTheme);
        CurrentEffect = GetPreferredEffect(CurrentTheme);
        Database.SelectedPreset = GetSelectionKey(CurrentTheme, CurrentEffect);
    }

    public ThemeSettingsDocument Database { get; }
    public ThemeSelectionMode CurrentMode { get; private set; }
    public ThemeVariant CurrentTheme { get; private set; }
    public ThemeEffectMode CurrentEffect { get; private set; }
    public bool IsDirty { get; private set; }

    public ThemePreset CurrentPreset => _store.GetPreset(Database, CurrentTheme, CurrentEffect);

    public ThemePreset Select(ThemeVariant theme, ThemeEffectMode effect, ThemePreset currentValues)
    {
        return SelectModeAndEffect(
            ThemeSelectionPolicy.GetExplicitMode(theme),
            theme,
            effect,
            currentValues);
    }

    public ThemePreset SelectMode(
        ThemeSelectionMode mode,
        ThemeVariant? systemTheme,
        ThemePreset currentValues)
    {
        var theme = ThemeSelectionPolicy.ResolveEffectiveTheme(mode, systemTheme);
        return SelectModeAndEffect(mode, theme, GetPreferredEffect(theme), currentValues);
    }

    public ThemePreset SelectEffect(ThemeEffectMode effect, ThemePreset currentValues)
        => SelectModeAndEffect(CurrentMode, CurrentTheme, effect, currentValues);

    public ThemePreset SynchronizeSystemTheme(
        ThemeVariant? systemTheme,
        ThemePreset currentValues)
    {
        if (CurrentMode != ThemeSelectionMode.System)
            return CurrentPreset;

        var theme = ThemeSelectionPolicy.ResolveEffectiveTheme(CurrentMode, systemTheme);
        if (theme == CurrentTheme)
            return CurrentPreset;

        // Capture the outgoing palette before resolving the other system-theme preset.
        CaptureCurrentIfChanged(currentValues);
        CurrentTheme = theme;
        CurrentEffect = GetPreferredEffect(theme);
        Database.SelectedPreset = GetSelectionKey(CurrentTheme, CurrentEffect);
        return CurrentPreset;
    }

    public void CaptureCurrent(ThemePreset currentValues)
    {
        StoreCurrentPreset(currentValues, forceChanged: true);
        Database.SelectedPreset = GetSelectionKey(CurrentTheme, CurrentEffect);
        IsDirty = true;
    }

    public void MarkDirty()
    {
        _changedPresetKeys.Add(GetSelectionKey(CurrentTheme, CurrentEffect));
        IsDirty = true;
    }

    public bool Persist(ThemePreset currentValues)
    {
        if (!IsDirty)
            return true;

        CaptureCurrentIfChanged(currentValues);
        if (!_store.TryPersistChanges(
                Database,
                _changedPresetKeys,
                Database.SelectedPreset,
                _selectionModeChanged,
                _changedEffectThemes))
        {
            return false;
        }

        _changedPresetKeys.Clear();
        _changedEffectThemes.Clear();
        _selectionModeChanged = false;
        IsDirty = false;
        return true;
    }

    private void CaptureCurrentIfChanged(ThemePreset currentValues)
    {
        StoreCurrentPreset(currentValues, forceChanged: false);
        Database.SelectedPreset = GetSelectionKey(CurrentTheme, CurrentEffect);
    }

    private void StoreCurrentPreset(ThemePreset currentValues, bool forceChanged)
    {
        var key = GetSelectionKey(CurrentTheme, CurrentEffect);
        var previous = _store.GetPreset(Database, CurrentTheme, CurrentEffect);
        _store.SetPreset(Database, CurrentTheme, CurrentEffect, currentValues);
        var current = Database.Presets[key];
        if (forceChanged || previous != current)
        {
            _changedPresetKeys.Add(key);
            IsDirty = true;
        }
    }

    private ThemePreset SelectModeAndEffect(
        ThemeSelectionMode mode,
        ThemeVariant theme,
        ThemeEffectMode effect,
        ThemePreset currentValues)
    {
        var previousMode = CurrentMode;
        var previousEffect = GetPreferredEffect(theme);
        // Capture before changing the key so rapid preset switches cannot overwrite the source values.
        CaptureCurrentIfChanged(currentValues);
        CurrentMode = mode;
        CurrentTheme = theme;
        CurrentEffect = effect;
        Database.SelectedThemeMode = mode;
        SetPreferredEffect(theme, effect);
        Database.SelectedPreset = GetSelectionKey(theme, effect);
        if (previousMode != mode)
            _selectionModeChanged = true;
        if (previousEffect != effect)
            _changedEffectThemes.Add(theme);
        IsDirty = true;
        return CurrentPreset;
    }

    private ThemeEffectMode GetPreferredEffect(ThemeVariant theme)
    {
        var effect = theme == ThemeVariant.Light
            ? Database.LightThemeEffect
            : Database.DarkThemeEffect;
        return Enum.IsDefined(effect)
            ? effect
            : ThemeSelectionPolicy.GetFactoryEffect(theme);
    }

    private void SetPreferredEffect(ThemeVariant theme, ThemeEffectMode effect)
    {
        if (theme == ThemeVariant.Light)
            Database.LightThemeEffect = effect;
        else
            Database.DarkThemeEffect = effect;
    }

    private static string GetSelectionKey(ThemeVariant theme, ThemeEffectMode effect) => $"{theme}.{effect}";
}
