namespace DevProjex.Infrastructure.ThemePresets;

public sealed class ThemePresetSession
{
    private readonly UserSettingsStore _store;

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
        CaptureCurrent(currentValues);
        CurrentTheme = theme;
        CurrentEffect = effect;
        Database.LastSelected = GetSelectionKey(theme, effect);
        IsDirty = true;
        return _store.GetPreset(Database, theme, effect);
    }

    public void CaptureCurrent(ThemePreset currentValues)
    {
        _store.SetPreset(Database, CurrentTheme, CurrentEffect, currentValues);
        Database.LastSelected = GetSelectionKey(CurrentTheme, CurrentEffect);
        IsDirty = true;
    }

    public void MarkDirty() => IsDirty = true;

    public bool Persist(ThemePreset currentValues)
    {
        if (!IsDirty)
            return true;

        CaptureCurrent(currentValues);
        if (!_store.TrySave(Database))
            return false;

        IsDirty = false;
        return true;
    }

    private static string GetSelectionKey(ThemeVariant theme, ThemeEffectMode effect) => $"{theme}.{effect}";
}
