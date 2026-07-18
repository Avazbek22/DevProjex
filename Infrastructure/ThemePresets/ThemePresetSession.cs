namespace DevProjex.Infrastructure.ThemePresets;

public sealed class ThemePresetSession
{
    private readonly ThemeSettingsStore _store;
    private readonly HashSet<string> _changedPresetKeys = new(StringComparer.OrdinalIgnoreCase);

    public ThemePresetSession(ThemeSettingsStore store, ThemeSettingsDocument database)
    {
        _store = store;
        Database = database;

        if (!_store.TryParseKey(database.SelectedPreset, out var theme, out var effect))
        {
            theme = ThemeVariant.Dark;
            effect = ThemeEffectMode.Acrylic;
        }

        CurrentTheme = theme;
        CurrentEffect = effect;
        Database.SelectedPreset = GetSelectionKey(theme, effect);
    }

    public ThemeSettingsDocument Database { get; }
    public ThemeVariant CurrentTheme { get; private set; }
    public ThemeEffectMode CurrentEffect { get; private set; }
    public bool IsDirty { get; private set; }

    public ThemePreset CurrentPreset => _store.GetPreset(Database, CurrentTheme, CurrentEffect);

    public ThemePreset Select(ThemeVariant theme, ThemeEffectMode effect, ThemePreset currentValues)
    {
        // Capture before changing the key so rapid preset switches cannot overwrite the source values.
        CaptureCurrentIfChanged(currentValues);
        CurrentTheme = theme;
        CurrentEffect = effect;
        Database.SelectedPreset = GetSelectionKey(theme, effect);
        IsDirty = true;
        return _store.GetPreset(Database, theme, effect);
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
        if (!_store.TryPersistChanges(Database, _changedPresetKeys, Database.SelectedPreset))
            return false;

        _changedPresetKeys.Clear();
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

    private static string GetSelectionKey(ThemeVariant theme, ThemeEffectMode effect) => $"{theme}.{effect}";
}
