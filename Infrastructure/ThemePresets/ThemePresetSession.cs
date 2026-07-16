namespace DevProjex.Infrastructure.ThemePresets;

public sealed class ThemePresetSession
{
    private readonly UserSettingsStore _store;
    private readonly HashSet<string> _changedPresetKeys = new(StringComparer.OrdinalIgnoreCase);

    public ThemePresetSession(UserSettingsStore store, UserSettingsDb database)
    {
        _store = store;
        Database = database;

        if (!_store.TryParseKey(database.LastSelected, out var theme, out var effect))
        {
            theme = ThemeVariant.Dark;
            effect = ThemeEffectMode.Transparent;
        }

        CurrentTheme = theme;
        CurrentEffect = effect;
        Database.LastSelected = GetSelectionKey(theme, effect);
    }

    public UserSettingsDb Database { get; }
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
        Database.LastSelected = GetSelectionKey(theme, effect);
        IsDirty = true;
        return _store.GetPreset(Database, theme, effect);
    }

    public void CaptureCurrent(ThemePreset currentValues)
    {
        StoreCurrentPreset(currentValues, forceChanged: true);
        Database.LastSelected = GetSelectionKey(CurrentTheme, CurrentEffect);
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
        if (!_store.TryPersistThemeChanges(Database, _changedPresetKeys, Database.LastSelected))
            return false;

        _changedPresetKeys.Clear();
        IsDirty = false;
        return true;
    }

    private void CaptureCurrentIfChanged(ThemePreset currentValues)
    {
        StoreCurrentPreset(currentValues, forceChanged: false);
        Database.LastSelected = GetSelectionKey(CurrentTheme, CurrentEffect);
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
