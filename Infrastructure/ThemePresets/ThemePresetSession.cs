namespace DevProjex.Infrastructure.ThemePresets;

[Flags]
public enum ThemePresetFields
{
    None = 0,
    BackgroundTransparency = 1,
    PanelContrast = 2,
    MenuTransparency = 4,
    BorderVisibility = 8,
    All = BackgroundTransparency | PanelContrast | MenuTransparency | BorderVisibility
}

public sealed class ThemePresetSession
{
    private readonly ThemeSettingsStore _store;
    private readonly HashSet<string> _changedPresetKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ThemePresetFields> _changedPresetFields =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ThemeVariant> _changedEffectThemes = [];
    private readonly bool _startupStoreTemporarilyUnavailable;
    private bool _selectionModeChanged;
    private bool _selectionChanged;

    public ThemePresetSession(
        ThemeSettingsStore store,
        ThemeSettingsDocument database,
        ThemeVariant? systemTheme = null,
        bool startupStoreTemporarilyUnavailable = false)
    {
        _store = store;
        _startupStoreTemporarilyUnavailable = startupStoreTemporarilyUnavailable;
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
            currentValues,
            explicitModeSelection: true,
            explicitEffectSelection: true);
    }

    public ThemePreset SelectMode(
        ThemeSelectionMode mode,
        ThemeVariant? systemTheme,
        ThemePreset currentValues)
    {
        var theme = ThemeSelectionPolicy.ResolveEffectiveTheme(mode, systemTheme);
        return SelectModeAndEffect(
            mode,
            theme,
            GetPreferredEffect(theme),
            currentValues,
            explicitModeSelection: true,
            explicitEffectSelection: false);
    }

    public ThemePreset SelectEffect(
        ThemeEffectMode effect,
        ThemePreset currentValues,
        bool explicitSelection = true)
        => SelectModeAndEffect(
            CurrentMode,
            CurrentTheme,
            effect,
            currentValues,
            explicitModeSelection: false,
            explicitEffectSelection: explicitSelection);

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

    public void MarkDirty() => MarkDirty(ThemePresetFields.All);

    public void MarkDirty(ThemePresetFields fields)
    {
        if (fields == ThemePresetFields.None)
            return;

        var key = GetSelectionKey(CurrentTheme, CurrentEffect);
        _changedPresetKeys.Add(key);
        AddChangedFields(key, fields);
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
                _changedEffectThemes,
                _startupStoreTemporarilyUnavailable ? _changedPresetFields : null,
                updateSelectedPreset: !_startupStoreTemporarilyUnavailable || _selectionChanged,
                refreshDocumentAfterPersist: !_startupStoreTemporarilyUnavailable))
        {
            return false;
        }

        _changedPresetKeys.Clear();
        _changedPresetFields.Clear();
        _changedEffectThemes.Clear();
        _selectionModeChanged = false;
        _selectionChanged = false;
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
            AddChangedFields(
                key,
                forceChanged ? ThemePresetFields.All : GetChangedFields(previous, current));
            IsDirty = true;
        }
    }

    private ThemePreset SelectModeAndEffect(
        ThemeSelectionMode mode,
        ThemeVariant theme,
        ThemeEffectMode effect,
        ThemePreset currentValues,
        bool explicitModeSelection,
        bool explicitEffectSelection)
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
        _selectionChanged |= explicitModeSelection || explicitEffectSelection;
        if (_startupStoreTemporarilyUnavailable ? explicitModeSelection : previousMode != mode)
            _selectionModeChanged = true;
        if (_startupStoreTemporarilyUnavailable ? explicitEffectSelection : previousEffect != effect)
            _changedEffectThemes.Add(theme);
        if (!_startupStoreTemporarilyUnavailable || explicitModeSelection || explicitEffectSelection)
            IsDirty = true;
        return CurrentPreset;
    }

    private void AddChangedFields(string key, ThemePresetFields fields)
    {
        if (!_startupStoreTemporarilyUnavailable || fields == ThemePresetFields.None)
            return;

        _changedPresetFields[key] = _changedPresetFields.GetValueOrDefault(key) | fields;
    }

    private static ThemePresetFields GetChangedFields(ThemePreset previous, ThemePreset current)
    {
        var fields = ThemePresetFields.None;
        if (previous.BackgroundTransparency != current.BackgroundTransparency)
            fields |= ThemePresetFields.BackgroundTransparency;
        if (previous.PanelContrast != current.PanelContrast)
            fields |= ThemePresetFields.PanelContrast;
        if (previous.MenuTransparency != current.MenuTransparency)
            fields |= ThemePresetFields.MenuTransparency;
        if (previous.BorderVisibility != current.BorderVisibility)
            fields |= ThemePresetFields.BorderVisibility;
        return fields;
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
